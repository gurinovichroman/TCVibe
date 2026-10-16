using System.Collections.Concurrent;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>"Parallel copy" works file by file, and ends where the sequential copy ends.</summary>
public class ParallelCopyTests
{
    private const int Threads = 4;

    // ── Plan ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CopyPlan_Tree_ListsEveryFolderBeforeWhatItHolds()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/src/a/1.txt", [1]);
        fs.AddFile("/src/a/b/2.txt", [2]);
        fs.AddDirectory("/src/a/b/empty");
        fs.AddFile("/src/top.txt", [3]);
        var selected = new[] { await EntryAsync(fs, "/src/a"), await EntryAsync(fs, "/src/top.txt") };

        var plan = await CopyPlan.BuildAsync(selected, (p, ct) => fs.ListAsync(p, ct), CancellationToken.None);

        plan.Items.Select(i => i.Entry.Path.Value).Should().Equal(
            "/src/a", "/src/a/b", "/src/a/b/empty", "/src/a/b/2.txt", "/src/a/1.txt", "/src/top.txt");
        plan.Items[0].IsSelected.Should().BeTrue();
        plan.Items[^1].IsSelected.Should().BeTrue();
        for (var i = 0; i < plan.Items.Count; i++)
        {
            var item = plan.Items[i];
            if (item.IsSelected)
            {
                continue;
            }
            item.Parent.Should().BeLessThan(i, "a folder is planned before what it holds");
            item.Entry.Path.Parent.Should().Be(plan.Items[item.Parent].Entry.Path);
        }
        plan.Directories.Should().BeInAscendingOrder().And.HaveCount(3);
        plan.Files.Select(i => plan.Items[i].Entry.Name).Should().Equal("2.txt", "1.txt", "top.txt");
    }

    [Fact]
    public async Task CopyPlan_Junction_IsListedButNotEntered()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/src/link/loop.txt", [1]);
        var link = new FileEntry(new VfsPath("/src/link"), "link", true, 0, DateTime.Now,
            FileAttributes.Directory | FileAttributes.ReparsePoint);

        var plan = await CopyPlan.BuildAsync([link], (p, ct) => fs.ListAsync(p, ct), CancellationToken.None);

        plan.Items.Should().ContainSingle().Which.Entry.Should().BeSameAs(link);
        plan.Files.Should().BeEmpty();
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_ParallelSingleFolder_CopiesSeveralFilesAtOnce()
    {
        var source = new SlowFileSystem();
        for (var i = 0; i < 16; i++)
        {
            source.AddFile($"/src/big/f{i:00}.bin", Content(i));
        }
        var dest = new SlowFileSystem();
        dest.AddDirectory("/dst");

        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], new OverwriteStrategy());
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        source.MaxInFlight.Should().BeGreaterThan(1, "the files of one selected folder are copied side by side")
            .And.BeLessThanOrEqualTo(Threads);
        for (var i = 0; i < 16; i++)
        {
            dest.ReadFile($"/dst/big/f{i:00}.bin").Should().Equal(Content(i));
        }
    }

    [Fact]
    public async Task Copy_ParallelTree_EndsWhereTheSequentialCopyEnds()
    {
        static SlowFileSystem Destination()
        {
            var fs = new SlowFileSystem();
            fs.AddFile("/dst/a/1.txt", [9, 9]);     // replaced
            fs.AddFile("/dst/a/b/old.txt", [8]);    // kept: folders merge
            return fs;
        }

        var source = new SlowFileSystem { TransferTime = TimeSpan.FromMilliseconds(5) };
        source.AddFile("/src/a/1.txt", [1]);
        source.AddFile("/src/a/2.txt", [2]);
        source.AddFile("/src/a/b/3.txt", [3]);
        source.AddDirectory("/src/a/b/c");
        for (var i = 0; i < 6; i++)
        {
            source.AddFile($"/src/a/d/{i}.txt", Content(i));
        }
        source.AddFile("/src/top.txt", [4]);
        var selected = new[] { await EntryAsync(source, "/src/a"), await EntryAsync(source, "/src/top.txt") };

        var sequentialDest = Destination();
        var sequential = Copy(source, sequentialDest, selected, new OverwriteStrategy(), threads: 1);
        await sequential.ExecuteAsync(CancellationToken.None);

        var parallelDest = Destination();
        var parallel = Copy(source, parallelDest, selected, new OverwriteStrategy());
        await parallel.ExecuteAsync(CancellationToken.None);

        (await SnapshotAsync(parallelDest)).Should().Equal(await SnapshotAsync(sequentialDest));
        parallel.SkippedCount.Should().Be(sequential.SkippedCount);
        parallel.State.Should().Be(OperationState.Completed);
    }

    [Fact]
    public async Task Copy_ParallelConflictsAnsweredForAll_AskOnce()
    {
        var source = new SlowFileSystem();
        var dest = new SlowFileSystem();
        for (var i = 0; i < 12; i++)
        {
            source.AddFile($"/src/big/f{i}.bin", Content(i));
            dest.AddFile($"/dst/big/f{i}.bin", [0xFF]);
        }

        // The conflict dialog with "apply to all" ticked — without a gate of its own, so it is the
        // copy that has to keep the questions one at a time.
        ConflictResolution? forAll = null;
        int asked = 0, open = 0, mostOpen = 0;
        var strategy = new AskUserStrategy(async (_, _) =>
        {
            if (forAll is { } answer)
            {
                return answer;
            }

            Interlocked.Increment(ref asked);
            var now = Interlocked.Increment(ref open);
            if (now > mostOpen)
            {
                mostOpen = now;
            }
            await Task.Delay(50);   // the user reads the dialog
            Interlocked.Decrement(ref open);

            forAll = ConflictResolution.Overwrite;
            return ConflictResolution.Overwrite;
        });

        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], strategy);
        await op.ExecuteAsync(CancellationToken.None);

        asked.Should().Be(1, "\"apply to all\" reaches the files that were already waiting");
        mostOpen.Should().Be(1, "only one conflict dialog may be open at a time");
        for (var i = 0; i < 12; i++)
        {
            dest.ReadFile($"/dst/big/f{i}.bin").Should().Equal(Content(i));
        }
    }

    [Fact]
    public async Task Copy_ParallelRenameOntoTheNameOfASibling_KeepsEveryFile()
    {
        // «a.txt» is taken, so it becomes «a (1).txt» — the name of a sibling copied at the same time.
        var source = new SlowFileSystem();
        source.AddFile("/src/d/a.txt", [1]);
        source.AddFile("/src/d/a (1).txt", [2]);
        var dest = new SlowFileSystem();
        dest.AddFile("/dst/d/a.txt", [9]);

        var op = Copy(source, dest, [await EntryAsync(source, "/src/d")], new RenameAutoStrategy());
        await op.ExecuteAsync(CancellationToken.None);

        var landed = await dest.ListAsync(new VfsPath("/dst/d"));
        landed.Should().HaveCount(3);
        // Which of the two gets the new name depends on which starts first; neither may be lost.
        landed.Select(e => dest.ReadFile(e.Path.Value)[0]).Order().Should().Equal(1, 2, 9);
    }

    [Fact]
    public async Task Copy_ParallelCancelled_StopsWithoutHalfWrittenFiles()
    {
        using var cts = new CancellationTokenSource();
        var source = new SlowFileSystem { TransferTime = TimeSpan.FromMilliseconds(30) };
        for (var i = 0; i < 40; i++)
        {
            source.AddFile($"/src/big/f{i:00}.bin", Content(i));
        }
        source.OnRead(3, cts.Cancel);
        var dest = new SlowFileSystem();
        dest.AddDirectory("/dst");

        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], new OverwriteStrategy());
        await op.Invoking(o => o.ExecuteAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        op.State.Should().Be(OperationState.Cancelled);
        var landed = await dest.ListAsync(new VfsPath("/dst/big"));
        landed.Count.Should().BeLessThan(40);
        foreach (var file in landed)
        {
            dest.ReadFile(file.Path.Value).Should().Equal(source.ReadFile("/src/big/" + file.Name));
        }
    }

    [Fact]
    public async Task Copy_ParallelPaused_CopiesNothingUntilResumed()
    {
        var source = new SlowFileSystem();
        for (var i = 0; i < 8; i++)
        {
            source.AddFile($"/src/big/f{i}.bin", Content(i));
        }
        var dest = new SlowFileSystem();
        dest.AddDirectory("/dst");
        using var pause = new PauseHandle();
        pause.Pause();

        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], new OverwriteStrategy());
        op.PauseHandle = pause;
        var running = Task.Run(() => op.ExecuteAsync(CancellationToken.None));
        await Task.Delay(200);

        dest.HasDirectory("/dst/big").Should().BeFalse("nothing moves while paused");
        running.IsCompleted.Should().BeFalse();

        pause.Resume();
        await running;
        (await dest.ListAsync(new VfsPath("/dst/big"))).Should().HaveCount(8);
    }

    [Fact]
    public async Task Copy_ParallelFilesThatFail_AreSkippedAndCounted()
    {
        var source = new SlowFileSystem();
        for (var i = 0; i < 10; i++)
        {
            source.AddFile($"/src/big/f{i}.bin", Content(i));
        }
        source.Unreadable.UnionWith(["f2.bin", "f5.bin", "f7.bin"]);
        var dest = new SlowFileSystem();
        dest.AddDirectory("/dst");

        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], new OverwriteStrategy());
        op.ErrorHandler = (_, _) => Task.FromResult(OperationErrorResolution.Skip);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        op.SkippedCount.Should().Be(3);
        op.Warnings.Should().HaveCount(3);
        (await dest.ListAsync(new VfsPath("/dst/big"))).Select(e => e.Name)
            .Should().NotContain(["f2.bin", "f5.bin", "f7.bin"]).And.HaveCount(7);
    }

    [Fact]
    public async Task Copy_ParallelOnlyIfNewer_SkipsTheOlderFilesAndCountsThem()
    {
        var older = new DateTime(2020, 1, 1);
        var newer = new DateTime(2025, 1, 1);
        var source = new SlowFileSystem();
        var dest = new SlowFileSystem();
        for (var i = 0; i < 8; i++)
        {
            source.AddFile($"/src/big/f{i}.bin", Content(i), i % 2 == 0 ? newer : older);
            dest.AddFile($"/dst/big/f{i}.bin", [0xFF], new DateTime(2022, 1, 1));
        }

        var strategy = new AskUserStrategy((_, _) => throw new InvalidOperationException("a sync never asks"));
        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], strategy, onlyIfNewer: true);
        await op.ExecuteAsync(CancellationToken.None);

        op.SkippedCount.Should().Be(4);
        for (var i = 0; i < 8; i++)
        {
            dest.ReadFile($"/dst/big/f{i}.bin").Should().Equal(i % 2 == 0 ? Content(i) : [0xFF]);
        }
    }

    [Fact]
    public async Task Copy_ParallelFolderLeftOut_LeavesOutWhatItHoldsAndStillReachesTheTotal()
    {
        var source = new SlowFileSystem();
        for (var i = 0; i < 6; i++)
        {
            source.AddFile($"/src/d/f{i}.bin", Content(i));
        }
        source.AddFile("/src/x.bin", Content(7));
        var dest = new SlowFileSystem();
        dest.AddFile("/dst/d", [0xFF]);   // a file where the folder would go
        var progress = new ProgressRecorder();

        var op = Copy(source, dest, [await EntryAsync(source, "/src/d"), await EntryAsync(source, "/src/x.bin")],
            new SkipStrategy());
        op.Progress = progress;
        await op.ExecuteAsync(CancellationToken.None);

        op.SkippedCount.Should().Be(1);
        dest.ReadFile("/dst/d").Should().Equal(0xFF);
        dest.ReadFile("/dst/x.bin").Should().Equal(Content(7));
        progress.Reports.Last().BytesCopied.Should().Be(progress.Reports.Last().TotalBytes);
    }

    [Fact]
    public async Task Copy_ParallelProgress_NeverStepsBackAndEndsAtTheTotal()
    {
        var source = new SlowFileSystem { TransferTime = TimeSpan.FromMilliseconds(15) };
        long total = 0;
        for (var i = 0; i < 30; i++)
        {
            var content = new byte[1000 + i * 37];
            source.AddFile($"/src/big/f{i:00}.bin", content);
            total += content.Length;
        }
        var dest = new SlowFileSystem();
        dest.AddDirectory("/dst");
        var progress = new ProgressRecorder();

        var op = Copy(source, dest, [await EntryAsync(source, "/src/big")], new OverwriteStrategy());
        op.Progress = progress;
        await op.ExecuteAsync(CancellationToken.None);

        var reports = progress.Reports;
        reports.Should().NotBeEmpty().And.OnlyContain(r => r.TotalBytes == total);
        reports.Select(r => r.BytesCopied).Should().BeInAscendingOrder();
        reports.Last().BytesCopied.Should().Be(total);
    }

    [Fact]
    public async Task Copy_ParallelOnDisk_KeepsFolderAndFileTimes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"tcv_pcopy_{Guid.NewGuid():N}");
        try
        {
            var src = Path.Combine(root, "src", "tree");
            Directory.CreateDirectory(Path.Combine(src, "sub"));
            for (var i = 0; i < 6; i++)
            {
                await File.WriteAllBytesAsync(Path.Combine(src, i % 2 == 0 ? "" : "sub", $"f{i}.bin"), Content(i));
            }
            var stamp = new DateTime(2021, 3, 4, 5, 6, 7);
            foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTime(file, stamp);
            }
            Directory.SetLastWriteTime(Path.Combine(src, "sub"), stamp);
            Directory.SetLastWriteTime(src, stamp);
            var dst = Path.Combine(root, "dst");
            Directory.CreateDirectory(dst);

            var fs = new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance);
            var op = new CopyOperation(fs, fs, [(await fs.GetEntryAsync(new VfsPath(src)))!], new VfsPath(dst),
                new OverwriteStrategy(), new CopyOptions(Threads: Threads), NullLogger<CopyOperation>.Instance);
            await op.ExecuteAsync(CancellationToken.None);

            var copied = Path.Combine(dst, "tree");
            Directory.GetFiles(copied, "*", SearchOption.AllDirectories).Should().HaveCount(6)
                .And.OnlyContain(f => File.GetLastWriteTime(f) == stamp);
            Directory.GetLastWriteTime(Path.Combine(copied, "sub")).Should().Be(stamp);
            Directory.GetLastWriteTime(copied).Should().Be(stamp, "a folder's time is set after its files land");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static CopyOperation Copy(
        IVirtualFileSystem source, IVirtualFileSystem dest, IReadOnlyList<IFileEntry> selected,
        IConflictStrategy strategy, int threads = Threads, bool onlyIfNewer = false) =>
        new(source, dest, selected, new VfsPath("/dst"), strategy,
            new CopyOptions(OnlyIfNewer: onlyIfNewer, Threads: threads), NullLogger<CopyOperation>.Instance);

    private static async Task<IFileEntry> EntryAsync(IVirtualFileSystem fs, string path) =>
        (await fs.GetEntryAsync(new VfsPath(path)))!;

    private static byte[] Content(int seed) => Enumerable.Range(0, 64).Select(b => (byte)(b * 7 + seed)).ToArray();

    /// <summary>Every folder and file under the root, with the files' bytes, in a stable order.</summary>
    private static async Task<List<string>> SnapshotAsync(SlowFileSystem fs)
    {
        var lines = new List<string>();
        await WalkAsync(VfsPath.Root);
        lines.Sort(StringComparer.Ordinal);
        return lines;

        async Task WalkAsync(VfsPath dir)
        {
            foreach (var entry in await fs.ListAsync(dir))
            {
                if (entry.IsDirectory)
                {
                    lines.Add($"D {entry.Path.Value}");
                    await WalkAsync(entry.Path);
                }
                else
                {
                    lines.Add($"F {entry.Path.Value} {Convert.ToHexString(fs.ReadFile(entry.Path.Value))}");
                }
            }
        }
    }

    private sealed class ProgressRecorder : IProgress<FileProgress>
    {
        private readonly ConcurrentQueue<FileProgress> _reports = new();
        public IReadOnlyList<FileProgress> Reports => _reports.ToArray();
        public void Report(FileProgress value) => _reports.Enqueue(value);
    }

    /// <summary>
    /// An <see cref="InMemoryFileSystem"/> that several threads may share, whose transfers take a
    /// while (so parallel ones overlap) and which remembers how many were running at once.
    /// </summary>
    private sealed class SlowFileSystem : IVirtualFileSystem
    {
        private readonly InMemoryFileSystem _inner = new();
        private readonly object _lock = new();
        private int _inFlight;
        private int _maxInFlight;
        private int _reads;
        private int _readTrigger = int.MaxValue;
        private Action? _onRead;

        public TimeSpan TransferTime { get; init; } = TimeSpan.FromMilliseconds(40);
        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        /// <summary>File names that cannot be read, as a file another program has locked.</summary>
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Runs <paramref name="action"/> once the <paramref name="count"/>-th read has started.</summary>
        public void OnRead(int count, Action action)
        {
            _readTrigger = count;
            _onRead = action;
        }

        public void AddFile(string path, byte[] content) => Locked(() => _inner.AddFile(path, content));
        public void AddFile(string path, byte[] content, DateTime modified) => Locked(() => _inner.AddFile(path, content, modified));
        public void AddDirectory(string path) => Locked(() => _inner.AddDirectory(path));
        public bool HasDirectory(string path) => Locked(() => _inner.HasDirectory(path));
        public byte[] ReadFile(string path) => Locked(() => _inner.ReadFile(path));

        private void Locked(Action action) => Locked(() => { action(); return 0; });

        private T Locked<T>(Func<T> func)
        {
            lock (_lock)
            {
                return func();
            }
        }

        public string Scheme => "mem";
        public string DisplayName => "mem://slow";

        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
        {
            return Locked(() => _inner.ListAsync(path, ct));
        }

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
        {
            return Locked(() => _inner.GetEntryAsync(path, ct));
        }

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
        {
            if (Unreadable.Contains(path.FileName))
            {
                throw new IOException($"{path.FileName} is locked");
            }

            var content = Locked(() => _inner.HasFile(path.Value) ? _inner.ReadFile(path.Value) : null)
                ?? throw new FileNotFoundException(path.Value);

            if (Interlocked.Increment(ref _reads) == _readTrigger)
            {
                _onRead?.Invoke();
            }
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)))
            {
                Interlocked.CompareExchange(ref _maxInFlight, now, seen);
            }

            return Task.FromResult<Stream>(new SlowReadStream(content, this));
        }

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new LandingStream(this, path.Value));

        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
        {
            return Locked(() => _inner.CopyAsync(source, destination, progress, ct));
        }

        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
        {
            return Locked(() => _inner.MoveAsync(source, destination, ct));
        }

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
        {
            return Locked(() => _inner.DeleteAsync(path, recursive, ct));
        }

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
        {
            return Locked(() => _inner.CreateDirectoryAsync(path, ct));
        }

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
        {
            return Locked(() => _inner.RenameAsync(path, newName, ct));
        }

        public bool SupportsOperation(VfsOperation operation) => true;

        public void Dispose() { }

        /// <summary>Takes <see cref="TransferTime"/> before handing out the bytes, then counts itself done.</summary>
        private sealed class SlowReadStream(byte[] content, SlowFileSystem owner) : MemoryStream(content, writable: false)
        {
            private bool _waited;
            private bool _closed;

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!_waited)
                {
                    _waited = true;
                    await Task.Delay(owner.TransferTime, cancellationToken);
                }
                return Read(buffer.Span);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !_closed)
                {
                    _closed = true;
                    Interlocked.Decrement(ref owner._inFlight);
                }
                base.Dispose(disposing);
            }
        }

        /// <summary>The written bytes appear in the file system only once the stream is closed.</summary>
        private sealed class LandingStream(SlowFileSystem owner, string path) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    var bytes = ToArray();
                    owner.AddFile(path, bytes);
                }
                base.Dispose(disposing);
            }
        }
    }
}
