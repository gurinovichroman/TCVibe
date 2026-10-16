using System.Windows;
using System.Windows.Controls;
using TCV.App.Settings;

namespace TCV.App.Controls;

/// <summary>
/// Keeps the shortcut text in the menus (and on the F-key bar) equal to the shortcut that is really
/// bound. A menu item names its command with <see cref="CommandIdProperty"/>; the text is then read
/// from <see cref="HotkeyManager"/>, so a rebind in the hotkey dialog shows up in the menu instead of
/// leaving a hard-coded "Ctrl+D" advertising a key that no longer does that (INV-05).
/// </summary>
public static class MenuGestures
{
    public static readonly DependencyProperty CommandIdProperty = DependencyProperty.RegisterAttached(
        "CommandId", typeof(string), typeof(MenuGestures), new PropertyMetadata(null));

    public static string? GetCommandId(DependencyObject element) => (string?)element.GetValue(CommandIdProperty);

    public static void SetCommandId(DependencyObject element, string? value) => element.SetValue(CommandIdProperty, value);

    /// <summary>Refreshes every item under <paramref name="root"/> that names a command.</summary>
    public static void Refresh(ItemsControl root, HotkeyManager hotkeys)
    {
        foreach (var item in root.Items.OfType<MenuItem>())
        {
            if (GetCommandId(item) is { } id)
            {
                item.InputGestureText = GestureText.Format(hotkeys.GetBinding(id));
            }

            if (item.HasItems)
            {
                Refresh(item, hotkeys);
            }
        }
    }
}
