using System.Windows.Input;

namespace TCV.App.Settings;

/// <summary>
/// How a shortcut reads on screen — in menus and on the F-key bar. The stored form
/// (<see cref="KeyStroke.ToString"/>) uses WPF's key names ("OemQuestion", "Return"), which are
/// right for settings.json and wrong for a person: this turns them into what is printed on the key.
/// </summary>
public static class GestureText
{
    public static string Format(HotkeyBinding? binding) =>
        binding is null
            ? string.Empty
            : binding.Second is { } second
                ? $"{Format(binding.First)} {Format(second)}"
                : Format(binding.First);

    public static string Format(KeyStroke stroke)
    {
        var parts = new List<string>(4);
        if (stroke.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (stroke.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (stroke.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(KeyName(stroke.Key));
        return string.Join("+", parts);
    }

    private static string KeyName(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Delete => "Del",
        Key.Insert => "Ins",
        Key.Prior => "PgUp",
        Key.Next => "PgDn",
        Key.Left => "←",
        Key.Right => "→",
        Key.Up => "↑",
        Key.Down => "↓",
        Key.OemQuestion => "/",
        Key.OemPipe => "\\",
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemTilde => "`",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + ((int)key - (int)Key.NumPad0).ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => key.ToString(),
    };
}
