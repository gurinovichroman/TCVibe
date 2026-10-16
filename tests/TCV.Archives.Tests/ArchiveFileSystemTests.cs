using System.IO.Compression;
using FluentAssertions;
using TCV.Archives;
using TCV.Archives.Engines;
using TCV.Core.Domain;

namespace TCV.Archives.Tests;

public class ArchiveFileSystemTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ArchiveFileSystemTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string CreateZip(params (string name, string content)[] entries)
    {
        var path = Path.Combine(_tempDir, "test.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public async Task ListAsync_Root_ReturnsTopLevelEntries()
    {
        var path = CreateZip(("readme.txt", "hi"), ("docs/guide.md", "# guide"));
        using var fs = new ArchiveFileSystem(path, new ZipEngine());

        var entries = await fs.ListAsync(new VfsPath("/"));

        entries.Should().Contain(e => e.Name == "readme.txt");
        entries.Should().Contain(e => e.Name == "docs" && e.IsDirectory);
        entries.Should().NotContain(e => e.Name == "guide.md"); // nested — not at root
    }

    [Fact]
    public async Task ListAsync_SubDir_ReturnsChildEntries()
    {
        var path = CreateZip(("docs/guide.md", "# guide"), ("docs/api.md", "# api"));
        using var fs = new ArchiveFileSystem(path, new ZipEngine());

        var entries = await fs.ListAsync(new VfsPath("/docs"));

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Name == "guide.md");
        entries.Should().Contain(e => e.Name == "api.md");
    }

    [Fact]
    public async Task OpenReadAsync_ValidEntry_ReturnsContent()
    {
        var path = CreateZip(("hello.txt", "Hello World"));
        using var fs = new ArchiveFileSystem(path, new ZipEngine());

        using var stream = await fs.OpenReadAsync(new VfsPath("/hello.txt"));
        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();

        content.Should().Be("Hello World");
    }

    [Fact]
    public void Scheme_ZipFile_ReturnsZip()
    {
        var path = CreateZip(("a.txt", "a"));
        using var fs = new ArchiveFileSystem(path, new ZipEngine());
        fs.Scheme.Should().Be("zip");
    }

    [Fact]
    public void SupportsOperation_Read_ReturnsTrue()
    {
        var path = CreateZip(("a.txt", "a"));
        using var fs = new ArchiveFileSystem(path, new ZipEngine());
        fs.SupportsOperation(TCV.Core.Domain.VfsOperation.Read).Should().BeTrue();
    }

    [Fact]
    public void SupportsOperation_Write_ReturnsFalse()
    {
        var path = CreateZip(("a.txt", "a"));
        using var fs = new ArchiveFileSystem(path, new ZipEngine());
        fs.SupportsOperation(TCV.Core.Domain.VfsOperation.Write).Should().BeFalse();
    }
}
