using System.IO;
using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public class VfsChangedEventArgsTests
{
    [Fact]
    public void Constructor_SetsProperties()
    {
        var path = new VfsPath("C:/foo/bar.txt");
        var args = new VfsChangedEventArgs(path, WatcherChangeTypes.Changed);

        args.Path.Should().Be(path);
        args.ChangeType.Should().Be(WatcherChangeTypes.Changed);
    }

    [Theory]
    [InlineData(WatcherChangeTypes.Created)]
    [InlineData(WatcherChangeTypes.Deleted)]
    [InlineData(WatcherChangeTypes.Renamed)]
    [InlineData(WatcherChangeTypes.Changed)]
    public void ChangeType_AllVariantsSupported(WatcherChangeTypes changeType)
    {
        var args = new VfsChangedEventArgs(VfsPath.Root, changeType);
        args.ChangeType.Should().Be(changeType);
    }
}
