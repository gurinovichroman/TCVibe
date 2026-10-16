using System.Windows.Input;
using FluentAssertions;
using TCV.App.Commands;
using TCV.App.Settings;

namespace TCV.App.Tests;

/// <summary>
/// The view modes are commands (INV-05): a gesture, a palette row and a help description each. The
/// dispatcher case in MainWindow cannot be unit-tested; the other two places can.
/// </summary>
public class ViewModeCommandTests
{
    [Theory]
    [InlineData("ViewModeBrief", Key.F1, ModifierKeys.Control)]
    [InlineData("ViewModeDetails", Key.F2, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("ViewModeThumbnails", Key.F1, ModifierKeys.Control | ModifierKeys.Shift)]
    [InlineData("ViewModeTree", Key.F8, ModifierKeys.Control)]
    public void ViewMode_HasItsGestureAndAPaletteRow(string commandId, Key key, ModifierKeys modifiers)
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(key, modifiers).Should().Be(commandId);
        CommandCatalog.All.Select(c => c.Id).Should().Contain(commandId);
    }

    [Fact]
    public void CtrlF2_StaysWithTheGadgets()
    {
        // Total Commander's Ctrl+F2 (Details) was already RunGadget's; Details took Ctrl+Shift+F2.
        new HotkeyManager(new AppSettings()).GetCommandId(Key.F2, ModifierKeys.Control).Should().Be("RunGadget");
    }

    [Fact]
    public void ViewModeCommands_DoNotTakeTheHelpOrPaletteKeys()
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(Key.F1, ModifierKeys.None).Should().Be("CommandPalette");
        hotkeys.GetCommandId(Key.F1, ModifierKeys.Shift).Should().Be("Help");
    }

    [Fact]
    public void ViewModeCommands_SitInTheViewCategory()
    {
        CommandCatalog.All
            .Where(c => c.Id.StartsWith("ViewMode", StringComparison.Ordinal))
            .Select(c => c.CategoryEn)
            .Should().HaveCount(4).And.OnlyContain(category => category == "View");
    }
}
