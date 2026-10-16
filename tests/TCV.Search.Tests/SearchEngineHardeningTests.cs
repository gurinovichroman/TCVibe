using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Search;

namespace TCV.Search.Tests;

/// <summary>Threading, loops, unreadable folders, parent ignore rules and the other edges of the walk.</summary>
public class SearchEngineHardeningTests
{
    private static IFileEntry Entry(string path, bool isDirectory, FileAttributes attributes = FileAttributes.Normal, long size = 10)
    {
        var mock = new Mock<IFileEntry>();
        mock.SetupGet(e => e.Path).Returns(new VfsPath(path));
        mock.SetupGet(e => e.Name).Returns(path[(path.LastIndexOf('/') + 1)..]);
        mock.SetupGet(e => e.IsDirectory).Returns(isDirectory);
        mock.SetupGet(e => e.Size).Returns(size);
        mock.SetupGet(e => e.LastModified).Returns(new DateTime(2026, 1, 1));
        mock.SetupGet(e => e.Attributes).Returns(isDirectory ? attributes | FileAttributes.Directory : attributes);
        return mock.Object;
    }

    private static async Task<List<SearchResult>> RunAsync(IVirtualFileSystem vfs, SearchCriteria criteria, string root = "/")
    {
        var results = new List<SearchResult>();
        await foreach (var r in new SearchEngine(vfs).SearchAsync(root, criteria))
        {
            results.Add(r);
        }

        return results;
    }

