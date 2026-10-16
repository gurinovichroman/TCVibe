using System.IO;
using System.IO.Compression;
using FluentAssertions;
using TCV.App.FileSystem;
using TCV.Archives;
using TCV.Archives.Engines;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.MultiRename;

namespace TCV.App.Tests;

/// <summary>
/// The hits of an archive search shown in a panel are renamed through the view over the archive (6.71,
/// TRAP-28): the archive is rewritten, and the list keeps each hit under its new name.
/// </summary>
public sealed class ArchiveSearchResultsRenameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-arc-hits-" + Guid.NewGuid().ToString("N"));
    private readonly ArchiveFileSystem _archive;

    public ArchiveSearchResultsRenameTests()
    {
        Directory.CreateDirectory(_dir);
        var zip = Path.Combine(_dir, "hits.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "dir/a.txt", "dir/sub/c.txt", "b.txt" })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write("content of " + name);
            }
        }

        _archive = new ArchiveFileSystem(zip, new ZipEngine());
    }

    public void Dispose()
    {
        _archive.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<SearchResultsFileSystem> ViewAsync(params string[] hits)
    {
        var entries = new List<IFileEntry>();
        foreach (var hit in hits)
        {
            entries.Add((await _archive.GetEntryAsync(new VfsPath(hit)))!);
        }

        return new SearchResultsFileSystem(_archive, entries);
    }

    private static async Task<IReadOnlyList<string>> ListedAsync(SearchResultsFileSystem view) =>
        (await view.ListAsync(new VfsPath(SearchResultsFileSystem.RootPath))).Select(e => e.Path.Value).ToList();

    [Fact]
    public async Task RenameAsync_HitThroughTheView_StaysInTheListUnderItsNewName()
    {
        var view = await ViewAsync("/dir/a.txt", "/b.txt");

        await view.RenameAsync(new VfsPath("/dir/a.txt"), "x.txt", CancellationToken.None);

        (await ListedAsync(view)).Should().Equal("/dir/x.txt", "/b.txt");
        (await _archive.GetEntryAsync(new VfsPath("/dir/a.txt"))).Should().BeNull();
    }

    [Fact]
    public async Task RenameBatchAsync_HitInsideARenamedFolder_FollowsBothRenames()
    {
        var view = await ViewAsync("/dir", "/dir/a.txt", "/dir/sub/c.txt");

        await view.RenameBatchAsync(
            [(new VfsPath("/dir/a.txt"), "x.txt"), (new VfsPath("/dir"), "d2")], CancellationToken.None);

        (await ListedAsync(view)).Should().Equal("/d2", "/d2/x.txt", "/d2/sub/c.txt");
    }

    [Fact]
    public async Task MultiRename_OverArchiveHits_RenamesThemInOneRewriteAndTheListFollows()
    {
        var view = await ViewAsync("/dir/a.txt", "/b.txt");
        var files = new List<IFileEntry>();
        foreach (var hit in new[] { "/dir/a.txt", "/b.txt" })
        {
            files.Add((await view.GetEntryAsync(new VfsPath(hit)))!);
        }

        var result = await new RenameEngine(view).ExecuteBatchAsync(files,
            [new RenamePreviewItem("a.txt", "a.md", false), new RenamePreviewItem("b.txt", "b.md", false)]);

        result.Succeeded.Should().BeTrue();
        (await ListedAsync(view)).Should().Equal("/dir/a.md", "/b.md");
        using var stream = await _archive.OpenReadAsync(new VfsPath("/b.md"));
        new StreamReader(stream).ReadToEnd().Should().Be("content of b.txt");
    }

    [Fact]
    public void CanRenameAsBatch_OnlyOverAnArchiveTheEngineCanWrite()
    {
        new SearchResultsFileSystem(_archive, []).CanRenameAsBatch.Should().BeTrue();
        new SearchResultsFileSystem(new ArchiveFileSystem(Path.Combine(_dir, "x.tar"), new TarEngine()), [])
            .CanRenameAsBatch.Should().BeFalse();
        new SearchResultsFileSystem(new InMemoryFileSystem("file"), []).CanRenameAsBatch.Should().BeFalse();
    }
}
