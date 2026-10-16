using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Search;

namespace TCV.Search.Tests;

public class SearchEngineTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static IFileEntry MakeFile(string path, string name, long size = 100,
        DateTime? modified = null, System.IO.FileAttributes attrs = System.IO.FileAttributes.Normal)
    {
        var mock = new Mock<IFileEntry>();
        mock.SetupGet(e => e.Path).Returns(new VfsPath(path));
        mock.SetupGet(e => e.Name).Returns(name);
        mock.SetupGet(e => e.IsDirectory).Returns(false);
        mock.SetupGet(e => e.Size).Returns(size);
        mock.SetupGet(e => e.LastModified).Returns(modified ?? DateTime.Now);
        mock.SetupGet(e => e.Attributes).Returns(attrs);
        return mock.Object;
    }

    private static IFileEntry MakeDir(string path, string name)
    {
        var mock = new Mock<IFileEntry>();
        mock.SetupGet(e => e.Path).Returns(new VfsPath(path));
        mock.SetupGet(e => e.Name).Returns(name);
        mock.SetupGet(e => e.IsDirectory).Returns(true);
        mock.SetupGet(e => e.Size).Returns(0L);
        mock.SetupGet(e => e.LastModified).Returns(DateTime.Now);
        mock.SetupGet(e => e.Attributes).Returns(System.IO.FileAttributes.Directory);
        return mock.Object;
    }

    private static IVirtualFileSystem MakeVfs(
        (string path, IReadOnlyList<IFileEntry> entries)[] listing)
    {
        var mock = new Mock<IVirtualFileSystem>();
        foreach (var (path, entries) in listing)
        {
            mock.Setup(v => v.ListAsync(
                    It.Is<VfsPath>(p => p.Value == path), It.IsAny<CancellationToken>()))
                .ReturnsAsync(entries);
        }
        return mock.Object;
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_MatchesAllFiles_WhenNoCriteria()
    {
        var file1 = MakeFile("/a.txt", "a.txt");
        var file2 = MakeFile("/b.txt", "b.txt");
        var vfs = MakeVfs([("/", new[] { file1, file2 })]);

        var engine = new SearchEngine(vfs);
        var results = new List<SearchResult>();
        await foreach (var r in engine.SearchAsync("/", new SearchCriteria()))
            results.Add(r);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_NameWildcard_FiltersCorrectly()
    {
        var txtFile = MakeFile("/a.txt", "a.txt");
        var mdFile = MakeFile("/b.md", "b.md");
        var vfs = MakeVfs([("/", new[] { txtFile, mdFile })]);

        var engine = new SearchEngine(vfs);
        var criteria = new SearchCriteria { NamePattern = "*.txt" };
        var results = new List<SearchResult>();
        await foreach (var r in engine.SearchAsync("/", criteria))
            results.Add(r);

        results.Should().HaveCount(1);
        results[0].Entry.Name.Should().Be("a.txt");
    }

    [Fact]
    public async Task SearchAsync_SizeFilter_ReturnsOnlyMatchingFiles()
    {
        var small = MakeFile("/small.txt", "small.txt", size: 100);
        var large = MakeFile("/large.txt", "large.txt", size: 100_000);
        var vfs = MakeVfs([("/", new[] { small, large })]);

        var engine = new SearchEngine(vfs);
        var criteria = new SearchCriteria { MaxSizeBytes = 1000 };
        var results = new List<SearchResult>();
        await foreach (var r in engine.SearchAsync("/", criteria))
            results.Add(r);

        results.Should().HaveCount(1);
        results[0].Entry.Name.Should().Be("small.txt");
    }

    [Fact]
    public async Task SearchAsync_DateFilter_ReturnsOnlyRecentFiles()
    {
        var old = MakeFile("/old.txt", "old.txt", modified: new DateTime(2020, 1, 1));
        var recent = MakeFile("/new.txt", "new.txt", modified: DateTime.Now);
        var vfs = MakeVfs([("/", new[] { old, recent })]);

        var engine = new SearchEngine(vfs);
        var criteria = new SearchCriteria { ModifiedAfter = new DateTime(2024, 1, 1) };
        var results = new List<SearchResult>();
        await foreach (var r in engine.SearchAsync("/", criteria))
            results.Add(r);

        results.Should().HaveCount(1);
        results[0].Entry.Name.Should().Be("new.txt");
    }

    [Fact]
    public async Task SearchAsync_ExcludesHiddenByDefault()
    {
        var visible = MakeFile("/visible.txt", "visible.txt");
        var hidden = MakeFile("/hidden.txt", "hidden.txt",
            attrs: System.IO.FileAttributes.Hidden);
        var vfs = MakeVfs([("/", new[] { visible, hidden })]);

        var engine = new SearchEngine(vfs);
        var results = new List<SearchResult>();
        await foreach (var r in engine.SearchAsync("/", new SearchCriteria()))
            results.Add(r);

        results.Should().HaveCount(1);
        results[0].Entry.Name.Should().Be("visible.txt");
    }

    [Fact]
    public async Task SearchAsync_IncludeHidden_ReturnsHiddenFiles()
    {
        var visible = MakeFile("/visible.txt", "visible.txt");
        var hidden = MakeFile("/hidden.txt", "hidden.txt",
            attrs: System.IO.FileAttributes.Hidden);
        var vfs = MakeVfs([("/", new[] { visible, hidden })]);

        var engine = new SearchEngine(vfs);
        var criteria = new SearchCriteria { IncludeHidden = true };
        var results = new List<SearchResult>();
        await foreach (var r in engine.SearchAsync("/", criteria))
            results.Add(r);

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_Cancellation_StopsEarly()
    {
        var files = Enumerable.Range(0, 100)
            .Select(i => MakeFile($"/{i}.txt", $"{i}.txt"))
            .ToArray();
        var vfs = MakeVfs([("/", files)]);
        var cts = new CancellationTokenSource();

        var engine = new SearchEngine(vfs);
        int count = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var r in engine.SearchAsync("/", new SearchCriteria(), cts.Token))
            {
                count++;
                if (count == 5) cts.Cancel();
            }
        });

        count.Should().BeLessThan(100);
    }
}
