using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>Home / End, the Brief and Thumbnails arrows, Shift+arrows in Details: the panel's cursor, and Shift marking the way.</summary>
public class CursorMoveTests
{
    private static async Task<(FileListViewModel List, List<FileEntryViewModel> Rows)> CreateWithFiles(
        bool withParent, params string[] names)
    {
        var fs = new InMemoryFileSystem("file");
        foreach (var n in names)
        {
            fs.AddFile($"/C:/work/{n}", [1]);
        }

        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);
        var rows = new List<FileEntryViewModel>();
        if (withParent)
        {
            var parent = FileEntryViewModel.CreateParent();
            rows.Add(parent);
            list.Files.Add(parent);
        }

        foreach (var n in names)
        {
            var entry = await fs.GetEntryAsync(new VfsPath($"/C:/work/{n}"));
            var vm = new FileEntryViewModel(entry!);
            rows.Add(vm);
            list.Files.Add(vm);
        }

        return (list, rows);
    }

    [Fact]
    public async Task MoveCursorTo_WithoutShift_MovesTheCursorAndLeavesTheMarks()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d");
        list.SelectedItem = rows[1];
        list.ToggleSelect(rows[0]);

        list.MoveCursorTo(3, extendMarks: false);

        list.SelectedItem.Should().BeSameAs(rows[3]);
        list.SelectedFiles.Should().ContainSingle().Which.Should().BeSameAs(rows[0]);
    }

    [Fact]
    public async Task MoveCursorTo_ShiftEnd_MarksFromTheCursorToTheLastRow()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d");
        list.SelectedItem = rows[1];

        list.MoveCursorTo(rows.Count - 1, extendMarks: true);

        list.SelectedItem.Should().BeSameAs(rows[3]);
        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[1], rows[2], rows[3] });
        rows[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task MoveCursorTo_ShiftHome_NeverMarksTheParentEntry()
    {
        var (list, rows) = await CreateWithFiles(true, "a", "b");
        list.SelectedItem = rows[2];

        list.MoveCursorTo(0, extendMarks: true);

        list.SelectedItem.Should().BeSameAs(rows[0], "the cursor may stand on \"..\"");
        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[1], rows[2] });
        rows[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task MoveCursorTo_ShiftBackwards_UnmarksOnlyWhatTheRunAdded()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.ToggleSelect(rows[4]);   // marked with Insert before the run
        list.SelectedItem = rows[0];

        list.MoveCursorTo(3, extendMarks: true);
        list.MoveCursorTo(1, extendMarks: true);

        list.SelectedItem.Should().BeSameAs(rows[1]);
        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[1], rows[4] });
    }

    [Fact]
    public async Task MoveCursorTo_ShiftPastTheAnchor_FlipsTheRangeToTheOtherSide()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectedItem = rows[2];

        list.MoveCursorTo(4, extendMarks: true);
        list.MoveCursorTo(0, extendMarks: true);

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[1], rows[2] });
    }

    [Fact]
    public async Task MoveCursorTo_AfterTheCursorMovedOtherwise_StartsANewRun()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectedItem = rows[0];
        list.MoveCursorTo(1, extendMarks: true);   // marks a, b

        list.SelectedItem = rows[3];                // an arrow key or a click
        list.MoveCursorTo(4, extendMarks: true);   // a new run from d, on top of a and b

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[1], rows[3], rows[4] });
    }

    [Fact]
    public async Task MoveCursorTo_APlainMove_EndsTheRun()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d");
        list.SelectedItem = rows[0];
        list.MoveCursorTo(1, extendMarks: true);

        list.MoveCursorTo(2, extendMarks: false);
        list.MoveCursorTo(3, extendMarks: true);

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[1], rows[2], rows[3] },
            "the second run starts where the plain move left the cursor");
    }

    [Fact]
    public async Task MoveCursorTo_AfterTheMarksWereCleared_StartsANewRunAtTheCursor()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectedItem = rows[0];
        list.MoveCursorTo(2, extendMarks: true);   // marks a, b, c

        list.DeselectAll();                         // Esc; the cursor stays on c
        list.MoveCursorTo(3, extendMarks: true);

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[2], rows[3] },
            "the cleared marks do not come back with the next Shift move");
    }

    [Fact]
    public async Task ExtendMarksTo_AfterAClickCollapsedTheMarksOnTheRunsLastRow_StartsFromThatRow()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectedItem = rows[0];
        list.ExtendMarksTo(rows[2]);                // Shift+click: a, b, c

        list.DeselectAll();                         // a plain click on c collapses the marks to it
        list.SelectedItem = rows[2];
        list.ExtendMarksTo(rows[4]);                // Shift+click on e

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[2], rows[3], rows[4] });
    }

    [Fact]
    public async Task MoveCursorTo_ShiftFromAMarkedRow_UnmarksTheRange()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectAll();
        list.SelectedItem = rows[1];

        list.MoveCursorTo(3, extendMarks: true);

        list.SelectedItem.Should().BeSameAs(rows[3]);
        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[4] },
            "a run that starts on a marked row takes the marks back");
    }

    [Fact]
    public async Task MoveCursorTo_AnUnmarkingRunSteppingBack_RestoresTheMarks()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectAll();
        list.SelectedItem = rows[1];

        list.MoveCursorTo(4, extendMarks: true);
        list.MoveCursorTo(2, extendMarks: true);

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[3], rows[4] });
    }

    [Fact]
    public async Task ExtendMarksTo_FromAMarkedCursor_StillMarks()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.SelectedItem = rows[1];
        list.ToggleSelect(rows[1]);   // Ctrl+click on b

        list.ExtendMarksTo(rows[3]);  // Shift+click on d

        list.SelectedItem.Should().BeSameAs(rows[3]);
        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[1], rows[2], rows[3] });
    }

    [Fact]
    public async Task ExtendMarksTo_AgainFromTheSameAnchor_ReplacesTheRangeOnly()
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c", "d", "e");
        list.ToggleSelect(rows[4]);   // marked before the clicks
        list.SelectedItem = rows[0];

        list.ExtendMarksTo(rows[3]);
        list.ExtendMarksTo(rows[1]);

        list.SelectedFiles.Should().BeEquivalentTo(new[] { rows[0], rows[1], rows[4] });
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(99, 2)]
    public async Task MoveCursorTo_OutOfRange_IsClampedToTheList(int index, int expected)
    {
        var (list, rows) = await CreateWithFiles(false, "a", "b", "c");

        list.MoveCursorTo(index, extendMarks: false);

        list.SelectedItem.Should().BeSameAs(rows[expected]);
    }

    [Fact]
    public void MoveCursorTo_InAnEmptyList_DoesNothing()
    {
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);

        list.MoveCursorTo(0, extendMarks: true);

        list.SelectedItem.Should().BeNull();
        list.SelectedFiles.Should().BeEmpty();
    }
}
