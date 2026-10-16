namespace TCV.Core.PluginSdk;

public interface IToolPlugin : IPlugin
{
    string MenuTitle { get; }
    HotkeyGesture? DefaultHotkey { get; }
    void Execute(IToolContext context);
}
