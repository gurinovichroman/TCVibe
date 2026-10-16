using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The delete's progress window (6.36): it counts items rather than bytes, Cancel and Pause land
/// between two items, and what was deleted before a Cancel stays deleted.
/// </summary>
public sealed class DeleteOperationProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-del-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _disk = new(NullLogger<WindowsFileSystem>.Instance);

    public DeleteOperationProgressTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leaked handle in a failed test; the OS cleans the temp folder later.
        }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private sealed class Recorder : IProgress<ItemProgress>
    {
        public List<ItemProgress> Reports { get; } = [];
        public void Report(ItemProgress value) => Reports.Add(value);
    }

    private static DeleteOperation Permanent(IVirtualFileSystem fs, params IFileEntry[] entries) =>
        new(fs, entries, moveToRecycleBin: false, NullLogger<DeleteOperation>.Instance);

    private static async Task<IFileEntry> EntryAsync(IVirtualFileSystem fs, string path) =>
        (await fs.GetEntryAsync(new VfsPath(path)))!;

    // ── Counting ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_FolderTree_CountsEveryFileAndFolderItRemoves()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddFile("/work/a.txt", [1]);
        fs.AddFile("/work/sub/b.txt", [2]);
        fs.AddFile("/work/sub/c.txt", [3]);
        fs.AddFile("/top.txt", [4]);
        var recorder = new Recorder();
        var op = Permanent(fs, await EntryAsync(fs, "/work"), await EntryAsync(fs, "/top.txt"));
        op.Progress = recorder;

        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        fs.HasDirectory("/work").Should().BeFalse();
        fs.HasFile("/top.txt").Should().BeFalse();

        // work, a.txt, sub, b.txt, c.txt and top.txt: six items, each removed on its own.
        fs.DeleteCalls.Should().Be(6);
        var counted = recorder.Reports.Where(r => r.Total > 0).ToList();
        counted.Should().NotBeEmpty();
        counted.Should().OnlyContain(r => r.Total == 6 && r.Done <= r.Total);
        counted.Select(r => r.Done).Should().BeInAscendingOrder();
        recorder.Reports[^1].Done.Should().Be(6);
        recorder.Reports[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public async Task Execute_FolderOnAServer_GoesInOneCallAndCountsAsOneItem()
    {
        // Every call is a round trip there; the server's own recursive delete is the cheaper one.
        var fs = new InMemoryFileSystem("sftp");
        fs.AddFile("/www/a.txt", [1]);
        fs.AddFile("/www/b.txt", [2]);
        var recorder = new Recorder();
        var op = Permanent(fs, await EntryAsync(fs, "/www"));
        op.Progress = recorder;

        await op.ExecuteAsync(CancellationToken.None);

        fs.HasDirectory("/www").Should().BeFalse();
        fs.DeleteCalls.Should().Be(1);
        recorder.Reports[^1].Done.Should().Be(1);
        recorder.Reports[^1].Total.Should().Be(1);
    }

    [Fact]
    public async Task Execute_FolderThatCannotBeListed_IsDeletedInOneCallAsBefore()
    {
        var fs = new InMemoryFileSystem("file") { FailListingOf = "/locked" };
        fs.AddFile("/locked/a.txt", [1]);
        var op = Permanent(fs, await EntryAsync(fs, "/locked"));

        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        fs.HasDirectory("/locked").Should().BeFalse();
        fs.HasFile("/locked/a.txt").Should().BeFalse();
        fs.DeleteCalls.Should().Be(1);
    }

    // ── Cancel and Pause ──────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_CancelledAfterTheFirstEntry_KeepsItDeletedAndLeavesTheRest()
    {
        var mem = new InMemoryFileSystem("file");
        mem.AddFile("/a.txt", [1]);
        mem.AddFile("/b.txt", [2]);
        mem.AddFile("/c.txt", [3]);
        using var cts = new CancellationTokenSource();
        var fs = new DeleteHookFileSystem(mem, path =>
        {
            if (path.FileName == "a.txt")
            {
                cts.Cancel();
            }
        });
        var op = Permanent(fs,
            await EntryAsync(fs, "/a.txt"), await EntryAsync(fs, "/b.txt"), await EntryAsync(fs, "/c.txt"));

        var act = () => op.ExecuteAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        op.State.Should().Be(OperationState.Cancelled);
        mem.HasFile("/a.txt").Should().BeFalse("what was deleted before Cancel stays deleted");
        mem.HasFile("/b.txt").Should().BeTrue();
        mem.HasFile("/c.txt").Should().BeTrue();
    }

    [Fact]
    public async Task Execute_CancelledInsideAFolder_StopsBetweenTwoFiles()
    {
        // One big folder is the usual case (node_modules): Cancel must not wait for the whole tree.
        var mem = new InMemoryFileSystem("file");
        for (var i = 1; i <= 5; i++)
        {
            mem.AddFile($"/big/f{i}.txt", [(byte)i]);
        }

        using var cts = new CancellationTokenSource();
        var deletes = 0;
        var fs = new DeleteHookFileSystem(mem, _ =>
        {
            if (++deletes == 1)
            {
                cts.Cancel();
            }
        });
        var op = Permanent(fs, await EntryAsync(fs, "/big"));

        var act = () => op.ExecuteAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        op.State.Should().Be(OperationState.Cancelled);
        deletes.Should().Be(1);
        mem.HasDirectory("/big").Should().BeTrue();
        Enumerable.Range(1, 5).Count(i => mem.HasFile($"/big/f{i}.txt")).Should().Be(4);
    }

    [Fact]
    public async Task Execute_WhilePaused_DeletesNothingUntilResumed()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddFile("/a.txt", [1]);
        using var pause = new PauseHandle();
        pause.Pause();
        var op = Permanent(fs, await EntryAsync(fs, "/a.txt"));
        op.PauseHandle = pause;

        var run = Task.Run(() => op.ExecuteAsync(CancellationToken.None));
        await Task.Delay(100);

        fs.HasFile("/a.txt").Should().BeTrue();
        run.IsCompleted.Should().BeFalse();

        pause.Resume();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        fs.HasFile("/a.txt").Should().BeFalse();
        op.State.Should().Be(OperationState.Completed);
    }

    // ── On the disk ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_LockedFileInAFolderSkipped_KeepsTheFolderWithoutAskingAgain()
    {
        Directory.CreateDirectory(P("proj"));
        File.WriteAllText(P("proj", "busy.txt"), "x");
        File.WriteAllText(P("proj", "free.txt"), "y");
        var prompts = 0;
        var op = new DeleteOperation(_disk, [await EntryAsync(_disk, P("proj"))], moveToRecycleBin: false,
            NullLogger<DeleteOperation>.Instance)
        {
            ErrorHandler = (_, _) =>
            {
                prompts++;
                return Task.FromResult(OperationErrorResolution.Skip);
            },
        };

        using (new FileStream(P("proj", "busy.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await op.ExecuteAsync(CancellationToken.None);
        }

        prompts.Should().Be(1, "the folder still holding the skipped file is not asked about again");
        op.State.Should().Be(OperationState.Completed);
        op.SkippedCount.Should().Be(1);
        File.Exists(P("proj", "busy.txt")).Should().BeTrue();
        File.Exists(P("proj", "free.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Execute_FolderHoldingAJunction_RemovesTheLinkAndKeepsWhatItPointsTo()
    {
        Directory.CreateDirectory(P("target"));
        File.WriteAllText(P("target", "keep.txt"), "k");
        Directory.CreateDirectory(P("doomed"));
        File.WriteAllText(P("doomed", "a.txt"), "a");
        if (!TryCreateJunction(P("doomed", "link"), P("target")))
        {
            return;   // no mklink on this machine
        }

        var op = Permanent(_disk, await EntryAsync(_disk, P("doomed")));
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        Directory.Exists(P("doomed")).Should().BeFalse();
        File.Exists(P("target", "keep.txt")).Should().BeTrue("walking the tree must not enter a junction");
    }

    // ── What the window shows ─────────────────────────────────────────────────

    [Fact]
    public void ItemProgress_HalfwayAfterTenSeconds_EstimatesTenMore()
    {
        var progress = new ItemProgress(5, 10, "x.txt", TimeSpan.FromSeconds(10));

        progress.Percentage.Should().Be(50);
        progress.CountText.Should().Be("5 / 10");
        progress.Remaining.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ItemProgress_TotalNotKnownYet_ShowsNoCountAndNoEstimate()
    {
        var progress = new ItemProgress(0, 0, "node_modules", TimeSpan.FromSeconds(1));

        progress.Percentage.Should().Be(0);
        progress.CountText.Should().BeEmpty();
        progress.Remaining.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>A junction needs no privilege, unlike a symlink. False when even that is unavailable.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Runs a callback after each delete has reached the file system — where a user's Cancel lands.</summary>
    private sealed class DeleteHookFileSystem(InMemoryFileSystem inner, Action<VfsPath> afterDelete) : IVirtualFileSystem
    {
        public string Scheme => inner.Scheme;
        public string DisplayName => inner.DisplayName;

        public event EventHandler<VfsChangedEventArgs>? Changed
        {
            add => inner.Changed += value;
            remove => inner.Changed -= value;
        }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) =>
            inner.ListAsync(path, ct);

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) =>
            inner.GetEntryAsync(path, ct);

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
            inner.OpenReadAsync(path, ct);

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
            inner.OpenWriteAsync(path, overwrite, ct);

        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
            inner.CopyAsync(source, destination, progress, ct);

        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
            inner.MoveAsync(source, destination, ct);

        public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
        {
            await inner.DeleteAsync(path, recursive, ct);
            afterDelete(path);
        }

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => inner.RenameAsync(path, newName, ct);

        public bool SupportsOperation(VfsOperation operation) => inner.SupportsOperation(operation);

        public void Dispose() => inner.Dispose();
    }
}
