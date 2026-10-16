using System.IO.Compression;
using FluentAssertions;
using TCV.Archives;

namespace TCV.Archives.Tests;

public class ArchiveDetectorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ArchiveDetectorTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    [Fact]
    public void DetectFormat_ZipFile_ReturnsZip()
    {
        var path = CreateZip("test.zip");
        ArchiveDetector.DetectFormat(path).Should().Be(ArchiveDetector.ArchiveFormat.Zip);
    }

    [Fact]
    public void DetectFormat_UnknownExtension_ReturnsUnknown()
    {
        var path = Path.Combine(_tempDir, "file.xyz");
        File.WriteAllText(path, "hello");
        ArchiveDetector.DetectFormat(path).Should().Be(ArchiveDetector.ArchiveFormat.Unknown);
    }

    [Theory]
    [InlineData("archive.zip", ArchiveDetector.ArchiveFormat.Zip)]
    [InlineData("archive.ZIP", ArchiveDetector.ArchiveFormat.Zip)]
    [InlineData("archive.7z", ArchiveDetector.ArchiveFormat.SevenZip)]
    [InlineData("archive.rar", ArchiveDetector.ArchiveFormat.Rar)]
    [InlineData("archive.tar", ArchiveDetector.ArchiveFormat.Tar)]
    [InlineData("archive.tar.gz", ArchiveDetector.ArchiveFormat.Tar)] // Path.GetExtension returns ".gz" → Tar
    [InlineData("archive.tgz", ArchiveDetector.ArchiveFormat.Tar)]
    public void DetectByExtension_VariousExtensions(string fileName, ArchiveDetector.ArchiveFormat expected)
    {
        ArchiveDetector.DetectByExtension(fileName).Should().Be(expected);
    }

    [Fact]
    public void IsArchive_ZipFile_ReturnsTrue()
    {
        var path = CreateZip("check.zip");
        ArchiveDetector.IsArchive(path).Should().BeTrue();
    }

    private string CreateZip(string name)
    {
        var path = Path.Combine(_tempDir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("test.txt");
        using var stream = entry.Open();
        stream.Write("hello"u8);
        return path;
    }
}
