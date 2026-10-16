using System.IO;
using FluentAssertions;
using TCV.App.Navigation;
using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The panel's Tree view and the docked folder tree (6.52): which node a panel path belongs to, what
/// a folder shows, and that a fresh listing updates the tree without collapsing what was open.
/// </summary>
public class FolderTreeTests
{
    private static readonly string[] Roots = [@"C:\", @"D:\"];

    // ── Locating a path ───────────────────────────────────────────────────────

    [Fact]
    public void Locate_PathOnADrive_GivesTheDriveAndTheFolderNames()
    {
        var located = FolderTree.Locate(Roots, @"D:\work\repo");

        located.Should().NotBeNull();
        located!.Value.RootIndex.Should().Be(1);
        located.Value.Segments.Should().Equal("work", "repo");
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"c:\")]
    public void Locate_TheDriveRootItself_HasNoSegments(string path)
    {
        var located = FolderTree.Locate(Roots, path);

        located!.Value.RootIndex.Should().Be(0);
        located.Value.Segments.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\server\share\docs")]   // the trees list drives, not network shares
    [InlineData(@"E:\data")]               // a drive that is not in the tree
    [InlineData("/pub/logs")]              // a mounted panel's path
    [InlineData("")]
    public void Locate_PathOnNoRoot_IsNull(string path)
    {
        FolderTree.Locate(Roots, path).Should().BeNull();
    }

    [Fact]
    public void Locate_RespectsFolderBoundaries()
    {
        // "C:\a" is not a prefix *folder* of "C:\ab": matching it would walk into the wrong subtree.
        var located = FolderTree.Locate([@"C:\a", @"C:\"], @"C:\ab\x");

        located!.Value.RootIndex.Should().Be(1);
        located.Value.Segments.Should().Equal("ab", "x");
    }

    [Theory]
    [InlineData(@"C:\Src", @"c:\src\", true)]
    [InlineData(@"C:\", "C:", true)]
    [InlineData(@"C:\src", @"C:\src2", false)]
    [InlineData(null, @"C:\", false)]
    public void SamePath_IgnoresCaseAndATrailingSeparator(string? a, string? b, bool same)
    {
        FolderTree.SamePath(a, b).Should().Be(same);
    }

    // ── What a folder shows ───────────────────────────────────────────────────

    private static IFileEntry Entry(string name, bool isDirectory, FileAttributes attributes = FileAttributes.Normal) =>
        new FileEntry(new VfsPath("/C:/" + name), name, isDirectory, 0, new DateTime(2026, 1, 1),
            attributes | (isDirectory ? FileAttributes.Directory : 0));

    [Fact]
    public void ChildrenOf_ListsFoldersThenFiles_EachByName_WithoutHiddenOnes()
    {
        var listing = new[]
        {
            Entry("zeta.txt", false),
            Entry("src", true),
            Entry(".git", true, FileAttributes.Hidden),
            Entry("Docs", true),
            Entry("a.txt", false),
        };

        FolderTree.ChildrenOf(listing, foldersOnly: false).Should().Equal(
            new FolderTreeEntry("Docs", true),
            new FolderTreeEntry("src", true),
            new FolderTreeEntry("a.txt", false),
            new FolderTreeEntry("zeta.txt", false));
    }

    [Fact]
    public void ChildrenOf_FoldersOnly_LeavesFilesOut()
    {
        // The docked tree: the panels next to it list the files already.
        var listing = new[] { Entry("src", true), Entry("readme.md", false) };

        FolderTree.ChildrenOf(listing, foldersOnly: true).Should().Equal(new FolderTreeEntry("src", true));
    }

    // ── Nodes ─────────────────────────────────────────────────────────────────

    /// <summary>A disk stand-in: "C:\x" is stored as "/C:/x", the way the in-memory file system normalises it.</summary>
    private static InMemoryFileSystem Disk(params string[] folders)
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/C:");
        foreach (var folder in folders)
        {
            fs.AddDirectory(folder);
        }

        return fs;
    }

    private static TreeNodeViewModel Root(IVirtualFileSystem fs, bool foldersOnly = false) =>
        new("C:", @"C:\", true, fs, foldersOnly);

    [Fact]
    public async Task EnsureLoaded_ReadsTheFolder()
    {
        var fs = Disk("/C:/src", "/C:/docs");
        fs.AddFile("/C:/readme.md", [1]);
        var root = Root(fs);

        await root.EnsureLoadedAsync();

        root.IsLoaded.Should().BeTrue();
        root.Children.Select(c => c.Name).Should().Equal("docs", "src", "readme.md");
    }

    [Fact]
    public async Task FoldersOnlyNode_ShowsNoFiles_AndItsChildrenNeither()
    {
        var fs = Disk("/C:/src", "/C:/src/lib");
        fs.AddFile("/C:/readme.md", [1]);
        fs.AddFile("/C:/src/main.cs", [1]);
        var root = Root(fs, foldersOnly: true);

        await root.EnsureLoadedAsync();
        var src = root.Children.Single();
        await src.EnsureLoadedAsync();

        root.Children.Select(c => c.Name).Should().Equal("src");
        src.Children.Select(c => c.Name).Should().Equal("lib");
    }

    [Fact]
    public async Task ApplyListing_AddsNewFoldersAndDropsGoneOnes_KeepingTheOpenSubtree()
    {
        var fs = Disk("/C:/src", "/C:/old", "/C:/src/lib");
        var root = Root(fs);
        await root.EnsureLoadedAsync();
        var src = root.Children.Single(c => c.Name == "src");
        await src.ExpandAsync();

        // A folder was made and another deleted, by this program or any other: the panel re-listed C:\.
        fs.AddDirectory("/C:/new");
        await fs.DeleteAsync(new VfsPath("/C:/old"), recursive: true, default);
        root.ApplyListing(await fs.ListAsync(new VfsPath("/C:")));

        root.Children.Select(c => c.Name).Should().Equal("new", "src");
        root.Children.Single(c => c.Name == "src").Should().BeSameAs(src, "the open folder is kept, not rebuilt");
        src.IsExpanded.Should().BeTrue();
        src.Children.Select(c => c.Name).Should().Equal("lib");
    }

    [Fact]
    public async Task ApplyListing_OnANodeNeverOpened_IsIgnored()
    {
        // Its placeholder stays: it reads its own listing on the first expansion.
        var fs = Disk("/C:/src");
        var root = Root(fs);

        root.ApplyListing(await fs.ListAsync(new VfsPath("/C:")));

        root.IsLoaded.Should().BeFalse();
        root.Children.Should().ContainSingle(c => c.Name == "...");
    }

    [Fact]
    public async Task RefreshAsync_RereadsALoadedFolder()
    {
        var fs = Disk("/C:/src");
        var root = Root(fs);
        await root.EnsureLoadedAsync();

        fs.AddDirectory("/C:/tests");
        await root.RefreshAsync();

        root.Children.Select(c => c.Name).Should().Equal("src", "tests");
    }

    [Fact]
    public async Task RefreshOpenSubtree_RereadsOpenFoldersOnly()
    {
        var fs = Disk("/C:/open", "/C:/closed");
        var root = Root(fs);
        await root.EnsureLoadedAsync();
        var open = root.Children.Single(c => c.Name == "open");
        await open.ExpandAsync();
        var closed = root.Children.Single(c => c.Name == "closed");

        fs.AddDirectory("/C:/open/added");
        fs.AddDirectory("/C:/closed/added");
        await root.RefreshOpenSubtreeAsync();

        open.Children.Select(c => c.Name).Should().Equal("added");
        closed.IsLoaded.Should().BeFalse("a collapsed folder is read when it is opened, not on every refresh");
    }

    [Fact]
    public async Task ExpandingAgain_RereadsTheFolder()
    {
        // Collapse and expand is the Explorer way to refresh one folder.
        var fs = Disk("/C:/src");
        var root = Root(fs);
        await root.ExpandAsync();

        fs.AddDirectory("/C:/tests");
        root.IsExpanded = false;
        root.IsExpanded = true;
        await WaitFor(() => root.Children.Count == 2);

        root.Children.Select(c => c.Name).Should().Equal("src", "tests");
    }

    [Fact]
    public async Task ExpandAsync_OnALoadedFolder_DoesNotReadItAgain()
    {
        // Following the panel opens every level on the way: it must not cost a listing per level.
        var fs = Disk("/C:/src");
        var root = Root(fs);
        await root.ExpandAsync();
        root.IsExpanded = false;

        fs.AddDirectory("/C:/tests");
        await root.ExpandAsync();

        root.Children.Select(c => c.Name).Should().Equal("src");
    }

    // ── Shared walks ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExpandTo_OpensTheWayAndReturnsTheFolder_WithoutOpeningIt()
    {
        var fs = Disk("/C:/a", "/C:/a/b", "/C:/a/b/c");
        var roots = new[] { Root(fs) };

        var node = await FolderTreeNodes.ExpandToAsync(roots, @"C:\a\b", expandTarget: false);

        node!.FullPath.Should().Be(@"C:\a\b");
        roots[0].IsExpanded.Should().BeTrue();
        roots[0].Children.Single().IsExpanded.Should().BeTrue();
        node.IsExpanded.Should().BeFalse();
        node.IsLoaded.Should().BeFalse();
    }

    [Fact]
    public async Task ExpandTo_AFolderTheTreeDoesNotShow_StopsAtTheNearestShownOne()
    {
        var fs = Disk("/C:/a");
        var roots = new[] { Root(fs) };

        var node = await FolderTreeNodes.ExpandToAsync(roots, @"C:\a\hidden\deeper", expandTarget: true);

        node!.FullPath.Should().Be(@"C:\a");
    }

    [Fact]
    public async Task ExpandTo_APathOnNoRoot_IsNull()
    {
        var roots = new[] { Root(Disk()) };

        (await FolderTreeNodes.ExpandToAsync(roots, @"\\server\share", expandTarget: false)).Should().BeNull();
    }

    [Fact]
    public async Task FindLoaded_FindsOnlyFoldersAlreadyRead()
    {
        var fs = Disk("/C:/a", "/C:/a/b");
        var roots = new[] { Root(fs) };
        await roots[0].EnsureLoadedAsync();

        FolderTreeNodes.FindLoaded(roots, @"C:\a")!.FullPath.Should().Be(@"C:\a");
        FolderTreeNodes.FindLoaded(roots, @"C:\a\b").Should().BeNull("C:\\a was never read, so b is not in the tree yet");
        roots[0].Children.Single().IsExpanded.Should().BeFalse("finding opens nothing");
    }

    [Fact]
    public void MergeRoots_KeepsTheDrivesThatStay_AndTheirNodes()
    {
        var fs = Disk();
        var c = new TreeNodeViewModel("C:", @"C:\", true, fs) { IsExpanded = true };
        var roots = new System.Collections.ObjectModel.ObservableCollection<TreeNodeViewModel>
        {
            c,
            new("E:", @"E:\", true, fs),
        };

        // E: was pulled out, F: was plugged in.
        var changed = FolderTreeNodes.MergeRoots(roots, [("C:", @"C:\"), ("F:", @"F:\")],
            (name, root) => new TreeNodeViewModel(name, root, true, fs));

        changed.Should().BeTrue();
        roots.Select(r => r.Name).Should().Equal("C:", "F:");
        roots[0].Should().BeSameAs(c);
        c.IsExpanded.Should().BeTrue();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }
}
