using TCV.Core.PluginSdk;

namespace TCV.Tools;

/// <summary>Tool plugin that exposes MD5/SHA1/SHA256 checksum computation via the Tools menu.</summary>
public class ChecksumPlugin : IToolPlugin
{
    public string Id => "tcv.tools.checksum";
    public string Name => "Checksum";
    public Version Version => new(1, 0, 0);
    public string MenuTitle => "Контрольные суммы…";
    public HotkeyGesture? DefaultHotkey => null;

    /// <summary>The engine is public so TCV.App can use it directly when opening the dialog.</summary>
    public ChecksumEngine Engine { get; } = new();

    public void Initialize(IPluginHost host) { }

    /// <summary>
    /// Execute is intentionally a no-op stub. TCV.App's MainViewModel opens ChecksumDialog
    /// directly (accessing <see cref="Engine"/>) because WPF dialogs cannot live in TCV.Tools.
    /// </summary>
    public void Execute(IToolContext context) { }
}
