using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using TCV.App.FileSystem;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.PluginHost;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Git;

namespace TCV.App;

public partial class App : Application
{
    private IHost? _host;
    private StartupOptions _startupOptions = StartupOptions.Default;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureLogging();
        InstallCrashHandlers();

        // async void: an exception escaping here would tear the process down without a word, and
        // the log is the only thing that could explain it.
        try
        {
            await StartAsync(e);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Startup failed");
            ReportCrash(ex, fatal: true);
            Shutdown(1);
        }
    }

    private async Task StartAsync(StartupEventArgs e)
    {

        // Parsed before the host is built so the DI graph can hand the same options to MainViewModel.
        _startupOptions = StartupOptions.Parse(e.Args);
        if (_startupOptions.WorkspaceName is { } requested)
            Log.Information("Opening workspace {Workspace} in a secondary window", requested);

        _host = CreateHostBuilder(_startupOptions).Build();
        await _host.StartAsync();

        Log.Information("TCVibe starting up");
        if (PortableLayout.CustomDataDirectory is { } customData)
        {
            // The layout root still says which copy runs (and installs updates) on that state.
            Log.Information("State in {Data} (--data); portable layout at {Root}", customData, PortableLayout.Root ?? "none");
        }
        else if (PortableLayout.IsVelopack)
        {
            Log.Information("Portable layout at {Root}; state in {Data}", PortableLayout.Root, AppPaths.DataDirectory);
        }

        if (PortableLayout.MigratedEntries > 0)
        {
            Log.Information("Copied {Count} file(s) and folder(s) of an older copy into {Data}",
                PortableLayout.MigratedEntries, AppPaths.DataDirectory);
        }

        // Initialize localization and theme from saved settings
        var settings = _host.Services.GetRequiredService<AppSettings>();
        LocalizationManager.Initialize(string.IsNullOrEmpty(settings.Theme.Language)
            ? null
            : settings.Theme.Language);
        LocalizationManager.UseHotkeys(_host.Services.GetRequiredService<HotkeyManager>());

        var themeManager = _host.Services.GetRequiredService<ThemeManager>();
        themeManager.ApplyFromSettings();

        // Scratch copies (files opened from archives and servers) left by a run that did not exit cleanly.
        // A day: another window of the program is a separate process and may still be showing recent ones.
        TemporaryFiles.SweepOlderThan(TimeSpan.FromDays(1));

        // From here on, SFTP host keys and FTPS certificates are checked against known-hosts.json
        Network.NetworkSecurity.Default = _host.Services.GetRequiredService<Network.HostTrustPolicy>();

        // Initialize PluginHost — built-in first, then external
        var pluginHost = _host.Services.GetRequiredService<PluginHost.PluginHost>();
        pluginHost.LoadBuiltinPlugins();
        var pluginsDir = AppPaths.Data("plugins");
        pluginHost.ScanExternalPlugins(pluginsDir);

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();

        // A damaged settings.json was replaced by defaults; say so once the window is up to show it.
        var serializer = _host.Services.GetRequiredService<SettingsSerializer>();
        if (serializer.LoadWarning is { } settingsProblem)
        {
            Log.Warning("Settings problem at startup: {Problem}", settingsProblem);
            MessageBox.Show(
                string.Format(LocalizationManager.Get(
                    serializer.RestoredFromBackup ? "Settings_LoadRestored" : "Settings_LoadProblem"), settingsProblem),
                LocalizationManager.Get("Settings_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        StartMcpServerIfEnabled(settings);

        // A downloaded update may not have gone in: the install stopped before the hand-over, or the
        // updater gave up on files another program held (ADR-0031).
        if (mainWindow.DataContext is MainViewModel shell)
        {
            _ = shell.Services.ReportUpdateNotInstalledAsync();
        }
    }

    /// <summary>
    /// Brings the MCP server up when the user has enabled it. After the window, because the server's
    /// first job is answering questions about the panels.
    /// </summary>
    /// <remarks>
    /// A secondary workspace window (<c>--workspace</c>) never starts one. It is a separate process
    /// that deliberately does not own the session (TRAP-22), and two processes publishing
    /// <c>mcp-endpoint.json</c> would leave a client connected to whichever wrote last — pointing at
    /// panels the user is not looking at.
    /// </remarks>
    private void StartMcpServerIfEnabled(Settings.AppSettings settings)
    {
        if (_host is null) return;

        if (_startupOptions.IsSecondaryWindow)
        {
            Log.Information("Secondary workspace window — the MCP server is not started here");
            return;
        }

        if (!settings.Agent.McpEnabled || !settings.Agent.McpAutoStart) return;

        var mcp = _host.Services.GetRequiredService<Agents.McpServerHost>();
        if (mcp.Start())
            Log.Information("MCP server started at {Url}", mcp.Endpoint?.Url);
        else
            Log.Warning("MCP server did not start: {Error}", mcp.LastError);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Information("TCVibe shutting down");

        if (_host is not null)
        {
            // Synchronous on purpose. This used to be `async void`: WPF stops pumping the dispatcher
            // as soon as OnExit returns to it, so everything after the first real await — closing the
            // listener's accept loop, deleting mcp-endpoint.json, stopping the host — could simply
            // never run. Off the dispatcher via Task.Run, because the stop awaits with the dispatcher's
            // synchronisation context and blocking this thread on it would deadlock. Bounded, so a
            // stuck request cannot hold the process open.
            //
            // Stopped explicitly, before the container goes: the server holds a listening socket and
            // publishes mcp-endpoint.json, and both have to be gone by the time the process is.
            var host = _host;
            try
            {
                var stopped = Task.Run(async () =>
                {
                    await Task.WhenAll(
                        host.Services.GetRequiredService<Agents.McpServerHost>().StopAsync(),
                        CancelTransfersAsync(host.Services));
                    await host.StopAsync();
                }).Wait(TimeSpan.FromSeconds(8));

                if (!stopped) Log.Warning("Shutdown did not finish in time; leaving anyway");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Shutdown failed");
            }
            finally
            {
                // Whatever happened above, the endpoint file this process wrote must not outlive it.
                TCV.Ai.Mcp.McpEndpointFile.DeleteIfOwnedBy(Environment.ProcessId);
                host.Dispose();
            }
        }

        TemporaryFiles.DeleteCreatedThisRun();

        // Last, so the lines above are in the file.
        Log.CloseAndFlush();
        base.OnExit(e);
    }

    /// <summary>
    /// Ends every background transfer that has not ended — waiting, running or paused (6.61). The queue
    /// lives in memory, so none of them can continue after the process: each is cancelled, its partial
    /// file removed, and its run written to the operation log (INV-10, ADR-0025), instead of being cut
    /// off by the process ending with a partial file and no line. Bounded: removing an upload's partial
    /// file needs the server, which may not answer.
    /// </summary>
    private static async Task CancelTransfersAsync(IServiceProvider services)
    {
        try
        {
            await services.GetRequiredService<TCV.Network.TransferQueue>().CancelAllAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            Log.Warning("Cancelling background transfers did not finish in time");
        }
        catch (Exception ex)
        {
            // The host still has to stop after this.
            Log.Warning(ex, "Cancelling background transfers failed");
        }
    }

    // ── Crash handling ────────────────────────────────────────────────────────

    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Unhandled exception on the UI thread");
            ReportCrash(args.Exception, fatal: false);

            // A command that threw should not end the session: the window's state is still good.
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception ?? new InvalidOperationException(args.ExceptionObject?.ToString());
            Log.Fatal(ex, "Unhandled exception; terminating={Terminating}", args.IsTerminating);
            Log.CloseAndFlush();
            if (args.IsTerminating)
            {
                ReportCrash(ex, fatal: true);
            }
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // A forgotten fire-and-forget task failed. Nothing to show — but it must not vanish.
            Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
    }

    /// <summary>Tells the user something went wrong without dumping a stack trace on them; the log has it.</summary>
    private static void ReportCrash(Exception ex, bool fatal)
    {
        try
        {
            var message = string.Format(
                Localization.LocalizationManager.Get(fatal ? "Crash_FatalMessage" : "Crash_Message"),
                ex.Message, AppPaths.Data("logs"));
            MessageBox.Show(message, Localization.LocalizationManager.Get("Crash_Title"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception)
        {
            // Nothing left to do: the exception is already in the log.
        }
    }

    private static void ConfigureLogging()
    {
        var logPath = AppPaths.Data("logs", "tcv-.log");

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                logPath,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
    }

    private static IHostBuilder CreateHostBuilder(StartupOptions startup) =>
        Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureServices((context, services) => ConfigureServices(context, services, startup));

    private static void ConfigureServices(
        HostBuilderContext context, IServiceCollection services, StartupOptions startup)
    {
        // Command line (which workspace to open, whether this window owns the session)
        services.AddSingleton(startup);

        // File System
        services.AddSingleton<WindowsFileSystem>();
        services.AddSingleton<DriveManager>();
        services.AddSingleton<FileWatcher>();

        // Plugin Host
        services.AddSingleton<PluginLoader>();
        services.AddSingleton<PluginHost.PluginHost>();

        // Operations
        services.AddSingleton<OperationLog>();
        services.AddSingleton<OperationQueue>();

        // Settings
        // A guest window does not own settings.json (TRAP-22): its serializer never writes.
        // Asked per write: a guest whose workspace is gone is downgraded later (DowngradeToPrimary).
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<StartupOptions>();
            return new SettingsSerializer(sp.GetRequiredService<ILogger<SettingsSerializer>>())
            {
                ReadOnlyWhen = () => options.IsSecondaryWindow,
            };
        });
        services.AddSingleton(sp => sp.GetRequiredService<SettingsSerializer>().Load());

        // HotkeyManager
        services.AddSingleton<HotkeyManager>();

        // Theme
        services.AddSingleton<ThemeManager>();

        // Terminal, and the shells the command line runs a command in
        services.AddSingleton<TerminalService>();
        services.AddSingleton<ShellService>();

        // Editors and agent CLIs (Ctrl+E / Ctrl+Shift+A)
        services.AddSingleton<ExternalToolService>();
        services.AddSingleton<FileEditorLauncher>();

        // Instant name index behind the whole-disk search (6.21)
        services.AddSingleton<Navigation.NameIndexService>();

        // Saved operations and the drop stack (6.22 / 6.24)
        services.AddSingleton<SavedOperationStore>();
        services.AddSingleton<DropStack>();

        // Editing a file that is not on a reachable disk, through a temp copy (6.23)
        services.AddSingleton<RemoteEditService>();

        // Update
        services.AddSingleton<UpdateService>();

        // Git — the read side (status column, Ctrl+G diff) and the write side ("git as a process":
        // worktrees, commit/branch/stash, clone), bundled for MainViewModel by GitWorkflowServices.
        services.AddSingleton<TCV.Git.GitStatusProvider>();
        services.AddSingleton<TCV.Git.GitWorktreeService>();
        services.AddSingleton<TCV.Git.GitWriteService>();
        services.AddSingleton<TCV.Git.GitCloneService>();
        services.AddSingleton<WorktreeSetupService>();
        services.AddSingleton<GitWorkflowServices>();

        // Agent-aware layer (6D) — MCP server, supervised writes, LLM client, semantic index.
        // Nothing here opens a socket or contacts a network until the user turns it on (ADR-0008).
        services.AddSingleton<TCV.Ai.Agents.AgentSessionStore>();
        services.AddSingleton<Agents.AgentWriteGate>();
        services.AddSingleton<TCV.Ai.Agents.IAgentWriteGate>(
            sp => sp.GetRequiredService<Agents.AgentWriteGate>());
        services.AddSingleton<Agents.AgentBridge>();
        services.AddSingleton<Agents.McpServerHost>();
        services.AddSingleton(sp => new TCV.Ai.Llm.LlmClient(
            () => ToLlmOptions(sp.GetRequiredService<AppSettings>()),
            sp.GetRequiredService<ILogger<TCV.Ai.Llm.LlmClient>>()));
        services.AddSingleton(sp => new TCV.Ai.Semantic.VectorStore(
            null, sp.GetRequiredService<ILogger<TCV.Ai.Semantic.VectorStore>>()));
        services.AddSingleton<TCV.Ai.Semantic.SemanticIndexService>();
        services.AddSingleton<AgentServices>();

        // The rest of the application-wide singletons, bundled for the window's shell (6.26).
        services.AddSingleton<ShellServices>();

        // Dialogs
        services.AddSingleton<IDialogService, DialogService>();

        // Clipboard Buffer
        services.AddSingleton<IClipboardBuffer, ClipboardBufferService>();

        // Network — connection book and the background transfer queue (disposed with the host)
        services.AddSingleton<TCV.Network.ConnectionBook>();
        services.AddSingleton<TCV.Network.KnownHostsStore>();
        services.AddSingleton(sp => new TCV.Network.HostTrustPolicy(
            sp.GetRequiredService<TCV.Network.KnownHostsStore>(),
            request => HostTrustPrompt.AskAsync(sp.GetRequiredService<IDialogService>(), request)));
        // The queue's list is bound to a window, so it is told which thread owns it. It runs beside the
        // OperationQueue, not through it, so its transfers reach the operation log here (INV-10).
        services.AddSingleton(sp =>
        {
            var queue = new TCV.Network.TransferQueue(
                System.Threading.SynchronizationContext.Current,
                sp.GetRequiredService<TCV.Network.HostTrustPolicy>(),
                NetworkErrorText.Describe);
            RemoteTransferLog.Attach(queue, sp.GetRequiredService<OperationLog>());
            return queue;
        });

        // ViewModels — panels (Transient so each panel gets its own instances)
        services.AddTransient<FileListViewModel>(sp => new FileListViewModel(
            sp.GetRequiredService<WindowsFileSystem>(),
            sp.GetRequiredService<ILogger<FileListViewModel>>()));

        services.AddTransient<DriveBarViewModel>();
        services.AddTransient<StatusBarViewModel>();

        services.AddTransient<PanelViewModel>(sp => new PanelViewModel(
            "temp",
            sp.GetRequiredService<FileListViewModel>(),
            sp.GetRequiredService<DriveBarViewModel>(),
            sp.GetRequiredService<StatusBarViewModel>(),
            sp.GetRequiredService<AppSettings>(),
            new FileWatcher())); // each panel gets its own watcher

        // MainViewModel — needs two distinct panels
        services.AddTransient<MainViewModel>(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var fs = sp.GetRequiredService<WindowsFileSystem>();
            var drives = sp.GetRequiredService<DriveManager>();

            PanelViewModel CreatePanel(string name) => new(
                name,
                new FileListViewModel(fs, sp.GetRequiredService<ILogger<FileListViewModel>>()),
                new DriveBarViewModel(drives),
                new StatusBarViewModel(drives),
                settings,
                new FileWatcher());

            return new MainViewModel(
                CreatePanel("Left"),
                CreatePanel("Right"),
                sp.GetRequiredService<ShellServices>(),
                sp.GetRequiredService<GitStatusProvider>(),
                sp.GetRequiredService<GitWorkflowServices>(),
                sp.GetRequiredService<AgentServices>(),
                sp.GetRequiredService<StartupOptions>());
        });

        // Views
        services.AddTransient<MainWindow>();
    }

    /// <summary>
    /// Projects the settings onto the LLM client's options (ADR-0007). Read per call by the client, so
    /// editing the endpoint in settings takes effect without a restart.
    /// </summary>
    private static TCV.Ai.Llm.LlmOptions ToLlmOptions(AppSettings settings) => new(
        settings.Agent.LlmEndpoint,
        settings.Agent.LlmApiKey,
        settings.Agent.LlmModel,
        settings.Agent.EmbeddingModel,
        settings.Agent.LlmMaxTokens,
        Temperature: 0.2,
        TimeoutSeconds: settings.Agent.LlmTimeoutSeconds);
}
