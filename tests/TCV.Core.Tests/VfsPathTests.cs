using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public class VfsPathTests
{
    [Theory]
    [InlineData("C:\\Users\\test", "C:/Users/test")]
    [InlineData("C:/Users/test/", "C:/Users/test")]
    [InlineData("/foo/bar/", "/foo/bar")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    public void Normalize_ConvertsBackslashesAndTrimsTrailingSlash(string input, string expected)
    {
        var path = new VfsPath(input);
        path.Value.Should().Be(expected);
    }

    [Theory]
    [InlineData("D:\\", "D:/")]
    [InlineData("D:/", "D:/")]
    [InlineData("D:", "D:/")]
    public void Normalize_DriveRoot_KeepsSeparator(string input, string expected)
    {
        // "D:" without a separator is drive-*relative* and would resolve against
        // the process current directory instead of the drive root.
        new VfsPath(input).Value.Should().Be(expected);
    }

    [Fact]
    public void Parent_OfDriveRoot_ReturnsNull()
    {
        new VfsPath("D:\\").Parent.Should().BeNull();
    }

    [Fact]
    public void Combine_OnDriveRoot_DoesNotDoubleSeparator()
    {
        new VfsPath("D:\\").Combine("file.txt").Value.Should().Be("D:/file.txt");
    }

    [Fact]
    public void Root_HasSlashValue()
    {
        VfsPath.Root.Value.Should().Be("/");
    }

    [Fact]
    public void FileName_ReturnsLastSegment()
    {
        var path = new VfsPath("C:/Users/test/file.txt");
        path.FileName.Should().Be("file.txt");
    }

    [Fact]
    public void Parent_ReturnsParentPath()
    {
        var path = new VfsPath("C:/Users/test");
        path.Parent!.Value.Should().Be("C:/Users");
    }

    [Theory]
    [InlineData("/dir/file.txt", "/dir")]
    [InlineData("/file.txt", "/")]
    [InlineData("D:/repo/src/app.cs", "D:/repo/src")]
    public void Parent_StaysInSlashForm(string input, string expected)
    {
        // Path.GetDirectoryName would answer "\dir" here — callers that need a VFS path (an archive
        // panel, the file index) rely on Parent instead precisely because it normalises back.
        new VfsPath(input).Parent!.Value.Should().Be(expected);
    }

    [Fact]
    public void Parent_OfRoot_ReturnsNull()
    {
        var path = new VfsPath("/");
        path.Parent.Should().BeNull();
    }

    [Fact]
    public void Combine_AppendsSegment()
    {
        var path = new VfsPath("C:/Users");
        path.Combine("test").Value.Should().Be("C:/Users/test");
    }

    [Fact]
    public void Equality_IsCaseInsensitive()
    {
        var a = new VfsPath("C:/Users/Test");
        var b = new VfsPath("c:/users/test");
        a.Should().Be(b);
    }

    [Fact]
    public void GetHashCode_IsCaseInsensitive()
    {
        var a = new VfsPath("C:/Users/Test");
        var b = new VfsPath("c:/users/test");
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void ImplicitFromString_Works()
    {
        VfsPath path = "C:/Users/test";
        path.Value.Should().Be("C:/Users/test");
    }

    [Fact]
    public void ImplicitToString_Works()
    {
        var path = new VfsPath("C:/Users/test");
        string s = path;
        s.Should().Be("C:/Users/test");
    }

    [Fact]
    public void ToString_ReturnsValue()
    {
        var path = new VfsPath("C:/foo/bar");
        path.ToString().Should().Be("C:/foo/bar");
    }
}
