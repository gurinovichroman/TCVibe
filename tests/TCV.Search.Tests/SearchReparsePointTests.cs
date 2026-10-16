using System.IO;
using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Search.Tests;

/// <summary>
/// A junction or symlink inside a searched directory can lead anywhere. The agent bridge searches with
/// <see cref="SearchCriteria.SkipReparsePoints"/> so a link inside an approved directory cannot carry
/// the search — and the content it returns — out of it.
/// </summary>
public class SearchReparsePointTests
{
    private static IFileEntry Entry(string path, string name, bool isDirectory, FileAttributes attributes)
    {
        var mock = new Mock<IFileEntry>();
        mock.SetupGet(e => e.Path).Returns(new VfsPath(path));
        mock.SetupGet(e => e.Name).Returns(name);
        mock.SetupGet(e => e.IsDirectory).Returns(isDirectory);
        mock.SetupGet(e => e.Size).Returns(10L);
        mock.SetupGet(e => e.LastModified).Returns(DateTime.Now);
        mock.SetupGet(e => e.Attributes).Returns(attributes);
        return mock.Object;
    }

    /// <summary>/ holds a real folder and a junction; both lead to a file.</summary>
    private static IVirtualFileSystem Tree()
    {
        var vfs = new Mock<IVirtualFileSystem>();
        vfs.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                Entry("/real", "real", true, FileAttributes.Directory),
                Entry("/link", "link", true, FileAttributes.Directory | FileAttributes.ReparsePoint),
            });
        vfs.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/real"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/real/inside.txt", "inside.txt", false, FileAttributes.Normal) });
        vfs.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/link"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/link/outside.txt", "outside.txt", false, FileAttributes.Normal) });
        return vfs.Object;
    }

    private static async Task<List<string>> Names(IVirtualFileSystem vfs, SearchCriteria criteria)
    {
        var found = new List<string>();
        await foreach (var result in new SearchEngine(vfs).SearchAsync("/", criteria))
            found.Add(result.Entry.Name);
        return found;
    }

    [Fact]
    public async Task SearchAsync_SkipReparsePoints_DoesNotDescendIntoAJunction()
    {
        var found = await Names(Tree(), new SearchCriteria { FilesOnly = true, SkipReparsePoints = true });

        found.Should().Equal("inside.txt");
    }

    [Fact]
    public async Task SearchAsync_ByDefault_DoesNotDescendIntoLinksEither()
    {
        // Like ripgrep, no search descends into a link by default: a junction to a parent would loop, and
        // one to another drive would silently widen the search. Searching the link itself still works.
        var found = await Names(Tree(), new SearchCriteria { FilesOnly = true });

        found.Should().Equal("inside.txt");
    }
}
