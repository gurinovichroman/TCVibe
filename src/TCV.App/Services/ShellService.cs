using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Extensions.Logging;
using TCV.App.Navigation;
using TCV.App.Settings;
using TCV.App.Shells;

namespace TCV.App.Services;

/// <summary>
/// The shells the command line runs a typed command in: the configured list or, while it is empty,
/// the ones installed here (<see cref="ShellCatalog"/>); the one the user picked; and starting a
/// command in it.
/// </summary>
/// <remarks>
/// Detection probes the disk and <c>PATH</c>, which may hold a dead network share, and asks wsl.exe for
/// its distributions, so it runs in the pool and is kept for the session (INV-06). «Detect» in the
/// settings looks again.
/// </remarks>
public sealed class ShellService
{
    private readonly AppSettings _settings;
    private readonly SettingsSerializer _serializer;
    private readonly TerminalService _terminal;
    private readonly ILogger<ShellService> _logger;
    private readonly object _gate = new();
    private Task<IReadOnlyList<ShellProfile>>? _detected;

    public ShellService(
        AppSettings settings,
        SettingsSerializer serializer,
        TerminalService terminal,
        ILogger<ShellService> logger)
    {
        _settings = settings;
        _serializer = serializer;
        _terminal = terminal;
        _logger = logger;
    }

    /// <summary>The shells to offer: the configured ones, else those found on this machine.</summary>
    public async Task<IReadOnlyList<ShellProfile>> AvailableAsync()
    {
        var configured = _settings.Shells.Profiles;
        if (configured.Count > 0)
        {
            return configured.ToList();
        }

        Task<IReadOnlyList<ShellProfile>> detection;
        lock (_gate)
        {
            detection = _detected ??= Task.Run(DetectOrFallback);
        }

        return await detection.ConfigureAwait(false);
    }

    /// <summary>The shell the command line uses now.</summary>
    public async Task<ShellProfile> CurrentAsync() =>
        ShellCatalog.Pick(await AvailableAsync().ConfigureAwait(false), _settings.Shells.Selected);

    /// <summary>The name of the shell picked last, before the list is known: cmd when none was.</summary>
    public string SelectedName =>
        _settings.Shells.Selected is { Length: > 0 } name ? name : ShellCatalog.Fallback.Name;

    /// <summary>
    /// Remembers the shell picked in the command line. A guest window keeps it for its session: its
    /// serializer does not write (TRAP-22).
    /// </summary>
    public void Select(string name)
    {
        if (string.Equals(_settings.Shells.Selected, name, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Shells.Selected = name;
        try
        {
            _serializer.Save(_settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not persist the command line's shell");
        }
    }

    /// <summary>
    /// Looks at the disk again («Detect» in the settings) and keeps the answer for the command line.
    /// Call it off the UI thread.
    /// </summary>
    public IReadOnlyList<ShellProfile> DetectAgain()
    {
        var shells = Detect();
        lock (_gate)
        {
            _detected = Task.FromResult(shells);
        }

        return shells;
    }

    // A failed look is not kept for the session: the command line would throw on every Enter after it.
    private static IReadOnlyList<ShellProfile> DetectOrFallback()
    {
        try
        {
            return Detect();
        }
        catch (Exception)
        {
            return [ShellCatalog.Fallback];
        }
    }

    /// <summary>The catalog's shells that are installed here, cmd always among them, then the WSL distributions.</summary>
    private static IReadOnlyList<ShellProfile> Detect()
    {
        var found = new List<ShellProfile>();
        foreach (var template in ShellCatalog.All)
        {
            if (Locate(template) is { } command)
            {
                found.Add(template.ToSettings(command));
            }
        }

        if (!found.Any(s => s.Name == ShellCatalog.Fallback.Name))
        {
            found.Insert(0, ShellCatalog.Fallback);
        }

        found.AddRange(DetectWslDistributions());
        return found;
    }

    /// <summary>
    /// One shell per WSL distribution, from <c>wsl --list --quiet</c>, as VS Code finds them. Asked only
    /// of the WSL that <c>wsl --install</c> puts into Program Files, never of the System32 stub, which
    /// offers to install WSL. A WSL that does not answer in a few seconds adds nothing.
    /// </summary>
    private static IEnumerable<ShellProfile> DetectWslDistributions()
    {
        var program = Environment.ExpandEnvironmentVariables(ShellCatalog.WslProgram);
        if (!File.Exists(program))
        {
            return [];
        }

        try
        {
            var start = new ProcessStartInfo(program, "--list --quiet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // Without it wsl.exe writes UTF-16 into a pipe.
                StandardOutputEncoding = Encoding.UTF8,
            };
            start.Environment["WSL_UTF8"] = "1";

            using var process = Process.Start(start);
            if (process is null)
            {
                return [];
            }

            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return [];
            }

            // No distribution at all is an exit code with a sentence in the output, not a list.
            return process.ExitCode == 0
                ? ShellCatalog.WslDistributions(output.Result).Select(d => ShellCatalog.ForWslDistribution(ShellCatalog.WslProgram, d)).ToList()
                : [];
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// Runs <paramref name="command"/> in <paramref name="shell"/> in a terminal window in
    /// <paramref name="folder"/>. False when nothing could be started.
    /// </summary>
    public bool Run(ShellProfile shell, string folder, string command)
    {
        var program = Environment.ExpandEnvironmentVariables(shell.Command.Trim().Trim('"'));
        if (program.Length == 0)
        {
            _logger.LogWarning("Shell {Shell} has no program", shell.Name);
            return false;
        }

        var launch = ShellCommandLine.Build(program, shell.Arguments, command, folder);
        return _terminal.OpenShell(launch, folder);
    }

    /// <summary>The command as found here — the bare name on PATH, else the probe path as written — or null.</summary>
    private static string? Locate(ShellTemplate template)
    {
        if (template.Command.Length > 0
            && CommandResolver.Resolve(template.Command, PathDirectories(), PathExtensions(), File.Exists) is not null)
        {
            return template.Command;
        }

        return template.ProbePaths.FirstOrDefault(p => File.Exists(Environment.ExpandEnvironmentVariables(p)));
    }

    private static IEnumerable<string> PathDirectories() =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

    private static IEnumerable<string> PathExtensions() =>
        (Environment.GetEnvironmentVariable("PATHEXT") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}
