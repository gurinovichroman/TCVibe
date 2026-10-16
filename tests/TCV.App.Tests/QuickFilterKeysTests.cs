using System.Windows.Input;
using FluentAssertions;
using TCV.App.Controls;

namespace TCV.App.Tests;

/// <summary>Which keys the quick filter box keeps for its text, and which go on to the list's commands.</summary>
public class QuickFilterKeysTests
{
    [Theory]
    [InlineData(Key.W, ModifierKeys.None)]
    [InlineData(Key.W, ModifierKeys.Shift)]
    [InlineData(Key.D7, ModifierKeys.Shift)]
    [InlineData(Key.Q, ModifierKeys.Control | ModifierKeys.Alt)]   // AltGr types (TRAP-07)
    [InlineData(Key.Space, ModifierKeys.None)]
    [InlineData(Key.Multiply, ModifierKeys.None)]                  // "*" from the numpad starts a mask
    [InlineData(Key.Add, ModifierKeys.None)]
    [InlineData(Key.Back, ModifierKeys.None)]
    [InlineData(Key.Delete, ModifierKeys.None)]
    [InlineData(Key.Delete, ModifierKeys.Shift)]
    [InlineData(Key.Left, ModifierKeys.Shift)]
    [InlineData(Key.Home, ModifierKeys.None)]
    [InlineData(Key.End, ModifierKeys.Shift)]
    [InlineData(Key.Escape, ModifierKeys.None)]
    public void BelongsToBox_TypingAndCaretKeys_StayInTheBox(Key key, ModifierKeys modifiers)
    {
        QuickFilterKeys.BelongsToBox(key, modifiers).Should().BeTrue();
    }

    [Theory]
    [InlineData(Key.A)]
    [InlineData(Key.C)]
    [InlineData(Key.X)]
    [InlineData(Key.V)]
    [InlineData(Key.Z)]
    [InlineData(Key.Back)]
    [InlineData(Key.Left)]
    [InlineData(Key.Home)]
    public void BelongsToBox_TheTextBoxOwnCtrlChords_StayInTheBox(Key key)
    {
        QuickFilterKeys.BelongsToBox(key, ModifierKeys.Control).Should().BeTrue();
    }

    [Theory]
    [InlineData(Key.Enter, ModifierKeys.None)]      // opens the file the cursor found
    [InlineData(Key.Enter, ModifierKeys.Alt)]
    [InlineData(Key.F3, ModifierKeys.None)]
    [InlineData(Key.F5, ModifierKeys.None)]
    [InlineData(Key.F8, ModifierKeys.Shift)]
    [InlineData(Key.Tab, ModifierKeys.None)]
    [InlineData(Key.Down, ModifierKeys.None)]
    [InlineData(Key.PageUp, ModifierKeys.Shift)]
    [InlineData(Key.C, ModifierKeys.Control | ModifierKeys.Shift)]   // copy full path
    [InlineData(Key.R, ModifierKeys.Control)]
    [InlineData(Key.F, ModifierKeys.Alt)]
    public void BelongsToBox_KeysThatActOnTheList_GoToTheCommands(Key key, ModifierKeys modifiers)
    {
        QuickFilterKeys.BelongsToBox(key, modifiers).Should().BeFalse();
    }
}
