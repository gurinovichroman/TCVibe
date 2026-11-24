using FluentAssertions;
using TCV.Archives.Engines;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using CoreEngine = TCV.Core.PluginSdk.IArchiveEngine;

namespace TCV.Archives.Tests;

/// <summary>
/// Announced entries come from one pass over the archive (6.42): a solid archive is not decoded from its
/// start for every file, and whatever goes wrong with the pass, a read still gets the file or its reason.
/// </summary>
public sealed class ArchiveReadAheadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-ahead-" + Guid.NewGuid().ToString("N"));
    private readonly string _tar;

    public ArchiveReadAheadTests()
    {
        Directory.CreateDirectory(_dir);
        _tar = Path.Combine(_dir, "t.tar");
        var files = Enumerable.Range(0, 5).Select(i =>
        {
            var path = Path.Combine(_dir, $"f{i}.txt");
            File.WriteAllText(path, new string((char)('a' + i), 3 + i));
            return (path, $"dir/f{i}.txt");
        }).ToList();
        new TarEngine().Pack(_tar, files);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IReadOnlyList<VfsPath> Paths(params int[] files) => files.Select(i => new VfsPath($"/dir/f{i}.txt")).ToList();

    private static async Task<string> ReadAsync(IVirtualFileSystem fs, int file)
    {
        await using var content = await fs.OpenReadAsync(new VfsPath($"/dir/f{file}.txt"));
        using var reader = new StreamReader(content);
        return await reader.ReadToEndAsync();
    }

    private static string Text(int file) => new((char)('a' + file), 3 + file);

    [Fact]
    public async Task OpenRead_Announced_AllComeFromOnePass_InAnyOrder()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        using (fs.ReadAhead(Paths(0, 1, 2, 3, 4)))
        {
            foreach (var file in new[] { 3, 0, 4, 1, 2 })
            {
                (await ReadAsync(fs, file)).Should().Be(Text(file));
            }
        }

        engine.ReadPasses.Should().Be(1);
        engine.SingleReads.Should().Be(0);
    }

    [Fact]
    public async Task OpenRead_NothingAnnounced_ReadsEachFileOnItsOwn()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        await ReadAsync(fs, 0);
        await ReadAsync(fs, 1);

        engine.ReadPasses.Should().Be(0);
        engine.SingleReads.Should().Be(2);
    }

    [Fact]
    public async Task OpenRead_FileNotAnnounced_IsReadOnItsOwnBesideThePass()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        using (fs.ReadAhead(Paths(0, 1)))
        {
            (await ReadAsync(fs, 3)).Should().Be(Text(3));
        }

        engine.ReadPasses.Should().Be(0, "nothing announced was read");
        engine.SingleReads.Should().Be(1);
    }

    [Fact]
    public void ReadAhead_NeverRead_StartsNothing()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        fs.ReadAhead(Paths(0, 1, 2)).Dispose();

        engine.ReadPasses.Should().Be(0);
    }

    [Fact]
    public async Task ReadAhead_OneFile_IsAnOrdinaryRead()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        using (fs.ReadAhead(Paths(2)))
        {
            (await ReadAsync(fs, 2)).Should().Be(Text(2));
        }

        engine.ReadPasses.Should().Be(0);
        engine.SingleReads.Should().Be(1);
    }

    [Fact]
    public async Task OpenRead_SameFileTwice_GetsItsContentBothTimes()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        using (fs.ReadAhead(Paths(0, 1)))
        {
            (await ReadAsync(fs, 1)).Should().Be(Text(1));
            (await ReadAsync(fs, 1)).Should().Be(Text(1));
        }

        engine.ReadPasses.Should().Be(1);
    }

    [Fact]
    public async Task OpenRead_PassFails_TheFileIsReadFromTheArchiveAfterAll()
    {
        var engine = new CountingEngine(new TarEngine()) { FailPass = new InvalidDataException("damaged") };
        using var fs = new ArchiveFileSystem(_tar, engine);

        using (fs.ReadAhead(Paths(0, 1)))
        {
            (await ReadAsync(fs, 1)).Should().Be(Text(1));
        }

        engine.SingleReads.Should().Be(1);
    }

    [Fact]
    public async Task OpenRead_PassLeavesAFileOut_ItsOwnReadSaysWhy()
    {
        using var fs = new ArchiveFileSystem(_tar, new TarEngine());

        using (fs.ReadAhead([new VfsPath("/dir/f0.txt"), new VfsPath("/dir/gone.txt")]))
        {
            var act = () => fs.OpenReadAsync(new VfsPath("/dir/gone.txt"));

            await act.Should().ThrowAsync<FileNotFoundException>();
        }
    }

    [Fact]
    public async Task OpenRead_AfterInvalidate_ReadsTheChangedArchiveNotWhatWasReadAhead()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);

        using (fs.ReadAhead(Paths(0, 1)))
        {
            await ReadAsync(fs, 0);
            fs.Invalidate();
            await ReadAsync(fs, 1);
        }

        engine.ReadPasses.Should().Be(1);
        engine.SingleReads.Should().Be(1);
    }

    [Fact]
    public async Task StreamOpenWhenTheOwnerLetsGo_StaysReadable()
    {
        using var fs = new ArchiveFileSystem(_tar, new TarEngine());

        Stream open;
        using (fs.ReadAhead(Paths(0, 1)))
        {
            open = await fs.OpenReadAsync(new VfsPath("/dir/f1.txt"));
        }

        await using (open)
        {
            using var reader = new StreamReader(open);
            (await reader.ReadToEndAsync()).Should().Be(Text(1));
        }
    }

    [Fact]
    public async Task Dispose_DuringThePass_StopsIt()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);
        var release = new TaskCompletionSource();
        engine.BeforeEachEntry = () => release.Task.Wait(TimeSpan.FromSeconds(10));

        var scope = fs.ReadAhead(Paths(0, 1));
        var reading = fs.OpenReadAsync(new VfsPath("/dir/f1.txt"));
        await engine.PassStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        scope.Dispose();
        release.SetResult();

        // The waiting read goes to the archive itself rather than hanging with the pass.
        await using var content = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        using var reader = new StreamReader(content);
        (await reader.ReadToEndAsync()).Should().Be(Text(1));
    }

    [Fact]
    public async Task OpenRead_WaitingForThePass_HearsItsToken()
    {
        var engine = new CountingEngine(new TarEngine());
        using var fs = new ArchiveFileSystem(_tar, engine);
        var release = new TaskCompletionSource();
        engine.BeforeEachEntry = () => release.Task.Wait(TimeSpan.FromSeconds(10));
        using var cts = new CancellationTokenSource();

        using (fs.ReadAhead(Paths(0, 1)))
        {
            var reading = fs.OpenReadAsync(new VfsPath("/dir/f1.txt"), cts.Token);
            await engine.PassStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await cts.CancelAsync();

            var act = () => reading;
            await act.Should().ThrowAsync<OperationCanceledException>();
            release.SetResult();
        }
    }

    [Fact]
    public async Task ReadAhead_Cab_RunsExpandOnceForTheWholeBatch()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"), ("sub\\b.txt", "BBB"), ("sub\\c.txt", "CCC"));
        var engine = new CountingEngine(new CabEngine());
        using var fs = new ArchiveFileSystem(cab, engine);

        using (fs.ReadAhead([new VfsPath("/a.txt"), new VfsPath("/sub/b.txt"), new VfsPath("/sub/c.txt")]))
        {
            await using var content = await fs.OpenReadAsync(new VfsPath("/sub/c.txt"));
            using var reader = new StreamReader(content);
            (await reader.ReadToEndAsync()).Should().Be("CCC");
        }

        engine.ReadPasses.Should().Be(1);
        engine.SingleReads.Should().Be(0);
    }

    [Fact]
    public void ReadEntries_Cab_HandsOverEachRequestedFileOnceAndLeavesOutTheRest()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"), ("sub\\b.txt", "BBB"), ("sub\\c.txt", "CCC"));
        var handed = new Dictionary<string, string>();

        ((CoreEngine)new CabEngine()).ReadEntries(cab, ["sub/c.txt", "a.txt", "gone.txt"],
            (path, content) => handed.Add(path, new StreamReader(content).ReadToEnd()), CancellationToken.None);

        handed.Should().BeEquivalentTo(new Dictionary<string, string> { ["a.txt"] = "AAA", ["sub/c.txt"] = "CCC" });
    }

    [Fact]
    public void ExtractEntries_Cab_WritesEveryDestination_AndReportsWhatIsMissing()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"), ("sub\\b.txt", "BBB"));
        var one = Path.Combine(_dir, "out", "one.txt");
        var two = Path.Combine(_dir, "out", "two.txt");
        var three = Path.Combine(_dir, "out", "three.txt");
        var engine = (CoreEngine)new CabEngine();

        engine.ExtractEntries(cab, [("a.txt", one), ("a.txt", two), ("sub/b.txt", three)], CancellationToken.None);
        var missing = () => engine.ExtractEntries(cab, [("a.txt", one), ("gone.txt", two)], CancellationToken.None);

        File.ReadAllText(two).Should().Be("AAA");
        File.ReadAllText(three).Should().Be("BBB");
        missing.Should().Throw<FileNotFoundException>();
    }

    /// <summary>Tells a single read from a pass, the two things this is all about.</summary>
    private sealed class CountingEngine(CoreEngine inner) : CoreEngine
    {
        public int ReadPasses { get; private set; }

        public int SingleReads { get; private set; }

        public Exception? FailPass { get; init; }

        public Action? BeforeEachEntry { get; set; }

        public TaskCompletionSource PassStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool CanWrite => inner.CanWrite;

        public IReadOnlyList<TCV.Core.PluginSdk.ArchiveEntry> ListAll(string archivePath) => inner.ListAll(archivePath);

        public Stream ExtractEntry(string archivePath, string entryPath)
        {
            SingleReads++;
            return inner.ExtractEntry(archivePath, entryPath);
        }

        public void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume, CancellationToken ct)
        {
            ReadPasses++;
            PassStarted.TrySetResult();
            if (FailPass is not null)
            {
                throw FailPass;
            }

            inner.ReadEntries(archivePath, entryPaths, (path, content) =>
            {
                BeforeEachEntry?.Invoke();
                consume(path, content);
            }, ct);
        }

        public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files) => throw new NotSupportedException();

        public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) => throw new NotSupportedException();

        public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files) => throw new NotSupportedException();

        public void DeleteEntry(string archivePath, string entryPath) => throw new NotSupportedException();
    }
}
