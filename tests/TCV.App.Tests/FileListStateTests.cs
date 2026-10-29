using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Navigation;
using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>Marks (Insert / Space), the quick filter and the "why is this list empty" state of a panel.</summary>
public class FileListStateTests
{
    private static async Task<(FileListViewModel List, List<FileEntryViewModel> Rows)> CreateWithFiles(params string[] names)
    {
        var fs = new InMemoryFileSystem("file");
        foreach (var n in names) fs.AddFile($"/C:/work/{n}", [1]);

        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);
        var rows = new List<FileEntryViewModel>();
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
    public async Task ToggleSelectAndAdvance_MarksTheCursorRowAndMovesDown()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        list.SelectedItem = rows[0];

        list.ToggleSelectAndAdvance();

        rows[0].IsSelected.Should().BeTrue();
        list.SelectedFiles.Should().ContainSingle().Which.Should().BeSameAs(rows[0]);
        list.SelectedItem.Should().BeSameAs(rows[1]);
    }

    [Fact]
    public async Task ToggleSelectAndAdvance_RepeatedPresses_MarkARunOfFiles()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        list.SelectedItem = rows[0];

        list.ToggleSelectAndAdvance();
        list.ToggleSelectAndAdvance();

        list.SelectedFiles.Select(f => f.Name).Should().Equal("a.txt", "b.txt");
        list.SelectedItem.Should().BeSameAs(rows[2]);
    }

    [Fact]
    public async Task ToggleSelectAndAdvance_OnAMarkedRow_UnmarksIt()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt");
        list.SelectedItem = rows[0];
        list.ToggleSelectAndAdvance();
        list.SelectedItem = rows[0];

        list.ToggleSelectAndAdvance();

        rows[0].IsSelected.Should().BeFalse();
        list.SelectedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task ToggleSelectAndAdvance_OnTheLastRow_MarksItAndStaysPut()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt");
        list.SelectedItem = rows[1];

        list.ToggleSelectAndAdvance();

        rows[1].IsSelected.Should().BeTrue();
        list.SelectedItem.Should().BeSameAs(rows[1]);
    }

    [Fact]
    public async Task ToggleSelectAndAdvance_OnTheParentEntry_MarksNothingButStillMovesOn()
    {
        var (list, rows) = await CreateWithFiles("a.txt");
        var parent = FileEntryViewModel.CreateParent();
        list.Files.Insert(0, parent);
        list.SelectedItem = parent;

        list.ToggleSelectAndAdvance();

        list.SelectedFiles.Should().BeEmpty();
        list.SelectedItem.Should().BeSameAs(rows[0]);
    }

    [Fact]
    public async Task SetMarks_ReplacesTheMarksAndIgnoresTheParentEntry()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        list.SetMarks([rows[0]]);

        list.SetMarks([rows[1], rows[2], FileEntryViewModel.CreateParent()]);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("b.txt", "c.txt");
        rows[0].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task TypingAFilter_UnmarksTheRowsItHides()
    {
        // Selected files the panel is no longer showing must not stay selected: F8 would delete them unseen.
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt");
        list.SetMarks(rows);

        list.QuickSearchText = "zzz";

        list.SelectedFiles.Should().BeEmpty();
        rows.Should().OnlyContain(r => !r.IsSelected);
    }

    [Fact]
    public async Task CursorAfterFilter_LeavesTheParentEntryForTheFirstMatch()
    {
        var (_, rows) = await CreateWithFiles("bravo.txt", "brown.txt");
        var parent = FileEntryViewModel.CreateParent();
        var shown = new List<FileEntryViewModel> { parent, rows[0], rows[1] };

        FileListViewModel.CursorAfterFilter(parent, shown, QuickFilter.Parse("br"), queryChanged: true).Should().BeSameAs(rows[0],
            "Enter right after typing must open what was typed, not go up a level");
    }

    [Fact]
    public async Task CursorAfterFilter_KeepsACursorThatStillMatches()
    {
        var (_, rows) = await CreateWithFiles("bravo.txt", "brown.txt");

        FileListViewModel.CursorAfterFilter(rows[1], rows, QuickFilter.Parse("br"), queryChanged: true).Should().BeSameAs(rows[1]);
    }

    [Fact]
    public async Task CursorAfterFilter_MovesOffARowTheFilterHid_AndGivesUpWhenNothingMatches()
    {
        var (_, rows) = await CreateWithFiles("alpha.txt", "bravo.txt");

        FileListViewModel.CursorAfterFilter(rows[0], [rows[1]], QuickFilter.Parse("bra"), queryChanged: true).Should().BeSameAs(rows[1]);
        FileListViewModel.CursorAfterFilter(rows[0], [FileEntryViewModel.CreateParent()], QuickFilter.Parse("zzz"), queryChanged: true).Should().BeNull();
    }

    [Fact]
    public async Task CursorAfterFilter_GoesToTheNameThatStartsWithTheText_NotTheFirstThatContainsIt()
    {
        // The reported case: the cursor stayed on MainWindow.xbf, and WinDevCleaner.App sat out of sight
        // under a column of Microsoft.Windows.* that all contain "wind" as well.
        var (_, rows) = await CreateWithFiles(
            "MainWindow.xbf", "Microsoft.Windows.AI.dll", "Microsoft.Windows.SDK.NET.dll", "WinDevCleaner.App.dll");

        FileListViewModel.CursorAfterFilter(rows[0], rows, QuickFilter.Parse("wind"), queryChanged: true)
            .Should().BeSameAs(rows[3]);
    }

    [Fact]
    public async Task CursorAfterFilter_PrefersAWordStartToAMatchInsideAWord()
    {
        var (_, rows) = await CreateWithFiles("windslow.txt", "app-dsl.txt");

        FileListViewModel.CursorAfterFilter(null, rows, QuickFilter.Parse("dsl"), queryChanged: true)
            .Should().BeSameAs(rows[1]);
    }

    [Fact]
    public async Task CursorAfterFilter_KeepsAMatchPickedWithTheArrowWhileItIsAsGood()
    {
        // ↓ to the second name that starts with the text, one more letter: the pick stays.
        var (_, rows) = await CreateWithFiles("readme.md", "README.txt", "src.readme");

        FileListViewModel.CursorAfterFilter(rows[1], rows, QuickFilter.Parse("readm"), queryChanged: true)
            .Should().BeSameAs(rows[1]);
        FileListViewModel.CursorAfterFilter(rows[2], rows, QuickFilter.Parse("readm"), queryChanged: true)
            .Should().BeSameAs(rows[0], "a word inside a name is a weaker match than the start of one");
    }

    [Fact]
    public async Task CursorAfterFilter_UnderTheSameQuery_LeavesTheCursorWhereTheUserPutIt()
    {
        // A reload or a sort refills the list: the cursor moved with ↓ to a weaker match must not jump back.
        var (_, rows) = await CreateWithFiles("WinDevCleaner.App.dll", "MainWindow.xbf");
        var parent = FileEntryViewModel.CreateParent();
        var shown = new List<FileEntryViewModel> { parent, rows[0], rows[1] };

        FileListViewModel.CursorAfterFilter(rows[1], shown, QuickFilter.Parse("wind"), queryChanged: false).Should().BeSameAs(rows[1]);
        FileListViewModel.CursorAfterFilter(parent, shown, QuickFilter.Parse("wind"), queryChanged: false).Should().BeSameAs(parent);
    }

    // ── The miss and the tree (6.87) ──────────────────────────────────────────

    [Fact]
    public async Task QuickSearch_NothingMatches_SaysSo_AndAnEmptyBoxIsNoMiss()
    {
        var (list, _) = await CreateWithFiles("alpha.txt");

        list.QuickSearchText = "zzz";
        list.QuickSearchHasMatch.Should().BeFalse();

        list.QuickSearchText = string.Empty;
        list.QuickSearchHasMatch.Should().BeTrue();
    }

    // A real listing: mounted, so the load reads the in-memory folder rather than the disk.
    private static async Task<FileListViewModel> Listed(params string[] names)
    {
        var fs = new InMemoryFileSystem("ftp");
        foreach (var n in names) fs.AddFile($"/work/{n}", [1]);

        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(fs, "/work");
        await list.LoadAsync("/work");
        return list;
    }

    [Fact]
    public async Task HasNewerLoadInFlight_OnceEveryLoadHasLanded_IsFalse_AlsoInsideTheLoadedEvent()
    {
        var list = await Listed("a.txt", "b.txt");
        var seen = new List<bool>();
        list.DirectoryLoaded += (_, _) => seen.Add(list.HasNewerLoadInFlight);

        await list.LoadAsync("/work");
        await list.LoadAsync("/work");

        // The view restores the cursor only from the last load's event.
        seen.Should().Equal(false, false);
        list.HasNewerLoadInFlight.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_OvertakenByANewerLoadThatLandsFirst_LeavesItsRowsAndIsNotInFlight()
    {
        var server = new InMemoryFileSystem("ftp");
        server.AddFile("/work/a.txt", [1]);
        var gated = new HeldListingFileSystem(server);
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(gated, "/work");
        var loaded = 0;
        list.DirectoryLoaded += (_, _) => loaded++;

        // A server that does not stop for the token: the older listing comes back after the newer one.
        var release = gated.HoldNextListing();
        var older = list.LoadAsync("/work");
        list.HasNewerLoadInFlight.Should().BeTrue();

        server.AddFile("/work/b.txt", [1]);
        await list.LoadAsync("/work");
        release.SetResult();
        await older;

        list.Files.Where(f => !f.IsParentEntry).Select(f => f.Name).Should().Equal("a.txt", "b.txt");
        loaded.Should().Be(1, "the older listing must not land over the newer one");
        list.HasNewerLoadInFlight.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_TheServersOwnTimeout_IsAFailureAndNoLongerInFlight()
    {
        var server = new InMemoryFileSystem("ftp");
        server.AddFile("/work/a.txt", [1]);
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(server, "/work");
        Exception? reported = null;
        list.ListingCompleted += (_, e) => reported = e.Error;

        server.FailListingOf = "/work";
        server.FailListingWith = () => new TaskCanceledException("HTTP timeout", new TimeoutException());
        await list.LoadAsync("/work");

        list.HasLoadError.Should().BeTrue("a timeout is not the load being replaced by a newer one");
        reported.Should().BeOfType<TaskCanceledException>();
        list.HasNewerLoadInFlight.Should().BeFalse();
    }

    [Fact]
    public async Task CursorAfterRefresh_TheCursorsRowWent_TakesTheRowNowInItsPlace()
    {
        var (_, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        var state = new FileListViewModel.ViewState("b.txt", 1, []);

        FileListViewModel.CursorAfterRefresh(rows[1], [rows[0], rows[2]], state).Should().BeSameAs(rows[2]);
    }

    [Fact]
    public async Task CursorAfterRefresh_TheLastRowWent_TakesTheNewLastRow_NotTheFirst()
    {
        // The reported jump: F8 on the last file put the cursor on ".." and scrolled the list to the top.
        var (_, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        var state = new FileListViewModel.ViewState("c.txt", 2, []);

        FileListViewModel.CursorAfterRefresh(null, [rows[0], rows[1]], state).Should().BeSameAs(rows[1]);
    }

    [Fact]
    public async Task CursorAfterRefresh_ACursorStillListed_StaysWhereAKeyPutIt()
    {
        var (_, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        var state = new FileListViewModel.ViewState("b.txt", 1, []);

        FileListViewModel.CursorAfterRefresh(rows[2], [rows[0], rows[2]], state).Should().BeSameAs(rows[2]);
    }

    [Fact]
    public async Task CursorAfterRefresh_AFileListedAnew_IsFoundByName_AndAnEmptyListHasNoCursor()
    {
        var (_, rows) = await CreateWithFiles("a.txt", "b.txt");
        var changed = new FileEntryViewModel(rows[1].Entry);
        var state = new FileListViewModel.ViewState("b.txt", 1, []);

        FileListViewModel.CursorAfterRefresh(rows[1], [changed, rows[0]], state).Should().BeSameAs(changed);
        FileListViewModel.CursorAfterRefresh(rows[1], [], state).Should().BeNull();
    }

    [Theory]
    [InlineData("b.txt", "c.txt")]
    [InlineData("c.txt", "b.txt")]
    public async Task LoadAsync_ARefreshThatTookTheCursorsRow_PutsTheCursorOnItsSuccessorWithTheRows(string deleted, string expected)
    {
        // Not later, when the view restores the scroll after the layout: in between ↓ moved from the top.
        var server = new InMemoryFileSystem("ftp");
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
        {
            server.AddFile($"/work/{name}", [1]);
        }

        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(server, "/work");
        await list.LoadAsync("/work");
        list.SelectedItem = list.Files.Single(f => f.Name == deleted);
        string? cursorWhenLanded = null;
        list.DirectoryLoaded += (_, _) => cursorWhenLanded = list.SelectedItem?.Name;

        list.PrepareForRefresh();
        await server.DeleteAsync(new VfsPath($"/work/{deleted}"), recursive: false, CancellationToken.None);
        await list.LoadAsync("/work");

        cursorWhenLanded.Should().Be(expected);
    }

    [Fact]
    public async Task LoadAsync_ARefreshSavedInAnotherFolder_IsDroppedWhenThisOneLands()
    {
        var server = new InMemoryFileSystem("ftp");
        foreach (var path in new[] { "/work/a.txt", "/work/b.txt", "/work/c.txt", "/other/x.txt", "/other/y.txt", "/other/z.txt", "/other/w.txt" })
        {
            server.AddFile(path, [1]);
        }

        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(server, "/work");
        await list.LoadAsync("/work");
        list.SelectedItem = list.Files.Single(f => f.Name == "c.txt");

        list.PrepareForRefresh();
        await list.LoadAsync("/other");

        list.ConsumePendingViewState().Should().BeNull("the folder opens at its first row, not on the old row number");
        list.Files.Contains(list.SelectedItem!).Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_ARefreshAskedForWhileTheNextFolderIsRead_IsDroppedWhenItLands()
    {
        // The watcher's reload queued just as Enter opened a folder: it saves the rows still on screen.
        var server = new InMemoryFileSystem("ftp");
        foreach (var path in new[] { "/work/a.txt", "/work/b.txt", "/other/x.txt", "/other/y.txt" })
        {
            server.AddFile(path, [1]);
        }

        var gated = new HeldListingFileSystem(server);
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(gated, "/work");
        await list.LoadAsync("/work");
        list.SelectedItem = list.Files.Single(f => f.Name == "b.txt");

        var release = gated.HoldNextListing();
        var opening = list.LoadAsync("/other");
        list.PrepareForRefresh();
        release.SetResult();
        await opening;

        list.ConsumePendingViewState().Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_TheListedFolderReadAgain_LeavesItsRowsUsable_AnotherFolderHidesThem()
    {
        // %TEMP% never stops changing: every reload of the watcher dimmed the list and swallowed the clicks.
        var server = new InMemoryFileSystem("ftp");
        server.AddFile("/work/a.txt", [1]);
        server.AddFile("/other/x.txt", [1]);
        var gated = new HeldListingFileSystem(server);
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);

        var release = gated.HoldNextListing();
        list.Mount(gated, "/work");   // reads the folder
        list.IsOpeningFolder.Should().BeTrue("nothing is listed yet");
        release.SetResult();
        list.HasNewerLoadInFlight.Should().BeFalse();

        release = gated.HoldNextListing();
        var refresh = list.LoadAsync("/work");
        list.IsLoading.Should().BeTrue();
        list.IsOpeningFolder.Should().BeFalse();
        release.SetResult();
        await refresh;

        release = gated.HoldNextListing();
        var opening = list.LoadAsync("/other");
        list.IsOpeningFolder.Should().BeTrue("the rows on screen are another folder's");
        release.SetResult();
        await opening;
        list.IsOpeningFolder.Should().BeFalse();
    }

    [Fact]
    public async Task ReloadForFolderChange_WhileALoadIsOnItsWay_ReadsAgainOnceItLands_InsteadOfCancellingIt()
    {
        // A folder that keeps changing: each reload cancelled the one before, and none of them landed.
        var server = new InMemoryFileSystem("ftp");
        server.AddFile("/work/a.txt", [1]);
        var gated = new HeldListingFileSystem(server);
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(gated, "/work");
        await list.LoadAsync("/work");
        var landed = new List<string>();
        list.DirectoryLoaded += (_, _) => landed.Add(string.Join(",", list.Files.Where(f => !f.IsParentEntry).Select(f => f.Name)));

        var release = gated.HoldNextListing();
        var reading = list.LoadAsync("/work");
        server.AddFile("/work/b.txt", [1]);
        list.ReloadForFolderChange();
        release.SetResult();
        await reading;

        landed.Should().Equal("a.txt", "a.txt,b.txt");
        list.HasNewerLoadInFlight.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_ANameEditDroppedByTheListing_ReadsTheFolderAgainOnceItHasLanded()
    {
        // F2 a moment after a reload began, and the watcher saw a change meanwhile: the reload put off for
        // the edit runs after the listing that drops the edit, not inside it, cancelling it.
        var server = new InMemoryFileSystem("ftp");
        server.AddFile("/work/a.txt", [1]);
        var gated = new HeldListingFileSystem(server);
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(gated, "/work");
        await list.LoadAsync("/work");
        var landed = new List<string>();
        list.DirectoryLoaded += (_, _) => landed.Add(string.Join(",", list.Files.Where(f => !f.IsParentEntry).Select(f => f.Name)));
        var completed = 0;
        list.ListingCompleted += (_, _) => completed++;

        var release = gated.HoldNextListing();
        var reading = list.LoadAsync("/work");
        var edit = list.EditNameInPlaceAsync(list.Files.Single(f => f.Name == "a.txt"), "a.txt");
        server.AddFile("/work/b.txt", [1]);
        list.ReloadForFolderChange();
        release.SetResult();
        await reading;

        (await edit).Should().BeNull();
        landed.Should().Equal("a.txt", "a.txt,b.txt");
        completed.Should().Be(2, "the listing that dropped the edit is not cancelled by the reload");
        list.HasNewerLoadInFlight.Should().BeFalse();
    }

    [Fact]
    public async Task PrepareForRefresh_InAFolderWhoseCursorIsStillToBePlaced_SavesNothing()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt");
        list.PrepareForRefresh();
        list.ConsumePendingViewState().Should().BeNull("there is no cursor or mark to keep");

        list.SetMarks([rows[1]]);
        list.PrepareForRefresh();
        list.ConsumePendingViewState()!.SelectedItemNames.Should().Equal("b.txt");
    }

    [Fact]
    public async Task PrepareForRefresh_WithNoCursor_KeepsTheStateStillWaitingToBeRestored()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        list.SelectedItem = rows[1];
        list.PrepareForRefresh();

        // The reload took the cursor's row away and the view has not put the cursor back yet.
        list.SelectedItem = null;
        list.PrepareForRefresh();

        list.ConsumePendingViewState()!.FocusedItemName.Should().Be("b.txt");
    }

    /// <summary>Lists through the inner file system, but holds a listing back until the test lets it go.</summary>
    private sealed class HeldListingFileSystem(InMemoryFileSystem inner) : IVirtualFileSystem
    {
        private TaskCompletionSource? _hold;

        public TaskCompletionSource HoldNextListing() => _hold = new TaskCompletionSource();

        public string Scheme => inner.Scheme;
        public string DisplayName => inner.DisplayName;

        public event EventHandler<VfsChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
        {
            // Read now, returned when released: what the folder held when this listing was asked for.
            var listing = await inner.ListAsync(path, ct);
            if (Interlocked.Exchange(ref _hold, null) is { } hold)
            {
                await hold.Task;
            }

            return listing;
        }

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => inner.GetEntryAsync(path, ct);
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);
        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
            inner.OpenWriteAsync(path, overwrite, ct);
        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
            inner.CopyAsync(source, destination, progress, ct);
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => inner.MoveAsync(source, destination, ct);
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => inner.DeleteAsync(path, recursive, ct);
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => inner.RenameAsync(path, newName, ct);
        public bool SupportsOperation(VfsOperation operation) => inner.SupportsOperation(operation);
        public void Dispose() => inner.Dispose();
    }

    [Fact]
    public async Task QuickSearchJumps_KeepsEveryNameListed_AndPutsTheCursorOnTheMatch()
    {
        var list = await Listed("alpha.txt", "bravo.txt", "charlie.txt");
        list.QuickSearchJumps = true;
        var listed = list.Files.Count;

        list.QuickSearchText = "bra";

        list.Files.Should().HaveCount(listed, "jumping narrows nothing");
        list.SelectedItem!.Name.Should().Be("bravo.txt");
        list.QuickSearchHasMatch.Should().BeTrue();
    }

    [Fact]
    public async Task QuickSearchJumps_NothingMatches_LeavesTheCursorWhereItWas()
    {
        var list = await Listed("alpha.txt", "bravo.txt");
        list.QuickSearchJumps = true;
        list.SelectedItem = list.Files.Single(f => f.Name == "alpha.txt");

        list.QuickSearchText = "zzz";

        list.QuickSearchHasMatch.Should().BeFalse();
        list.SelectedItem!.Name.Should().Be("alpha.txt");
        list.Files.Should().Contain(f => f.Name == "bravo.txt");
    }

    [Fact]
    public async Task MoveToNextMatch_StepsOverTheNamesThatDoNotMatch()
    {
        var list = await Listed("a1.txt", "b.txt", "a2.txt", "c.txt", "a3.txt");
        list.QuickSearchJumps = true;
        list.QuickSearchText = "a";
        list.SelectedItem!.Name.Should().Be("a1.txt");

        list.MoveToNextMatch(forward: true, extendMarks: false);
        list.SelectedItem!.Name.Should().Be("a2.txt");
        list.MoveToNextMatch(forward: true, extendMarks: false);
        list.MoveToNextMatch(forward: true, extendMarks: false);
        list.SelectedItem!.Name.Should().Be("a3.txt", "past the last match the cursor stays");
        list.MoveToNextMatch(forward: false, extendMarks: false);
        list.SelectedItem!.Name.Should().Be("a2.txt");
    }

    [Fact]
    public async Task QuickSearchJumps_SwitchedOff_NarrowsTheListAgain()
    {
        var list = await Listed("alpha.txt", "bravo.txt");
        list.QuickSearchJumps = true;
        list.QuickSearchText = "bra";

        list.QuickSearchJumps = false;

        list.Files.Where(f => !f.IsParentEntry).Select(f => f.Name).Should().Equal("bravo.txt");
    }

    [Fact]
    public async Task QuickFilter_IsOffInTheTreeView_AndSwitchingToItClosesTheFilter()
    {
        var (list, _) = await CreateWithFiles("alpha.txt");
        list.CanQuickFilter.Should().BeTrue();
        list.QuickSearchText = "alp";

        list.ViewMode = Settings.ViewMode.Tree;

        list.CanQuickFilter.Should().BeFalse();
        list.IsFilterOpen.Should().BeFalse("a filter left open over the tree hides files in a list nobody sees");
        list.QuickSearchText.Should().BeEmpty();
    }

    [Fact]
    public async Task ChangingTheFolder_ClearsTheQuickFilter()
    {
        var (list, _) = await CreateWithFiles("a.txt");
        list.QuickSearchText = "zzz";

        list.CurrentPath = "/C:/work";

        list.QuickSearchText.Should().BeEmpty("a filter typed in one folder must not hide the next folder's files");
    }

    [Fact]
    public async Task ALoadThatFails_ClearsTheStaleListingAndSaysWhy()
    {
        var (list, rows) = await CreateWithFiles("a.txt");
        list.SetMarks(rows);

        list.CurrentPath = "/does/not/exist";   // the in-memory lister throws for an unknown folder

        list.HasLoadError.Should().BeTrue();
        list.EmptyStateText.Should().NotBeNullOrEmpty();
        list.Files.Where(f => !f.IsParentEntry).Should().BeEmpty("the previous folder's files must not stay under a new path");
        list.SelectedFiles.Should().BeEmpty();
    }

    [Fact]
    public void AFolderWithNothingInIt_ShowsTheEmptyStateNotAFailure()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/C:/empty");
        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);

        list.QuickSearchText = "x";      // drives ApplyFilter over an empty listing
        list.QuickSearchText = string.Empty;

        list.HasLoadError.Should().BeFalse();
        list.EmptyStateText.Should().Contain("Files_Empty");
    }

    [Fact]
    public async Task AFilterThatMatchesNothing_SaysSoInsteadOfLookingEmpty()
    {
        var (list, _) = await CreateWithFiles("a.txt");

        list.QuickSearchText = "zzz";

        list.EmptyStateText.Should().Contain("Files_NoMatches");
    }
}
