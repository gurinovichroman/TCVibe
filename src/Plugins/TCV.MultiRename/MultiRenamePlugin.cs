using TCV.Core.PluginSdk;

namespace TCV.MultiRename;

/// <summary>
/// Multi-Rename tool plugin.
///
/// The actual WPF dialog (MultiRenameDialog) lives in TCV.App to avoid a circular
/// project dependency.  MainViewModel opens the dialog directly via
/// <c>OpenMultiRenameCommand</c>, so <see cref="Execute"/> is intentionally a no-op
/// stub — the plugin registration is still needed so the hotkey and menu entry appear.
/// </summary>
public class MultiRenamePlugin : IToolPlugin
{
    public string Id => "tcv.multirename";
    public string Name => "Multi-Rename";
    public Version Version => new(1, 0, 0);
    public string MenuTitle => "Массовое переименование";
    public HotkeyGesture? DefaultHotkey => new("M", Ctrl: true);

    public void Initialize(IPluginHost host) { }

    /// <summary>
    /// The dialog is opened by <c>MainViewModel.OpenMultiRenameCommand</c> in TCV.App.
    /// This stub exists so the plugin infrastructure does not throw.
    /// </summary>
    public void Execute(IToolContext context) { }
}
