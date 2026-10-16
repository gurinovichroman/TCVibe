using System.Windows.Input;
using FluentAssertions;
using TCV.App.Controls;
using TCV.App.Settings;

namespace TCV.App.Tests;

/// <summary>Which cursor keys the file list moves the panel's cursor with itself, per view.</summary>
public class CursorKeysTests
{
    [Theory]
    [InlineData(ViewMode.Brief)]
    [InlineData(ViewMode.Thumbnails)]
    public void TryTake_ArrowsInAWrappedView_AreTakenWithAndWithoutShift(ViewMode view)
    {
        foreach (var modifiers in new[] { ModifierKeys.None, ModifierKeys.Shift, ModifierKeys.Control })
        {
            CursorKeys.TryTake(Key.Down, view, modifiers, out var move).Should().BeTrue();
            move.Should().Be(GridMove.Down);
            CursorKeys.TryTake(Key.Left, view, modifiers, out move).Should().BeTrue();
            move.Should().Be(GridMove.Left);
            CursorKeys.TryTake(Key.PageDown, view, modifiers, out move).Should().BeTrue();
            move.Should().Be(GridMove.PageDown);
        }
    }

    [Theory]
    [InlineData(Key.Up)]
    [InlineData(Key.Down)]
    [InlineData(Key.PageUp)]
    [InlineData(Key.PageDown)]
    public void TryTake_PlainVerticalKeysInDetails_StayWithTheGrid(Key key)
    {
        CursorKeys.TryTake(key, ViewMode.Details, ModifierKeys.None, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(Key.Up, GridMove.Up)]
    [InlineData(Key.Down, GridMove.Down)]
    [InlineData(Key.PageUp, GridMove.PageUp)]
    [InlineData(Key.PageDown, GridMove.PageDown)]
    public void TryTake_ShiftVerticalKeysInDetails_MoveTheCursorSoInsertMarksSurvive(Key key, GridMove expected)
    {
        // Left to the grid, Shift+Down became the grid's own range selection and replaced every Insert mark.
        CursorKeys.TryTake(key, ViewMode.Details, ModifierKeys.Shift, out var move).Should().BeTrue();
        move.Should().Be(expected);
        CursorKeys.TryTake(key, ViewMode.Details, ModifierKeys.Shift | ModifierKeys.Control, out move).Should().BeTrue();
        move.Should().Be(expected);
    }

    [Theory]
    [InlineData(Key.Left)]
    [InlineData(Key.Right)]
    public void TryTake_ShiftLeftOrRightInDetails_IsSwallowed(Key key)
    {
        CursorKeys.TryTake(key, ViewMode.Details, ModifierKeys.Shift, out var move).Should().BeTrue();
        move.Should().BeNull();
        CursorKeys.TryTake(key, ViewMode.Details, ModifierKeys.None, out _).Should().BeFalse("plain Left and Right scroll a wide grid");
    }

    [Theory]
    [InlineData(ViewMode.Details)]
    [InlineData(ViewMode.Brief)]
    [InlineData(ViewMode.Thumbnails)]
    public void TryTake_HomeAndEnd_AreTakenInEveryListViewButNotWithCtrl(ViewMode view)
    {
        CursorKeys.TryTake(Key.Home, view, ModifierKeys.Shift, out var move).Should().BeTrue();
        move.Should().Be(GridMove.First);
        CursorKeys.TryTake(Key.End, view, ModifierKeys.None, out move).Should().BeTrue();
        move.Should().Be(GridMove.Last);
        CursorKeys.TryTake(Key.Home, view, ModifierKeys.Control, out _).Should().BeFalse("Ctrl+Home is a command");
    }

    [Fact]
    public void TryTake_InTheTreeOrWithAlt_TakesNothing()
    {
        CursorKeys.TryTake(Key.Down, ViewMode.Tree, ModifierKeys.Shift, out _).Should().BeFalse();
        CursorKeys.TryTake(Key.Down, ViewMode.Brief, ModifierKeys.Alt, out _).Should().BeFalse();
        CursorKeys.TryTake(Key.Enter, ViewMode.Brief, ModifierKeys.None, out _).Should().BeFalse();
    }

    [Fact]
    public void FlowOf_Brief_RunsInColumnsAndTheRestInRows()
    {
        CursorKeys.FlowOf(ViewMode.Brief).Should().Be(WrapFlow.Columns);
        CursorKeys.FlowOf(ViewMode.Thumbnails).Should().Be(WrapFlow.Rows);
        CursorKeys.FlowOf(ViewMode.Details).Should().Be(WrapFlow.Rows);
    }
}
