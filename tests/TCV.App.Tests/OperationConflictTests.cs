using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>How copy and move behave when the name is taken, the folder is the source itself, or the tree has a junction.</summary>
public class OperationConflictTests
{
    private static readonly DateTime Old = new(2020, 1, 1);
    private static readonly DateTime New = new(2025, 1, 1);

    private sealed class FixedStrategy : IConflictStrategy
    {
        private readonly ConflictResolution _answer;
        public FixedStrategy(ConflictResolution answer) => _answer = answer;
        public int Asked { get; private set; }

        public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct)
        {
            Asked++;
            return Task.FromResult(_answer);
        }
    }

    private static async Task<IFileEntry> EntryAsync(InMemoryFileSystem fs, string path) =>
        (await fs.GetEntryAsync(new VfsPath(path)))!;

    private static CopyOperation Copy(
        InMemoryFileSystem fs, IFileEntry source, string dest, IConflictStrategy strategy, CopyOptions? options = null) =>
        new(fs, fs, [source], new VfsPath(dest), strategy, options ?? new CopyOptions(), NullLogger<CopyOperation>.Instance);

    private static MoveOperation Move(
        InMemoryFileSystem fs, IFileEntry source, string dest, IConflictStrategy strategy) =>
        new(fs, fs, [source], new VfsPath(dest), strategy, NullLogger<MoveOperation>.Instance);

    // ── Copy ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_Skip_KeepsTheExistingFileAndCountsTheSkip()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);

        var op = Copy(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.Skip));
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/x.txt").Should().Equal(2);
        op.SkippedCount.Should().Be(1);
        op.State.Should().Be(OperationState.Completed);
    }

    [Fact]
    public async Task Copy_Overwrite_ReplacesTheExistingFile()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);

        var op = Copy(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.Overwrite));
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/x.txt").Should().Equal(1);
        op.SkippedCount.Should().Be(0);
    }

    [Fact]
    public async Task Copy_RenameAuto_KeepsBothFiles()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);

        var op = Copy(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.RenameAuto));
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/x.txt").Should().Equal(2);
        fs.ReadFile("/b/x (1).txt").Should().Equal(1);
    }

    [Fact]
    public async Task Copy_OverwriteIfNewerAnswer_ReplacesOnlyWhenTheSourceIsNewer()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/older.txt", [1], Old);
        fs.AddFile("/b/older.txt", [2], New);
        fs.AddFile("/a/newer.txt", [3], New);
        fs.AddFile("/b/newer.txt", [4], Old);
        var strategy = new FixedStrategy(ConflictResolution.OverwriteIfNewer);

        await Copy(fs, await EntryAsync(fs, "/a/older.txt"), "/b", strategy).ExecuteAsync(CancellationToken.None);
        await Copy(fs, await EntryAsync(fs, "/a/newer.txt"), "/b", strategy).ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/older.txt").Should().Equal(2);
        fs.ReadFile("/b/newer.txt").Should().Equal(3);
    }

    [Fact]
    public async Task Copy_CancelAtConflict_EndsCancelledAndTouchesNothing()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);

        var op = Copy(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.Cancel));
        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None)).Should().ThrowAsync<OperationCanceledException>();

        op.State.Should().Be(OperationState.Cancelled);
        fs.ReadFile("/b/x.txt").Should().Equal(2);
    }

    [Fact]
    public async Task Copy_FolderOntoFolder_MergesAndAsksOnlyAboutTheFilesInside()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/proj/one.txt", [1]);
        fs.AddFile("/a/proj/two.txt", [2]);
        fs.AddFile("/b/proj/two.txt", [9]);
        fs.AddFile("/b/proj/keep.txt", [7]);
        var strategy = new FixedStrategy(ConflictResolution.Skip);

        var op = Copy(fs, await EntryAsync(fs, "/a/proj"), "/b", strategy);
        await op.ExecuteAsync(CancellationToken.None);

        strategy.Asked.Should().Be(1, "only two.txt clashes; the folder itself is merged without a question");
        fs.ReadFile("/b/proj/one.txt").Should().Equal(1);
        fs.ReadFile("/b/proj/two.txt").Should().Equal(9);
        fs.ReadFile("/b/proj/keep.txt").Should().Equal(7);
    }

    [Fact]
    public async Task Copy_OnlyIfNewer_DoesNotSkipAFolderBecauseItsOwnTimestampIsNotNewer()
    {
        // A folder's mtime changes when entries are added or removed, not when a file inside is
        // edited. Comparing it used to skip whole subtrees whose files had changed.
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/proj/file.txt", [1], New);
        fs.AddFile("/b/proj/file.txt", [2], Old);
        var strategy = new FixedStrategy(ConflictResolution.Skip);

        var op = Copy(fs, await EntryAsync(fs, "/a/proj"), "/b", strategy, new CopyOptions(OnlyIfNewer: true));
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/proj/file.txt").Should().Equal(1);
        strategy.Asked.Should().Be(0, "a newer file replaces the older one without asking");
    }

    [Fact]
    public async Task Copy_OnlyIfNewer_LeavesAnOlderOrEqualFileAlone()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1], Old);
        fs.AddFile("/b/x.txt", [2], New);

        var op = Copy(fs, await EntryAsync(fs, "/a/x.txt"), "/b",
            new FixedStrategy(ConflictResolution.Overwrite), new CopyOptions(OnlyIfNewer: true));
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/x.txt").Should().Equal(2);
        op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task Copy_FolderIntoItsOwnSubfolder_IsRefused()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/work/proj/a.txt", [1]);
        fs.AddDirectory("/work/proj/sub");

        var op = Copy(fs, await EntryAsync(fs, "/work/proj"), "/work/proj/sub",
            new FixedStrategy(ConflictResolution.Overwrite));
        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None))
            .Should().ThrowAsync<DestinationInsideSourceException>();

        op.State.Should().Be(OperationState.Failed);
        fs.HasFile("/work/proj/sub/proj/a.txt").Should().BeFalse("nothing may be nested into itself");
    }

    [Fact]
    public async Task Copy_FileOntoItself_IsSkippedNotTruncated()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1, 2, 3]);
        var strategy = new FixedStrategy(ConflictResolution.Overwrite);

        var op = Copy(fs, await EntryAsync(fs, "/a/x.txt"), "/a", strategy);
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/a/x.txt").Should().Equal(1, 2, 3);
        op.SkippedCount.Should().Be(1);
        strategy.Asked.Should().Be(0);
    }

    [Fact]
    public async Task Copy_JunctionFolder_IsNotFollowedAndIsReported()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/real/f.txt", [1]);
        fs.AddFile("/a/link/loop.txt", [2]);
        var real = await EntryAsync(fs, "/a/real");
        var link = new FileEntry(new VfsPath("/a/link"), "link", true, 0, New,
            FileAttributes.Directory | FileAttributes.ReparsePoint);

        var op = new CopyOperation(fs, fs, [real, link], new VfsPath("/b"),
            new FixedStrategy(ConflictResolution.Overwrite), new CopyOptions(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasFile("/b/real/f.txt").Should().BeTrue();
        fs.HasFile("/b/link/loop.txt").Should().BeFalse();
        op.SkippedCount.Should().Be(1);
        op.Warnings.Should().ContainSingle().Which.Should().Contain("link");
    }

    [Fact]
    public async Task Copy_ItemThatFails_IsSkippedWhenTheUserSaysSkip()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/ok.txt", [1]);
        var ok = await EntryAsync(fs, "/a/ok.txt");
        var ghost = new FileEntry(new VfsPath("/a/ghost.txt"), "ghost.txt", false, 5, New, FileAttributes.Normal);

        var op = new CopyOperation(fs, fs, [ghost, ok], new VfsPath("/b"),
            new FixedStrategy(ConflictResolution.Overwrite), new CopyOptions(), NullLogger<CopyOperation>.Instance)
        {
            ErrorHandler = (_, _) => Task.FromResult(OperationErrorResolution.Skip),
        };
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/ok.txt").Should().Equal(1);
        op.SkippedCount.Should().Be(1);
        op.Warnings.Should().ContainSingle().Which.Should().Contain("ghost.txt");
    }

    [Fact]
    public async Task Copy_ItemThatFails_AbortsWhenTheUserSaysAbort()
    {
        var fs = new InMemoryFileSystem();
        var ghost = new FileEntry(new VfsPath("/a/ghost.txt"), "ghost.txt", false, 5, New, FileAttributes.Normal);

        var op = new CopyOperation(fs, fs, [ghost], new VfsPath("/b"),
            new FixedStrategy(ConflictResolution.Overwrite), new CopyOptions(), NullLogger<CopyOperation>.Instance)
        {
            ErrorHandler = (_, _) => Task.FromResult(OperationErrorResolution.Abort),
        };

        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None)).Should().ThrowAsync<FileNotFoundException>();
        op.State.Should().Be(OperationState.Failed);
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Move_Skip_LeavesTheSourceInPlaceAndCountsTheSkip()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);

        var op = Move(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.Skip));
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasFile("/a/x.txt").Should().BeTrue("a skipped item must stay where it was");
        fs.ReadFile("/b/x.txt").Should().Equal(2);
        op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task Move_OverwriteIfNewerAnswer_ReplacesOnlyWhenTheSourceIsNewer()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/older.txt", [1], Old);
        fs.AddFile("/b/older.txt", [2], New);
        fs.AddFile("/a/newer.txt", [3], New);
        fs.AddFile("/b/newer.txt", [4], Old);
        var strategy = new FixedStrategy(ConflictResolution.OverwriteIfNewer);

        var older = Move(fs, await EntryAsync(fs, "/a/older.txt"), "/b", strategy);
        await older.ExecuteAsync(CancellationToken.None);
        await Move(fs, await EntryAsync(fs, "/a/newer.txt"), "/b", strategy).ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/older.txt").Should().Equal(2);
        fs.HasFile("/a/older.txt").Should().BeTrue();
        older.SkippedCount.Should().Be(1);
        fs.ReadFile("/b/newer.txt").Should().Equal(3);
    }

    [Fact]
    public async Task Move_OverwriteIfNewer_SameMomentInUtcAndLocal_IsNotNewer()
    {
        // TRAP-48: a server's UTC time and the disk's local time of one moment are equal, not "newer".
        var fs = new InMemoryFileSystem();
        var local = new DateTime(2025, 6, 1, 12, 0, 0, DateTimeKind.Local);
        fs.AddFile("/a/x.txt", [1], local.ToUniversalTime());
        fs.AddFile("/b/x.txt", [2], local);

        var op = Move(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.OverwriteIfNewer));
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/x.txt").Should().Equal(2);
        op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task Move_FolderOntoFolder_MergesInsteadOfDeletingTheDestination()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/proj/one.txt", [1]);
        fs.AddFile("/b/proj/keep.txt", [7]);
        var strategy = new FixedStrategy(ConflictResolution.Overwrite);

        var op = Move(fs, await EntryAsync(fs, "/a/proj"), "/b", strategy);
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/proj/one.txt").Should().Equal(1);
        fs.ReadFile("/b/proj/keep.txt").Should().Equal(7);
        fs.HasFile("/a/proj/one.txt").Should().BeFalse();
        fs.HasDirectory("/a/proj").Should().BeFalse("the emptied source folder is removed");
        strategy.Asked.Should().Be(0);
    }

    [Fact]
    public async Task Move_FolderOntoFolder_KeepsTheSourceFolderWhenAFileWasSkipped()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/proj/clash.txt", [1]);
        fs.AddFile("/b/proj/clash.txt", [2]);

        var op = Move(fs, await EntryAsync(fs, "/a/proj"), "/b", new FixedStrategy(ConflictResolution.Skip));
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasFile("/a/proj/clash.txt").Should().BeTrue();
        fs.ReadFile("/b/proj/clash.txt").Should().Equal(2);
        op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task Move_IntoItsOwnSubfolder_IsRefused()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/work/proj/a.txt", [1]);
        fs.AddDirectory("/work/proj/sub");

        var op = Move(fs, await EntryAsync(fs, "/work/proj"), "/work/proj/sub",
            new FixedStrategy(ConflictResolution.Overwrite));

        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None))
            .Should().ThrowAsync<DestinationInsideSourceException>();
        fs.HasFile("/work/proj/a.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Move_CancelAtConflict_EndsCancelled()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);

        var op = Move(fs, await EntryAsync(fs, "/a/x.txt"), "/b", new FixedStrategy(ConflictResolution.Cancel));

        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None)).Should().ThrowAsync<OperationCanceledException>();
        op.State.Should().Be(OperationState.Cancelled);
        fs.HasFile("/a/x.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Move_ItemThatFails_IsSkippedWhenTheUserSaysSkipAll()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/ok.txt", [1]);
        var ok = await EntryAsync(fs, "/a/ok.txt");
        var ghost = new FileEntry(new VfsPath("/a/ghost.txt"), "ghost.txt", false, 5, New, FileAttributes.Normal);

        var op = new MoveOperation(fs, fs, [ghost, ok], new VfsPath("/b"),
            new FixedStrategy(ConflictResolution.Overwrite), NullLogger<MoveOperation>.Instance)
        {
            ErrorHandler = (_, _) => Task.FromResult(OperationErrorResolution.SkipAll),
        };
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasFile("/b/ok.txt").Should().BeTrue();
        op.SkippedCount.Should().Be(1);
    }
}