    // ── Name matching ─────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_NameWithoutWildcards_MatchesAnywhereInTheName()
    {
        var vfs = new MemoryVfs()
            .Add("/appsettings.config.json", "x")
            .Add("/readme.md", "x");

        var results = await RunAsync(vfs, new SearchCriteria { NamePattern = "config" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("appsettings.config.json");
    }

    [Fact]
    public async Task SearchAsync_NameWithWildcard_StaysAnchored()
    {
        var vfs = new MemoryVfs().Add("/a.cs", "x").Add("/ba.cs", "x");

        var results = await RunAsync(vfs, new SearchCriteria { NamePattern = "a.*" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("a.cs");
    }

    // ── Loops and unreadable folders ──────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_ReparsePointDirectory_IsListedButNotEntered()
    {
        var mock = new Mock<IVirtualFileSystem>();
        mock.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/loop", true, FileAttributes.ReparsePoint), Entry("/real.txt", false) });
        // A junction back to the root: entering it would list "/" again, forever.
        mock.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/loop"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/loop/real.txt", false) });

        var results = await RunAsync(mock.Object, new SearchCriteria());

        results.Select(r => r.FullPath).Should().BeEquivalentTo("/loop", "/real.txt");
    }

    [Fact]
    public async Task SearchAsync_UnreadableFolder_IsSkippedAndCounted()
    {
        var mock = new Mock<IVirtualFileSystem>();
        mock.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/locked", true), Entry("/open", true), Entry("/a.txt", false) });
        mock.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/locked"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new UnauthorizedAccessException());
        mock.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/open"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/open/b.txt", false) });

        var engine = new SearchEngine(mock.Object);
        var found = new List<string>();
        await foreach (var r in engine.SearchAsync("/", new SearchCriteria { FilesOnly = true }))
        {
            found.Add(r.Entry.Name);
        }

        found.Should().BeEquivalentTo("a.txt", "b.txt");
        engine.UnreadableDirectories.Should().Be(1, "the search reports what it could not read");
    }

    [Fact]
    public async Task SearchAsync_CloudPlaceholder_IsNotOpenedForContent()
    {
        var placeholder = Entry("/cloud.txt", false, (FileAttributes)0x00400000);
        var mock = new Mock<IVirtualFileSystem>();
        mock.Setup(v => v.ListAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { placeholder });

        var results = await RunAsync(mock.Object, new SearchCriteria { ContentText = "needle" });

        results.Should().BeEmpty();
        mock.Verify(v => v.OpenReadAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()), Times.Never,
            "opening a OneDrive placeholder makes Windows download it");
    }

    // ── Parent .gitignore ─────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_FromASubfolder_HonoursTheRepositoryRootGitignore()
    {
        var vfs = new MemoryVfs()
            .Add("/repo/.git/HEAD", "ref")
            .Add("/repo/.gitignore", "*.log\nbuild/\n")
            .Add("/repo/src/app/keep.cs", "needle")
            .Add("/repo/src/app/noise.log", "needle")
            .Add("/repo/src/app/build/out.cs", "needle");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" }, root: "/repo/src/app");

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("keep.cs");
    }

    [Fact]
    public async Task SearchAsync_FromASubfolder_HonoursAnIntermediateGitignoreToo()
    {
        var vfs = new MemoryVfs()
            .Add("/repo/.git/HEAD", "ref")
            .Add("/repo/src/.gitignore", "*.gen.cs\n")
            .Add("/repo/src/app/a.cs", "needle")
            .Add("/repo/src/app/b.gen.cs", "needle");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" }, root: "/repo/src/app");

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("a.cs");
    }

    [Fact]
    public async Task SearchAsync_OutsideARepository_InheritsNothingFromParents()
    {
        var vfs = new MemoryVfs()
            .Add("/notes/.gitignore", "*.log\n")
            .Add("/notes/sub/a.log", "needle");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" }, root: "/notes/sub");

        results.Should().ContainSingle("without a working copy a stray .gitignore above is not ours to apply");
    }

    // ── Regex time budget, threading, cancellation ────────────────────────────

    [Fact]
    public async Task SearchAsync_CatastrophicPattern_SkipsTheFileInsteadOfHanging()
    {
        var hostile = new string('a', 40) + "!";
        var vfs = new MemoryVfs().Add("/evil.txt", hostile).Add("/ok.txt", "needle");
        var criteria = new SearchCriteria { ContentText = "(a+)+$", ContentIsRegex = true };
        var engine = new SearchEngine(vfs);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var _ in engine.SearchAsync("/", criteria)) { }
        watch.Stop();

        engine.TimedOutFiles.Should().BeGreaterThan(0);
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task SearchAsync_ReadingAndMatching_DoNotRunOnTheCallersThread()
    {
        var threads = new HashSet<int>();
        var mock = new Mock<IVirtualFileSystem>();
        mock.Setup(v => v.ListAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                lock (threads)
                {
                    threads.Add(Environment.CurrentManagedThreadId);
                }

                return Task.FromResult<IReadOnlyList<IFileEntry>>(new[] { Entry("/a.txt", false) });
            });
        mock.Setup(v => v.OpenReadAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                lock (threads)
                {
                    threads.Add(Environment.CurrentManagedThreadId);
                }

                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("needle")));
            });

        // A single-threaded context stands in for the UI thread: it must only ever see the results.
        var context = new SingleThreadContext();
        var uiThread = 0;
        await context.RunAsync(async () =>
        {
            uiThread = Environment.CurrentManagedThreadId;
            await foreach (var _ in new SearchEngine(mock.Object).SearchAsync("/", new SearchCriteria { ContentText = "needle" })) { }
        });

        uiThread.Should().NotBe(0);
        threads.Should().NotBeEmpty();
        threads.Should().NotContain(uiThread, "listing, reading and matching belong on the pool (INV-06)");
    }

    [Fact]
    public async Task SearchAsync_MaxResults_CapsContentHitsAndFlagsTruncation()
    {
        var vfs = new MemoryVfs();
        for (var i = 0; i < 50; i++)
        {
            vfs.Add($"/f{i}.txt", "needle\nneedle\n");
        }

        var engine = new SearchEngine(vfs);
        var count = 0;

        await foreach (var _ in engine.SearchAsync("/", new SearchCriteria { ContentText = "needle", MaxResults = 7 }))
        {
            count++;
        }

        count.Should().Be(7);
        engine.WasTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task EnumerateFilesAsync_WalksWithSearchRulesAndFiltersByPredicate()
    {
        var vfs = new MemoryVfs()
            .Add("/.gitignore", "vendor/\n")
            .Add("/a.zip", "x")
            .Add("/docs/b.zip", "x")
            .Add("/vendor/c.zip", "x")
            .Add("/d.txt", "x");
        var found = new List<string>();

        await foreach (var e in new SearchEngine(vfs).EnumerateFilesAsync(
                           "/", new SearchCriteria(), e => e.Name.EndsWith(".zip", StringComparison.Ordinal)))
        {
            found.Add(e.Name);
        }

        found.Should().BeEquivalentTo("a.zip", "b.zip");
    }

    [Fact]
    public async Task SearchAsync_MalformedRegex_ThrowsArgumentExceptionToTheCaller()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "x");
        var act = async () => await RunAsync(vfs, new SearchCriteria { ContentText = "(", ContentIsRegex = true });

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>Runs work on one dedicated thread with a synchronisation context, like a UI thread.</summary>
    private sealed class SingleThreadContext : SynchronizationContext
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback, object?)> _queue = new();

        public Task RunAsync(Func<Task> body)
        {
            var done = new TaskCompletionSource();
            var thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                _queue.Add((_ => _ = Pump(body, done), null));
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            }) { IsBackground = true };
            thread.Start();
            return done.Task;
        }

        private async Task Pump(Func<Task> body, TaskCompletionSource done)
        {
            try { await body(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
            finally { _queue.CompleteAdding(); }
        }

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    }
}
