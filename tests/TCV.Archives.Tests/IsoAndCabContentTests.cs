using FluentAssertions;
using TCV.Archives.Engines;
using TCV.Core.Domain;

namespace TCV.Archives.Tests;

public sealed class IsoAndCabContentTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-iso-cab-" + Guid.NewGuid().ToString("N"));

    public IsoAndCabContentTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    // ── ISO ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ListAll_IsoWithFilesAndFolder_ReturnsSlashFreePaths()
    {
        var iso = Fixtures.CreateIso(_dir, "readme", "guide");

        var entries = new IsoEngine().ListAll(iso);

        entries.Select(e => e.FullPath).Should().BeEquivalentTo("README.TXT", "DOCS", "DOCS/GUIDE.TXT");
        entries.Single(e => e.FullPath == "DOCS").IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_IsoRoot_ShowsTheFileAndTheFolder()
    {
        var iso = Fixtures.CreateIso(_dir, "readme", "guide");
        using var fs = new ArchiveFileSystem(iso, new IsoEngine());

        var root = await fs.ListAsync(new VfsPath("/"));
        var docs = await fs.ListAsync(new VfsPath("/DOCS"));

        root.Select(e => e.Name).Should().BeEquivalentTo("README.TXT", "DOCS");
        docs.Select(e => e.Name).Should().BeEquivalentTo("GUIDE.TXT");
    }

    [Fact]
    public async Task OpenReadAsync_IsoFileInFolder_ReturnsItsContent()
    {
        var iso = Fixtures.CreateIso(_dir, "readme", "the guide text");
        using var fs = new ArchiveFileSystem(iso, new IsoEngine());

        using var stream = await fs.OpenReadAsync(new VfsPath("/DOCS/GUIDE.TXT"));
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync()).Should().Be("the guide text");
    }

    [Fact]
    public void ExtractAll_Iso_RecreatesTheTree()
    {
        var iso = Fixtures.CreateIso(_dir, "readme", "guide");
        var dest = Path.Combine(_dir, "out");

        new IsoEngine().ExtractAll(iso, dest);

        File.ReadAllText(Path.Combine(dest, "README.TXT")).Should().Be("readme");
        File.ReadAllText(Path.Combine(dest, "DOCS", "GUIDE.TXT")).Should().Be("guide");
    }

    // ── CAB ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ListAll_CabWithSubfolder_SynthesisesTheDirectory()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"), ("sub\\b.txt", "BBB"));

        var entries = new CabEngine().ListAll(cab);

        entries.Select(e => e.FullPath).Should().BeEquivalentTo("a.txt", "sub", "sub/b.txt");
    }

    [Fact]
    public async Task ListAsync_CabSubfolder_ShowsItsFile()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"), ("sub\\b.txt", "BBB"));
        using var fs = new ArchiveFileSystem(cab, new CabEngine());

        var root = await fs.ListAsync(new VfsPath("/"));
        var sub = await fs.ListAsync(new VfsPath("/sub"));

        root.Select(e => e.Name).Should().BeEquivalentTo("a.txt", "sub");
        sub.Select(e => e.Name).Should().BeEquivalentTo("b.txt");
    }

    [Fact]
    public void ExtractEntry_CabFileInSubfolder_ReturnsOnlyThatFile()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"), ("sub\\b.txt", "BBB"));

        using var stream = new CabEngine().ExtractEntry(cab, "sub/b.txt");
        using var reader = new StreamReader(stream);

        reader.ReadToEnd().Should().Be("BBB");
    }

    [Fact]
    public void ExtractAll_CabWithManyFiles_DoesNotDeadlockOnExpandOutput()
    {
        // expand.exe prints a line per file; unread, that fills the pipe and stalls it.
        var files = Enumerable.Range(0, 400).Select(i => ($"file-{i:D4}.txt", $"content {i}")).ToArray();
        var cab = Fixtures.CreateCab(_dir, files);
        var dest = Path.Combine(_dir, "many");

        new CabEngine().ExtractAll(cab, dest);

        Directory.GetFiles(dest).Should().HaveCount(400);
    }

    [Fact]
    public void ExtractAll_CancelledBeforeStart_Throws()
    {
        var cab = Fixtures.CreateCab(_dir, ("a.txt", "AAA"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new CabEngine().ExtractAll(cab, Path.Combine(_dir, "never"), null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }
}
