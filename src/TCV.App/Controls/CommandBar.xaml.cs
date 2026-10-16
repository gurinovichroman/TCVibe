using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using TCV.App.Localization;
using TCV.App.Settings;

namespace TCV.App.Controls;

/// <summary>
/// The F-key bar at the bottom of the window. Every button runs its command through the same
/// dispatcher as the keyboard (<see cref="MainWindow.RunCommand"/>), so the bar, the menu and the
/// hotkeys cannot drift apart (INV-05).
/// </summary>
public partial class CommandBar : UserControl
{
    public CommandBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshLabels(_hotkeys);
            LocalizationManager.LanguageChanged += OnLanguageChanged;
        };
        Unloaded += (_, _) => LocalizationManager.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLabels(_hotkeys);

    private HotkeyManager? _hotkeys;

    /// <summary>Rewrites every button's text from the current bindings and language.</summary>
    public void Refresh(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        RefreshLabels(hotkeys);
    }

    private void RefreshLabels(HotkeyManager? hotkeys)
    {
        foreach (var button in Buttons.Children.OfType<Button>())
        {
            var id = (string)button.Tag;
            var label = LocalizationManager.Get("Bar_" + id);
            var gesture = hotkeys is null ? DefaultKey(id) : GestureText.Format(hotkeys.GetBinding(id));

            button.Content = BuildContent(gesture, label);
            AutomationProperties.SetName(button, label);
            button.ToolTip = gesture.Length > 0 ? $"{label} ({gesture})" : label;
        }
    }

    /// <summary>The key in the mono face and the text colour (the design's kbd), then the label; a long label is cut, not wrapped.</summary>
    private static TextBlock BuildContent(string gesture, string label)
    {
        var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };

        if (gesture.Length > 0)
        {
            var key = new Run(gesture) { FontSize = 10 };
            key.SetResourceReference(TextElement.FontFamilyProperty, "MonoFontFamily");
            key.SetResourceReference(TextElement.ForegroundProperty, "ForegroundBrush");
            text.Inlines.Add(key);
            text.Inlines.Add(new Run("  "));
        }

        text.Inlines.Add(new Run(label));
        return text;
    }

    // Before the first Refresh (the bar is built before the window hands over the bindings) the
    // classic F-key row is shown; the real bindings replace it immediately.
    private static string DefaultKey(string commandId) => commandId switch
    {
        "View" => "F3",
        "Edit" => "F4",
        "Copy" => "F5",
        "Move" => "F6",
        "NewFolder" => "F7",
        "Delete" => "F8",
        "Terminal" => "F9",
        "Quit" => "F10",
        _ => string.Empty,
    };

    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } && Window.GetWindow(this) is MainWindow window)
        {
            window.RunCommand(id);
        }
    }
}
