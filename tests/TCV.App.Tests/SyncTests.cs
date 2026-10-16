using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.Sync;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The comparison walk, the plan and its execution, on throwaway trees: what must never happen (a
/// failed listing becoming "empty", a wrong arrow silently destroying a newer file, two sides that
/// never settle) is pinned here.
/// </summary>
public sealed class SyncTests : IDisposable
{
    private static readonly DateTime Older = new(2026, 3, 1, 10, 0, 0);
    private static readonly DateTime Newer = new(2026, 3, 5, 10, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-sync-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _fs = new(NullLogger<WindowsFileSystem>.Instance);

    private string Left => Path.Combine(_root, "left");
    private string Right => Path.Combine(_root, "right");

    public SyncTests()
    {
        Directory.CreateDirectory(Left);
        Directory.CreateDirectory(Right);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static void Put(string root, string relative, string content, DateTime modified)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
        File.SetLastWriteTime(path, modified);
    }

    private static string Read(string root, string relative) =>
        File.ReadAllText(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private async Task<List<ComparisonRow>> CompareAsync(ComparisonOptions? options = null, IVirtualFileSystem? right = null)
    {
        var rows = new List<ComparisonRow>();
        await foreach (var row in new DirectoryComparer(_fs, right ?? _fs, options).CompareAsync(new VfsPath(Left), new VfsPath(Right)))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<SyncPlanItem> Default(IEnumerable<ComparisonRow> rows) =>
        rows.Where(r => r.DefaultDirection != SyncDirection.None).Select(r => new SyncPlanItem(r, r.DefaultDirection));

    // ── Traversal ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Compare_FolderOnOneSide_ListsItsContentsWithTheSameStatus()
    {
        Put(Left, "only/deep/a.txt", "a", Older);

        var rows = await CompareAsync();

        rows.Select(r => (r.RelativePath, r.Status)).Should().BeEquivalentTo(new[]
        {
            ("only", ComparisonStatus.OnlyLeft),
            ("only/deep", ComparisonStatus.OnlyLeft),
            ("only/deep/a.txt", ComparisonStatus.OnlyLeft),
        });
    }

    [Fact]
    public async Task Compare_DifferenceInANestedFolder_IsReportedWithItsRelativePath()
    {
        Put(Left, "src/app/main.cs", "new", Newer);
        Put(Right, "src/app/main.cs", "old", Older);

        var rows = await CompareAsync();

        rows.Should().ContainSingle(r => r.RelativePath == "src/app/main.cs" && r.Status == ComparisonStatus.LeftNewer);
    }

    [Fact]
    public async Task Compare_RootThatCannotBeListed_AbortsInsteadOfComparingAgainstNothing()
    {
        Directory.Delete(Right, recursive: true);

        var act = async () => await CompareAsync();

        var thrown = await act.Should().ThrowAsync<ComparisonFailedException>();
        thrown.Which.Side.Should().Be("right");
    }

    [Fact]
    public async Task Compare_SubfolderThatCannotBeListed_IsMarkedUnreadableNotEmpty()
    {
        var remote = new InMemoryFileSystem();
        remote.AddDirectory("/r");
        remote.AddDirectory("/r/sub");
        remote.FailListingOf = "/r/sub";
        Put(Left, "sub/keep.txt", "precious", Older);

        var rows = new List<ComparisonRow>();
        var comparer = new DirectoryComparer(_fs, remote);
        await foreach (var row in comparer.CompareAsync(new VfsPath(Left), new VfsPath("/r")))
        {
            rows.Add(row);
        }

        // The listing of "/r" itself works and shows "sub"; the failure is in "sub".
        rows.Should().NotContain(r => r.Status == ComparisonStatus.OnlyLeft && r.RelativePath == "sub/keep.txt",
            "a failed listing is not an empty folder — pre-ticking every file as 'only here' overwrites the unseen side");
        rows.Should().Contain(r => r.RelativePath == "sub" && r.Status == ComparisonStatus.Unreadable);
        comparer.UnreadableFolders.Should().Be(1);
    }

    [Fact]
    public void Unreadable_IsNeverOfferedForCopying()
    {
        var row = new ComparisonRow("sub", null, null, ComparisonStatus.Unreadable, true);

        row.DefaultDirection.Should().Be(SyncDirection.None);
        row.CanCopy(SyncDirection.ToRight).Should().BeFalse();
        row.CanCopy(SyncDirection.ToLeft).Should().BeFalse();
    }

    // ── Case variants ─────────────────────────────────────────────────────────

    private static IFileEntry Entry(string name, long size = 10, DateTime? modified = null)
    {
        var mock = new Mock<IFileEntry>();
        mock.SetupGet(e => e.Path).Returns(new VfsPath("/x/" + name));
        mock.SetupGet(e => e.Name).Returns(name);
        mock.SetupGet(e => e.IsDirectory).Returns(false);
        mock.SetupGet(e => e.Size).Returns(size);
        mock.SetupGet(e => e.LastModified).Returns(modified ?? Older);
        mock.SetupGet(e => e.Attributes).Returns(FileAttributes.Normal);
        return mock.Object;
    }

    [Fact]
    public void CompareLevel_NamesDifferingOnlyByCase_DoNotThrow_AndAreFlagged()
    {
        var left = new[] { Entry("Readme"), Entry("README") };
        var right = new[] { Entry("Readme") };

        var rows = DirectoryComparison.CompareLevel(left, right, new ComparisonOptions());

        rows.Should().ContainSingle(r => r.RelativePath == "README" && r.Status == ComparisonStatus.OnlyLeft && r.CaseVariant);
        rows.All(r => r.CaseVariant ? r.DefaultDirection == SyncDirection.None : true).Should().BeTrue(
            "a name that collides on a Windows target must be looked at, not pre-ticked");
    }

    [Fact]
    public void CompareLevel_ExactNameWinsOverACaseOnlyMatch()
    {
        var left = new[] { Entry("a.txt", modified: Newer) };
        var right = new[] { Entry("A.TXT", modified: Older), Entry("a.txt", modified: Newer) };

        var rows = DirectoryComparison.CompareLevel(left, right, new ComparisonOptions { IncludeSame = true });

        rows.Should().Contain(r => r.Status == ComparisonStatus.Same && r.RelativePath == "a.txt");
        rows.Should().Contain(r => r.Status == ComparisonStatus.OnlyRight && r.RelativePath == "A.TXT");
    }

    // ── Content comparison ────────────────────────────────────────────────────

    [Fact]
    public async Task Compare_ByContent_IdenticalFilesWithDifferentTimestamps_AreTheSame()
    {
        // two worktrees: same bytes, checked out at different moments
        Put(Left, "a.txt", "same bytes", Newer);
        Put(Right, "a.txt", "same bytes", Older);

        (await CompareAsync()).Should().ContainSingle(r => r.Status == ComparisonStatus.LeftNewer, "by timestamp alone it looks changed");
        (await CompareAsync(new ComparisonOptions { CompareContent = true })).Should().BeEmpty();
    }

    [Fact]
    public async Task Compare_ByContent_SameTimeSameSizeDifferentBytes_IsDifferent()
    {
        Put(Left, "a.txt", "aaaa", Older);
        Put(Right, "a.txt", "bbbb", Older);

        (await CompareAsync()).Should().BeEmpty("timestamp and size cannot tell them apart");
        (await CompareAsync(new ComparisonOptions { CompareContent = true }))
            .Should().ContainSingle(r => r.Status == ComparisonStatus.Different);
    }

    [Fact]
    public async Task ContentComparer_StopsAtTheFirstDifference_AndHandlesDifferentLengths()
    {
        Put(Left, "a", new string('x', 200_000), Older);
        Put(Right, "a", new string('x', 199_999) + "y", Older);
        Put(Right, "short", "x", Older);

        (await ContentComparer.AreEqualAsync(_fs, new VfsPath(Path.Combine(Left, "a")), _fs, new VfsPath(Path.Combine(Right, "a")), default))
            .Should().BeFalse();
        (await ContentComparer.AreEqualAsync(_fs, new VfsPath(Path.Combine(Left, "a")), _fs, new VfsPath(Path.Combine(Right, "short")), default))
            .Should().BeFalse();
        (await ContentComparer.AreEqualAsync(_fs, new VfsPath(Path.Combine(Left, "a")), _fs, new VfsPath(Path.Combine(Left, "a")), default))
            .Should().BeTrue();
    }

    // ── Plan summary (INV-11) ─────────────────────────────────────────────────

    [Fact]
    public async Task Summary_CountsCreatedReplacedAndNewerReplaced()
    {
        Put(Left, "new.txt", "n", Older);                 // only left        -> created
        Put(Left, "older-left.txt", "l", Older);          // right is newer
        Put(Right, "older-left.txt", "r", Newer);
        Put(Left, "newer-left.txt", "l", Newer);          // left is newer
        Put(Right, "newer-left.txt", "r", Older);
        var rows = await CompareAsync();

        // the user forces every arrow to the right, including onto the newer right-hand file
        var plan = rows.Select(r => new SyncPlanItem(r, SyncDirection.ToRight)).ToList();
        var summary = SyncPlanSummary.Of(plan, TimeSpan.FromSeconds(2));

        summary.NewFiles.Should().Be(1);
        summary.Overwritten.Should().Be(2);
        summary.OverwrittenNewer.Should().Be(1, "older-left.txt would replace a newer file");
        summary.NewerSamples.Should().Equal("older-left.txt");
    }

    [Fact]
    public void Summary_FileAgainstFolder_IsSkippedNotCopied()
    {
        var file = Entry("x");
        var folder = new Mock<IFileEntry>();
        folder.SetupGet(e => e.IsDirectory).Returns(true);
        folder.SetupGet(e => e.Name).Returns("x");
        folder.SetupGet(e => e.Path).Returns(new VfsPath("/y/x"));
        var row = new ComparisonRow("x", file, folder.Object, ComparisonStatus.Different, false);

        var summary = SyncPlanSummary.Of([new SyncPlanItem(row, SyncDirection.ToRight)], TimeSpan.FromSeconds(2));

        summary.Skipped.Should().Be(1);
        summary.Total.Should().Be(0);
    }

    [Fact]
    public void Confirmation_NamesTheReplacedFilesAndWarnsAboutNewerOnes()
    {
        var src = Entry("a.txt", modified: Older);
        var dst = Entry("a.txt", modified: Newer);
        var row = new ComparisonRow("a.txt", src, dst, ComparisonStatus.RightNewer, false);
        var plan = new[] { new SyncPlanItem(row, SyncDirection.ToRight) };
        var summary = SyncPlanSummary.Of(plan, TimeSpan.FromSeconds(2));
        var endpoint = new SyncEndpoint(_fs, Left, false);

        var text = SyncConfirmation.Describe(summary, plan, endpoint, endpoint);

        text.Should().Contain("a.txt").And.Contain("Cmp_ConfirmNewer").And.Contain("Cmp_ConfirmBin");
    }

    [Fact]
    public void Confirmation_ServerTarget_SaysThereIsNoRecycleBin()
    {
        var row = new ComparisonRow("a.txt", Entry("a.txt"), Entry("a.txt", modified: Newer), ComparisonStatus.RightNewer, false);
        var plan = new[] { new SyncPlanItem(row, SyncDirection.ToRight) };
        var summary = SyncPlanSummary.Of(plan, TimeSpan.FromSeconds(2));

        var text = SyncConfirmation.Describe(
            summary, plan, new SyncEndpoint(_fs, Left, false), new SyncEndpoint(new InMemoryFileSystem("sftp"), "/", true));

        text.Should().Contain("Cmp_ConfirmNoBin");
    }

    // ── Execution ─────────────────────────────────────────────────────────────

    private async Task<(SyncRunResult Result, OperationLog Log)> RunAsync(IEnumerable<SyncPlanItem> plan)
    {
        var log = new OperationLog(Path.Combine(_root, "log.jsonl"));
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);
        var runner = new SyncPlanRunner(
            NullLogger<CopyOperation>.Instance, NullLogger<DeleteOperation>.Instance,
            NullLogger<CreateDirectoryOperation>.Instance);

        var result = await runner.RunAsync(
            plan.ToList(),
            new SyncEndpoint(_fs, Left, false),
            new SyncEndpoint(_fs, Right, false),
            async op =>
            {
                await queue.EnqueueAsync(op);
                return op.State == OperationState.Completed;
            });
        return (result, log);
    }

    [Fact]
    public async Task Run_CopiesNewFilesAndFolders_AndKeepsTheSourceTimestamps()
    {
        Put(Left, "src/new/a.txt", "content", Older);

        var (result, _) = await RunAsync(Default(await CompareAsync()));

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        Read(Right, "src/new/a.txt").Should().Be("content");
        File.GetLastWriteTime(Path.Combine(Right, "src", "new", "a.txt"))
            .Should().BeCloseTo(Older, TimeSpan.FromSeconds(2), "a copy stamped 'now' would look newer than its source");
    }

    [Fact]
    public async Task Run_ThenCompareAgain_TheSidesHaveSettled()
    {
        Put(Left, "a.txt", "left version", Newer);
        Put(Right, "a.txt", "right version", Older);
        Put(Left, "b.txt", "only left", Older);
        Put(Right, "c.txt", "only right", Older);

        var (result, _) = await RunAsync(Default(await CompareAsync()));
        var again = await CompareAsync();

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        again.Should().BeEmpty("without preserved timestamps the copies looked newer and the arrow flipped");
    }

    [Fact]
    public async Task Run_ReplacingAFile_SendsTheOldOneThroughALoggedDelete()
    {
        Put(Left, "a.txt", "fresh", Newer);
        Put(Right, "a.txt", "stale", Older);

        var (result, log) = await RunAsync(Default(await CompareAsync()));

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        result.FilesReplaced.Should().Be(1);
        Read(Right, "a.txt").Should().Be("fresh");
        log.LoadAll().Should().Contain(e => e.Kind == OperationKind.Delete,
            "the replaced file is recycled by a delete that goes through the queue and the log (INV-10, INV-11)");
    }

    [Fact]
    public async Task Run_TargetThatCannotTakeTheTime_ReportsTheUnstampedCopies()
    {
        Put(Left, "a.txt", "content", Older);
        var entry = (await _fs.GetEntryAsync(new VfsPath(Path.Combine(Left, "a.txt"))))!;
        var server = new InMemoryFileSystem("dav");   // WebDAV: no way to set a modification time
        var log = new OperationLog(Path.Combine(_root, "log.jsonl"));
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);
        var runner = new SyncPlanRunner(
            NullLogger<CopyOperation>.Instance, NullLogger<DeleteOperation>.Instance,
            NullLogger<CreateDirectoryOperation>.Instance);

        var result = await runner.RunAsync(
            [new SyncPlanItem(new ComparisonRow("a.txt", entry, null, ComparisonStatus.OnlyLeft, false), SyncDirection.ToRight)],
            new SyncEndpoint(_fs, Left, false),
            new SyncEndpoint(server, "/", true),
            async op =>
            {
                await queue.EnqueueAsync(op);
                return op.State == OperationState.Completed;
            });

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        server.HasFile("/a.txt").Should().BeTrue();
        result.Unstamped.Should().Be(1, "the copy carries the upload time and will look newer next time");
    }

    [Fact]
    public async Task Run_KindMismatch_IsReportedAndNothingIsCopied()
    {
        Put(Left, "x", "a file", Older);
        Directory.CreateDirectory(Path.Combine(Right, "x"));
        var rows = await CompareAsync();

        var (result, _) = await RunAsync(rows.Select(r => new SyncPlanItem(r, SyncDirection.ToRight)));

        result.Succeeded.Should().BeFalse();
        result.FilesCopied.Should().Be(0);
        Directory.Exists(Path.Combine(Right, "x")).Should().BeTrue();
    }

    [Fact]
    public async Task Run_OperationThatFails_IsReportedNotSwallowed()
    {
        Put(Left, "a.txt", "x", Older);
        var rows = await CompareAsync();
        var runner = new SyncPlanRunner(
            NullLogger<CopyOperation>.Instance, NullLogger<DeleteOperation>.Instance,
            NullLogger<CreateDirectoryOperation>.Instance);

        var result = await runner.RunAsync(
            Default(rows).ToList(),
            new SyncEndpoint(_fs, Left, false), new SyncEndpoint(_fs, Right, false),
            _ => Task.FromResult(false));

        result.Succeeded.Should().BeFalse();
        result.FilesCopied.Should().Be(0);
    }

    // ── The window's view model ───────────────────────────────────────────────

    [Fact]
    public async Task ViewModel_Compare_FillsRowsAndTicksTheDefaults()
    {
        Put(Left, "only.txt", "x", Older);
        var vm = new CompareDirectoriesDialogViewModel(_fs, Left, _fs, Right);

        await vm.CompareCommand.ExecuteAsync(null);

        vm.Rows.Should().ContainSingle(r => r.Status == ComparisonStatus.OnlyLeft && r.IsChecked);
        vm.Plan.Should().ContainSingle();
        vm.IsComparing.Should().BeFalse();
    }

    [Fact]
    public async Task ViewModel_CycleDirection_StepsThroughWhatIsPossible()
    {
        Put(Left, "a.txt", "l", Newer);
        Put(Right, "a.txt", "r", Older);
        var vm = new CompareDirectoriesDialogViewModel(_fs, Left, _fs, Right);
        await vm.CompareCommand.ExecuteAsync(null);
        var row = vm.Rows.Single();
        row.Direction.Should().Be(SyncDirection.ToRight);

        row.CycleDirectionCommand.Execute(null);
        row.Direction.Should().Be(SyncDirection.ToLeft);
        row.OverwritesNewer.Should().BeTrue("pointing at the newer left file replaces it with the older right one");
        row.WarningText.Should().NotBeEmpty();

        row.CycleDirectionCommand.Execute(null);
        row.Direction.Should().Be(SyncDirection.None);
        row.IsChecked.Should().BeFalse();

        row.CycleDirectionCommand.Execute(null);
        row.Direction.Should().Be(SyncDirection.ToRight);
        row.IsChecked.Should().BeTrue();
    }

    [Fact]
    public async Task ViewModel_OneSidedRow_OnlyOffersTheDirectionThatHasASource()
    {
        Put(Left, "only.txt", "x", Older);
        var vm = new CompareDirectoriesDialogViewModel(_fs, Left, _fs, Right);
        await vm.CompareCommand.ExecuteAsync(null);
        var row = vm.Rows.Single();

        row.CycleDirectionCommand.Execute(null);   // -> none
        row.CycleDirectionCommand.Execute(null);   // -> back to right, never left

        row.Direction.Should().Be(SyncDirection.ToRight);
    }

    [Fact]
    public async Task ViewModel_SelectAllToLeft_SkipsRowsWithNothingOnTheRight()
    {
        Put(Left, "only-left.txt", "x", Older);
        Put(Right, "only-right.txt", "x", Older);
        var vm = new CompareDirectoriesDialogViewModel(_fs, Left, _fs, Right);
        await vm.CompareCommand.ExecuteAsync(null);

        vm.SelectToLeftCommand.Execute(null);

        vm.Plan.Select(p => p.Row.RelativePath).Should().Equal("only-right.txt");
    }

    [Fact]
    public async Task ViewModel_UnlistableSide_ReportsAndOffersNothing()
    {
        Directory.Delete(Right, recursive: true);
        var vm = new CompareDirectoriesDialogViewModel(_fs, Left, _fs, Right);

        await vm.CompareCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Cmp_ListingFailed");
        vm.Plan.Should().BeEmpty();
    }

    [Fact]
    public async Task ViewModel_Synchronize_IsUnavailableWhileComparing()
    {
        var release = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var mock = new Mock<IVirtualFileSystem>();
        mock.SetupGet(v => v.Scheme).Returns("file");
        var folder = new Mock<IFileEntry>();
        folder.SetupGet(e => e.IsDirectory).Returns(true);
        mock.Setup(v => v.GetEntryAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(folder.Object);
        mock.Setup(v => v.ListAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns(async (VfsPath _, CancellationToken ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return (IReadOnlyList<IFileEntry>)Array.Empty<IFileEntry>();
            });
        var vm = new CompareDirectoriesDialogViewModel(mock.Object, "/l", mock.Object, "/r");

        var running = vm.CompareCommand.ExecuteAsync(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        vm.SynchronizeCommand.CanExecute(null).Should().BeFalse();

        vm.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        vm.SynchronizeCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ViewModel_ServerInvolved_UsesTheWiderTimestampTolerance()
    {
        var remote = new InMemoryFileSystem("sftp");

        new CompareDirectoriesDialogViewModel(_fs, Left, remote, "/r").TimeTolerance
            .Should().Be(ComparisonOptions.RemoteTimeTolerance);
        new CompareDirectoriesDialogViewModel(_fs, Left, _fs, Right).TimeTolerance
            .Should().Be(TimeSpan.FromSeconds(2));
    }
}
