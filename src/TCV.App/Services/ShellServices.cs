using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.PluginHost;
using TCV.App.Settings;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>
/// The application-wide singletons the window and its command facades share, handed around as one
/// dependency (task 6.26). Same bargain as <see cref="GitWorkflowServices"/> and
/// <see cref="AgentServices"/>: the services are exposed as they are rather than wrapped, so
/// nothing here hides logic — it only stops <c>MainViewModel</c>'s constructor from listing every
/// service in the application ([TRAP-17](../../../CLAUDE.md)).
/// </summary>
/// <remarks>
/// The facades do <b>not</b> take this bundle: each one lists the handful of services it actually
/// uses, which is what makes «what does this command group touch» answerable from its constructor.
/// The bundle exists for the shell, which genuinely is the wiring point for all of them.
/// </remarks>
public sealed class ShellServices
{
    public ShellServices(
        AppSettings settings,
        SettingsSerializer serializer,
        IDialogService dialogs,
        WindowsFileSystem fileSystem,
        OperationQueue operations,
        OperationLog operationLog,
        HotkeyManager hotkeys,
        ThemeManager themes,
        PluginHost.PluginHost plugins,
        PluginLoader pluginLoader,
        TerminalService terminal,
        ShellService shells,
        ExternalToolService externalTools,
        UpdateService updates,
        IClipboardBuffer clipboard,
        ConnectionBook connections,
        TransferQueue transfers,
        Navigation.NameIndexService nameIndex,
        SavedOperationStore savedOperations,
        DropStack dropStack,
        RemoteEditService remoteEdit,
        FileEditorLauncher fileEditor,
        ILogger<ViewModels.MainViewModel> logger,
        ILogger<CopyOperation> copyLogger,
        ILogger<MoveOperation> moveLogger,
        ILogger<DeleteOperation> deleteLogger,
        ILogger<CreateDirectoryOperation> mkdirLogger)
    {
        Settings = settings;
        Serializer = serializer;
        Dialogs = dialogs;
        FileSystem = fileSystem;
        Operations = operations;
        OperationLog = operationLog;
        Hotkeys = hotkeys;
        Themes = themes;
        Plugins = plugins;
        PluginLoader = pluginLoader;
        Terminal = terminal;
        Shells = shells;
        ExternalTools = externalTools;
        Updates = updates;
        Clipboard = clipboard;
        Connections = connections;
        Transfers = transfers;
        NameIndex = nameIndex;
        SavedOperations = savedOperations;
        DropStack = dropStack;
        RemoteEdit = remoteEdit;
        FileEditor = fileEditor;
        Logger = logger;
        CopyLogger = copyLogger;
        MoveLogger = moveLogger;
        DeleteLogger = deleteLogger;
        MkdirLogger = mkdirLogger;
    }

    public AppSettings Settings { get; }
    public SettingsSerializer Serializer { get; }
    public IDialogService Dialogs { get; }
    public WindowsFileSystem FileSystem { get; }

    /// <summary>Every file-system mutation goes through this queue and its log (INV-10).</summary>
    public OperationQueue Operations { get; }
    public OperationLog OperationLog { get; }

    public HotkeyManager Hotkeys { get; }

    /// <summary>Needed so an imported settings bundle can repaint without a restart (6.20).</summary>
    public ThemeManager Themes { get; }

    public PluginHost.PluginHost Plugins { get; }
    public PluginLoader PluginLoader { get; }
    public TerminalService Terminal { get; }

    /// <summary>The shells the command line runs a typed command in (cmd, PowerShell, Git Bash, WSL …).</summary>
    public ShellService Shells { get; }
    public ExternalToolService ExternalTools { get; }
    public UpdateService Updates { get; }
    public IClipboardBuffer Clipboard { get; }

    /// <summary>Saved FTP/SFTP/WebDAV connections (portable since 6.27).</summary>
    public ConnectionBook Connections { get; }
    public TransferQueue Transfers { get; }

    /// <summary>Instant name index behind the whole-disk search (6.21).</summary>
    public Navigation.NameIndexService NameIndex { get; }

    /// <summary>Named, replayable operations (6.22).</summary>
    public SavedOperationStore SavedOperations { get; }

    /// <summary>Files gathered across directories, awaiting one copy or move (6.24).</summary>
    public DropStack DropStack { get; }

    /// <summary>Round-trips a file that is not on a reachable disk through a temp copy (6.23).</summary>
    public RemoteEditService RemoteEdit { get; }

    /// <summary>F4's editor: the one chosen in the settings, else the Windows association.</summary>
    public FileEditorLauncher FileEditor { get; }

    public ILogger<ViewModels.MainViewModel> Logger { get; }
    public ILogger<CopyOperation> CopyLogger { get; }
    public ILogger<MoveOperation> MoveLogger { get; }
    public ILogger<DeleteOperation> DeleteLogger { get; }
    public ILogger<CreateDirectoryOperation> MkdirLogger { get; }
}
