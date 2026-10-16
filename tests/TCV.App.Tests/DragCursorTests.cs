using System.Windows;
using FluentAssertions;
using TCV.App.Controls;
using Xunit;

namespace TCV.App.Tests;

public class DragCursorTests
{
    [Theory]
    [InlineData(DragDropEffects.Copy)]
    [InlineData(DragDropEffects.Move)]
    public void For_CopyOrMove_BuildsACachedCursor(DragDropEffects effect)
    {
        var first = DragCursor.For(effect, 1.0);

        first.Should().NotBeNull();
        DragCursor.For(effect, 1.0).Should().BeSameAs(first);
    }

    [Theory]
    [InlineData(DragDropEffects.None)]
    [InlineData(DragDropEffects.Link)]
    public void For_OtherEffects_KeepsTheSystemCursor(DragDropEffects effect) =>
        DragCursor.For(effect, 1.0).Should().BeNull();
}
