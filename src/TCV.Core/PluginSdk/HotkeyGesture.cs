namespace TCV.Core.PluginSdk;

/// <summary>Platform-agnostic hotkey definition for plugin default shortcuts.</summary>
public sealed record HotkeyGesture(string Key, bool Ctrl = false, bool Alt = false, bool Shift = false)
{
    public override string ToString()
    {
        var parts = new List<string>();
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        parts.Add(Key);
        return string.Join("+", parts);
    }
}
