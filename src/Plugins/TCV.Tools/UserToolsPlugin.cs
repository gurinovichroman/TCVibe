using System.Diagnostics;
using System.Text.Json;
using TCV.Core.PluginSdk;

namespace TCV.Tools;

/// <summary>
/// Tool plugin that loads user-defined external tools from plugin settings and
/// exposes them as individual menu items.  Each tool is registered as a separate
/// <see cref="IToolPlugin"/> instance so the host can build a normal menu.
/// </summary>
public sealed class UserToolsPlugin : IToolPlugin
{
    private const string SettingsKey = "UserTools";

    private ISettingsStore? _store;

    public string Id => "tcv.tools.usertools";
    public string Name => "User Tools";
    public Version Version => new(1, 0, 0);
    public string MenuTitle => "Пользовательские инструменты…";
    public HotkeyGesture? DefaultHotkey => null;

    /// <summary>
    /// The loaded list of user tools. Public so TCV.App can read/write it
    /// when showing the management dialog.
    /// </summary>
    public List<UserTool> Tools { get; private set; } = new();

    public void Initialize(IPluginHost host)
    {
        _store = host.GetSettings(Id);
        LoadTools();
    }

    /// <summary>
    /// Execute is intentionally a no-op stub for the top-level "User Tools…" entry.
    /// TCV.App's MainViewModel opens UserToolsDialog directly.
    /// Individual tools are executed via <see cref="ExecuteTool"/>.
    /// </summary>
    public void Execute(IToolContext context) { }

    /// <summary>Launches the given user tool using the provided context for macro expansion.</summary>
    /// <exception cref="TCV.Core.Domain.UnsafeMacroValueException">The tool runs through
    /// <c>cmd.exe</c> and a path it would receive holds <c>%</c> or <c>!</c>.</exception>
    /// <exception cref="PanelFolderUnavailableException">The tool uses %P or %T and that panel shows
    /// no folder on the disk.</exception>
    public void ExecuteTool(UserTool tool, IToolContext context)
    {
        var command = UserToolsMacroExpander.ExpandRaw(tool.Command, context);
        // A .cmd shim (or a bare name PATH resolves to one) is parsed by cmd.exe, which expands
        // %VAR% even inside quotes; such values are refused instead of guessed at.
        var arguments = UserToolsMacroExpander.Expand(
            tool.Arguments, context, forCmd: TCV.Core.Domain.ShellQuoting.MayRunThroughCmd(command));
        // No folder on the disk (the host does not run tools there, but a context may say so): the
        // process starts in ours rather than in "\pub" on the current drive.
        var workingDir = string.IsNullOrWhiteSpace(tool.WorkingDir)
            ? UserToolsMacroExpander.DiskFolderOf(context.ActiveFileSystem, context.ActivePath) ?? string.Empty
            : UserToolsMacroExpander.ExpandRaw(tool.WorkingDir, context);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = true,
        };

        Process.Start(psi);
    }

    // ── Persistence ──────────────────────────────────────────────────────────

    public void SaveTools()
    {
        if (_store is null) return;
        _store.Set(SettingsKey, Tools);
        _store.Save();
    }

    private void LoadTools()
    {
        if (_store is null) return;
        try
        {
            var loaded = _store.Get<List<UserTool>>(SettingsKey);
            Tools = loaded ?? new List<UserTool>();
        }
        catch
        {
            Tools = new List<UserTool>();
        }
    }
}
