using System.IO;
using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public class FileEntryTests
{
    [Fact]
    public void Constructor_SetsAllProperties()
    {
        var path = new VfsPath("C:/Users/test/file.txt");
        var lastModified = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Local);

        var entry = new FileEntry(
            path,
            name: "file.txt",
            isDirectory: false,
            size: 1024,
            lastModified: lastModified,
            attributes: FileAttributes.Normal);

        entry.Path.Should().Be(path);
        entry.Name.Should().Be("file.txt");
        entry.IsDirectory.Should().BeFalse();
        entry.Size.Should().Be(1024);
        entry.LastModified.Should().Be(lastModified);
        entry.Attributes.Should().Be(FileAttributes.Normal);
    }

    [Fact]
    public void Directory_Entry_HasIsDirectoryTrue()
    {
        var entry = new FileEntry(
            new VfsPath("C:/Users/test"),
            name: "test",
            isDirectory: true,
            size: 0,
            lastModified: DateTime.UtcNow,
            attributes: FileAttributes.Directory);

        entry.IsDirectory.Should().BeTrue();
        entry.Size.Should().Be(0);
    }

    [Fact]
    public void Entry_WithHiddenAttribute_ReturnsCorrectAttributes()
    {
        var entry = new FileEntry(
            new VfsPath("C:/Users/.hidden"),
            name: ".hidden",
            isDirectory: false,
            size: 512,
            lastModified: DateTime.UtcNow,
            attributes: FileAttributes.Hidden);

        entry.Attributes.Should().HaveFlag(FileAttributes.Hidden);
    }
}
