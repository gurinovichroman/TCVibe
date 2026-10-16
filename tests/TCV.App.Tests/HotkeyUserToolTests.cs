using FluentAssertions;
using System.Windows.Input;
using TCV.App.Settings;

namespace TCV.App.Tests;

/// <summary>
/// Gestures declared by the user's own tools (task 6.29): they reach the dispatcher, they never
/// shadow a built-in command, and the hotkey dialog's reload does not drop them.
/// </summary>
public class HotkeyUserToolTests
{
    private const string ToolId = "UserTool:Build";

    // Ctrl+F11 is free by default. It used to be Ctrl+F1, until that became the Brief view (Total
    // Commander's key) — and a tool cannot take a built-in's gesture.
    [Fact]
    public void A_free_gesture_reaches_the_tool()
    {
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "Ctrl+F11")]);

        manager.GetCommandId(Key.F11, ModifierKeys.Control).Should().Be(ToolId);
        manager.GetBinding(ToolId)!.ToString().Should().Be("Ctrl+F11");
    }

    [Fact]
    public void A_tool_cannot_take_a_gesture_a_builtin_already_owns()
    {
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "F5")]);

        manager.GetCommandId(Key.F5, ModifierKeys.None).Should().Be("Copy",
            "a shipped shortcut must not stop working because a tool was named after it");
    }

    [Fact]
    public void A_gesture_the_tool_lost_is_not_advertised()
    {
        // The palette shows the gesture next to the command; showing "F5" for a tool that F5 does
        // not start would be worse than showing nothing.
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "F5")]);

        manager.GetBinding(ToolId).Should().BeNull();
    }

    [Fact]
    public void Chords_work_for_tools_too()
    {
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "Ctrl+K Ctrl+B")]);

        manager.IsChordPrefix(Key.K, ModifierKeys.Control).Should().BeTrue();
        manager.GetChordCommandId(Key.K, ModifierKeys.Control, Key.B, ModifierKeys.Control)
            .Should().Be(ToolId);
    }

    [Fact]
    public void A_tool_chord_cannot_disable_a_builtin_by_claiming_its_key_as_a_prefix()
    {
        // "Ctrl+F Ctrl+B" would make Ctrl+F a chord prefix, and a prefix never fires on its own:
        // Search would stop opening and would eat the next key instead — the exact failure
        // A_tool_cannot_take_a_gesture_a_builtin_already_owns forbids for a single stroke.
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "Ctrl+F Ctrl+B")]);

        manager.IsChordPrefix(Key.F, ModifierKeys.Control).Should().BeFalse();
        manager.GetCommandId(Key.F, ModifierKeys.Control).Should().Be("Search");
        manager.GetChordCommandId(Key.F, ModifierKeys.Control, Key.B, ModifierKeys.Control)
            .Should().BeNull();
        manager.GetBinding(ToolId).Should().BeNull("the palette must not advertise a dead chord");
    }

    [Fact]
    public void A_tool_chord_may_still_hang_off_a_key_that_is_already_a_prefix()
    {
        // Ctrl+K fires nothing on its own, so a tool joining the built-in chords there costs nobody
        // a shortcut — that is the case Chords_work_for_tools_too covers, kept apart from the veto.
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "Ctrl+K Ctrl+B")]);

        manager.GetChordCommandId(Key.K, ModifierKeys.Control, Key.B, ModifierKeys.Control)
            .Should().Be(ToolId);
        manager.GetChordCommandId(Key.K, ModifierKeys.Control, Key.C, ModifierKeys.Control)
            .Should().Be("ColumnSettings", "the built-in chords on the same prefix keep working");
    }

    [Fact]
    public void A_reload_from_the_hotkey_dialog_keeps_the_tools_bound()
    {
        var settings = new AppSettings();
        var manager = new HotkeyManager(settings);
        manager.SetUserToolBindings([(ToolId, "Ctrl+F11")]);

        settings.Hotkeys["Copy"] = "Ctrl+F5";
        manager.Reload();

        manager.GetCommandId(Key.F11, ModifierKeys.Control).Should().Be(ToolId);
    }

    [Fact]
    public void Setting_the_list_again_replaces_it()
    {
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "Ctrl+F11")]);
        manager.SetUserToolBindings([("UserTool:Test", "Ctrl+F4")]);

        manager.GetCommandId(Key.F11, ModifierKeys.Control).Should().BeNull("the tool was removed");
        manager.GetCommandId(Key.F4, ModifierKeys.Control).Should().Be("UserTool:Test");
    }

    [Fact]
    public void An_unparseable_gesture_is_ignored_rather_than_fatal()
    {
        var manager = new HotkeyManager(new AppSettings());
        manager.SetUserToolBindings([(ToolId, "Ctrl+Nonsense"), ("UserTool:Test", "Ctrl+F4")]);

        manager.GetBinding(ToolId).Should().BeNull();
        manager.GetCommandId(Key.F4, ModifierKeys.Control).Should().Be("UserTool:Test");
    }
}
