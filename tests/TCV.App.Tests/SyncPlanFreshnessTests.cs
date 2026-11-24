using FluentAssertions;
using TCV.App.Sync;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>
/// The comparison window is not modal (6.96): the panels are free while it is open, so what a plan is
/// about to replace is looked at once more, and a stale row does not overwrite what came after it.
/// </summary>
public sealed class SyncPlanFreshnessTests
{
    private static readonly DateTime Old = new(2026, 1, 1, 10, 0, 0);
    private static readonly DateTime New = new(2026, 1, 2, 10, 0, 0);

    private readonly InMemoryFileSystem _left = new("sftp");
    private readonly InMemoryFileSystem _right = new("ftp");

    private SyncEndpoint LeftSide => new(_left, "/l", true);

    private SyncEndpoint RightSide => new(_right, "/r", true);

    private CompareDirectoriesDialogViewModel Window() => new(LeftSide, RightSide);

    private async Task<CompareDirectoriesDialogViewModel> ComparedAsync()
    {
        var vm = Window();
        await vm.CompareCommand.ExecuteAsync(null);
        return vm;
    }

    public SyncPlanFreshnessTests()
    {
        _left.AddDirectory("/l");
        _right.AddDirectory("/r");
        _left.AddFile("/l/a.txt", [1, 2], New);
        _right.AddFile("/r/a.txt", [1], Old);
    }

    [Fact]
    public async Task Synchronize_NothingChanged_ClosesConfirmed()
    {
        var vm = await ComparedAsync();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        await vm.SynchronizeCommand.ExecuteAsync(null);

        vm.Confirmed.Should().BeTrue();
        closed.Should().Be(1);
    }

    [Fact]
    public async Task Synchronize_TheTargetChangedSinceTheComparison_StaysOpenAndSaysSo()
    {
        var vm = await ComparedAsync();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        // The user edited the server's copy in the panel while the window stood open.
        _right.AddFile("/r/a.txt", [1, 2, 3, 4, 5], New.AddDays(1));

        await vm.SynchronizeCommand.ExecuteAsync(null);

        vm.Confirmed.Should().BeFalse();
        closed.Should().Be(0);
        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Cmp_ChangedSince");
        vm.Plan.Should().NotBeEmpty("the window stays, so Compare can show the folder as it is now");
    }

    [Fact]
    public async Task FindChangedAsync_ANameAboutToBeCreatedWasTaken_IsChanged()
    {
        _left.AddFile("/l/new.txt", [1], Old);
        var vm = await ComparedAsync();
        _right.AddFile("/r/new.txt", [9], New);

        var changed = await SyncPlanFreshness.FindChangedAsync(vm.Plan, LeftSide, RightSide, default);

        changed.Should().Contain("new.txt");
    }

    [Fact]
    public async Task FindChangedAsync_ATargetThatWentAway_IsNoOverwrite()
    {
        var vm = await ComparedAsync();
        await _right.DeleteAsync(new VfsPath("/r/a.txt"), false, default);

        (await SyncPlanFreshness.FindChangedAsync(vm.Plan, LeftSide, RightSide, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task FindChangedAsync_ANewFileInAFolderThatIsNotThereYet_IsFine()
    {
        _left.AddDirectory("/l/sub");
        _left.AddFile("/l/sub/x.txt", [1], Old);
        var vm = await ComparedAsync();

        (await SyncPlanFreshness.FindChangedAsync(vm.Plan, LeftSide, RightSide, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task FindChangedAsync_ATargetFolderThatCannotBeRead_CountsForWhatWouldBeReplaced()
    {
        var vm = await ComparedAsync();
        _right.FailListingOf = "/r";

        (await SyncPlanFreshness.FindChangedAsync(vm.Plan, LeftSide, RightSide, default)).Should().Equal("a.txt");
    }

    [Fact]
    public async Task FindChangedAsync_ListsEachFolderOnce()
    {
        _left.AddFile("/l/b.txt", [1, 2], New);
        _right.AddFile("/r/b.txt", [1], Old);
        var vm = await ComparedAsync();
        var before = _right.ListCalls;

        await SyncPlanFreshness.FindChangedAsync(vm.Plan, LeftSide, RightSide, default);

        (_right.ListCalls - before).Should().Be(1, "one listing per folder, not a command per file");
    }

    [Fact]
    public async Task Compare_ThePanelsMovedOn_TheWindowStaysOnTheFoldersItWasOpenedFor()
    {
        var vm = await ComparedAsync();

        // Nothing in the window reads the panels: the two sides were fixed when it opened.
        vm.Left.Should().Be(LeftSide);
        vm.Right.Should().Be(RightSide);
        vm.LeftPath.Should().Be("/l");
        vm.RightPath.Should().Be("/r");
    }

    [Fact]
    public async Task Compare_ManyRows_GivesTheThreadBackBetweenSlices()
    {
        for (var i = 0; i < 2000; i++)
        {
            _left.AddFile($"/l/f{i:D5}.txt", [1], Old);
        }

        var vm = Window();

        await vm.CompareCommand.ExecuteAsync(null);

        vm.Rows.Should().HaveCountGreaterThan(2000);
        vm.IsComparing.Should().BeFalse();
    }
}
