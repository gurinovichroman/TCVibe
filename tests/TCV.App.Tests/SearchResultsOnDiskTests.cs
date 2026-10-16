using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Search results are a mount whose entries are disk files (ROADMAP 6.48, TRAP-28): commands that
/// need real paths for the selection treat them as the disk, while the list itself stays no folder.
/// </summary>
public class SearchResultsOnDiskTests
{
    private static FileListViewModel ListOverDisk(out InMemoryFileSystem disk)
    {
        disk = new InMemoryFileSystem("file");
        disk.AddDirectory("C:/work");
        disk.AddDirectory("C:/work/sub");
        disk.AddFile("C:/work/a.txt", [1]);
        disk.AddFile("C:/work/sub/b.txt", [2]);
        var list = new FileListViewModel(disk, NullLogger<FileListViewModel>.Instance) { CurrentPath = "C:/work" };
        return list;
    }

    private static async Task<IReadOnlyList<IFileEntry>> HitsAsync(InMemoryFileSystem disk, params string[] paths)
    {
        var hits = new List<IFileEntry>();
        foreach (var path in paths)
        {
            hits.Add((await disk.GetEntryAsync(new VfsPath(path)))!);
        }

        return hits;
    }

    private static async Task<SearchResultsFileSystem> FeedAsync(FileListViewModel list, InMemoryFileSystem disk)
    {
        var view = new SearchResultsFileSystem(disk, await HitsAsync(disk, "C:/work/a.txt", "C:/work/sub/b.txt"));
        list.Mount(view, SearchResultsFileSystem.RootPath, kind: MountKind.SearchResults);
        return view;
    }

    // ── The panel's notion ───────────────────────────────────────────────────

    [Fact]
    public async Task SearchResults_AreMountedButTheirEntriesAreOnDisk()
    {
        var list = ListOverDisk(out var disk);
        await FeedAsync(list, disk);

        list.IsMounted.Should().BeTrue("Backspace at the list's root still leaves it (TRAP-28)");
        list.IsSearchResults.Should().BeTrue();
        list.EntriesOnDisk.Should().BeTrue();
        list.IsInsideArchive.Should().BeFalse();
        list.IsRemote.Should().BeFalse();
    }

    [Fact]
    public void SearchResultsMountedWithoutAKind_AreRecognisedByTheirScheme()
    {
        var list = ListOverDisk(out var disk);

        list.Mount(new SearchResultsFileSystem(disk, []));

        list.IsSearchResults.Should().BeTrue();
        list.EntriesOnDisk.Should().BeTrue();
    }

    [Fact]
    public async Task TheListItself_IsNoDiskFolder_ButWorksInTheFolderItWasOpenedOver()
    {
        var list = ListOverDisk(out var disk);
        await FeedAsync(list, disk);

        list.DiskFolder.Should().BeNull("nothing can be put into the list");
        list.WorkingFolder.Should().Be("C:/work");
    }

    [Fact]
    public async Task AHitFolderOpenedFromTheList_IsADiskFolder()
    {
        var list = ListOverDisk(out var disk);
        await FeedAsync(list, disk);

        list.CurrentPath = "C:/work/sub";

        list.DiskFolder.Should().Be(@"C:\work\sub");
        list.WorkingFolder.Should().Be(@"C:\work\sub");
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("sftp")]
    public void AnArchiveOrAServer_HasNeitherDiskEntriesNorAFolder(string scheme)
    {
        var list = ListOverDisk(out _);

        list.Mount(new InMemoryFileSystem(scheme), "/pub");

        list.EntriesOnDisk.Should().BeFalse();
        list.IsSearchResults.Should().BeFalse();
        list.DiskFolder.Should().BeNull();
        list.WorkingFolder.Should().BeNull();
    }

    [Theory]
    [InlineData("sftp")]
    [InlineData("zip")]
    public async Task ResultsOfASearchInAMount_AreNeitherDiskEntriesNorAFolder(string scheme)
    {
        // 6.60: the hits of a server or archive search are listed over that mount, not over the disk.
        var list = ListOverDisk(out _);
        var mount = new InMemoryFileSystem(scheme);
        mount.AddFile("/pub/a.txt", [1]);
        list.Mount(mount, "/pub");
        var hits = await HitsAsync(mount, "/pub/a.txt");

        list.Mount(new SearchResultsFileSystem(mount, hits), SearchResultsFileSystem.RootPath, kind: MountKind.SearchResults);

        list.IsSearchResults.Should().BeTrue();
        list.EntriesOnDisk.Should().BeFalse();
        list.DiskFolder.Should().BeNull();
        list.WorkingFolder.Should().BeNull();
        list.ShellMenuFolderFor(hits).Should().BeNull();
    }

    [Fact]
    public async Task LeavingResultsOverAnArchive_ReReadsTheArchiveAtTheSameRoot()
    {
        // Results and the archive under them both stand on "/": leaving changes no path, and without
        // an explicit re-read ".." kept showing the hits.
        var list = ListOverDisk(out _);
        var archive = new InMemoryFileSystem("zip");
        archive.AddFile("/a.txt", [1]);
        archive.AddFile("/b.txt", [2]);
        list.Mount(archive, "/");
        list.Mount(new SearchResultsFileSystem(archive, await HitsAsync(archive, "/a.txt")),
            SearchResultsFileSystem.RootPath, kind: MountKind.SearchResults);
        var before = archive.ListCalls;

        list.Unmount();

        list.ActiveVfs.Should().BeSameAs(archive);
        list.CurrentPath.Should().Be("/");
        archive.ListCalls.Should().BeGreaterThan(before);
    }

    [Fact]
    public async Task ShellMenu_HitsFromOneFolder_IsThatFolder_FromSeveral_IsNone()
    {
        var list = ListOverDisk(out var disk);
        disk.AddFile("C:/work/c.txt", [3]);
        await FeedAsync(list, disk);

        var oneFolder = await HitsAsync(disk, "C:/work/a.txt", "C:/work/c.txt");
        list.ShellMenuFolderFor(oneFolder).Should().Be(WindowsFileSystem.ToWindowsPath(oneFolder[0].Path.Parent!));
        list.ShellMenuFolderFor(await HitsAsync(disk, "C:/work/a.txt", "C:/work/sub/b.txt"))
            .Should().BeNull("the shell would resolve b.txt in C:\\work");
        list.ShellMenuFolderFor([]).Should().BeNull("the list itself is no folder");
    }

    [Fact]
    public async Task Thumbnails_ForSearchHits_AreDrawnLikeOnTheDisk()
    {
        // The Thumbnails view asks the policy with the panel's EntriesOnDisk: hits are disk files
        // under their own paths, so a picture among them gets its preview; an archive's does not.
        var list = ListOverDisk(out var disk);
        await FeedAsync(list, disk);
        bool PreviewsAPhoto() => ThumbnailPolicy.CanPreview("jpg", false, 1000, FileAttributes.Archive, onLocalDisk: list.EntriesOnDisk);

        PreviewsAPhoto().Should().BeTrue();

        list.Mount(new InMemoryFileSystem("zip"), "/pics");
        PreviewsAPhoto().Should().BeFalse();
    }

    [Fact]
    public void ThePlainDisk_IsItsOwnFolder()
    {
        var list = ListOverDisk(out _);

        list.EntriesOnDisk.Should().BeTrue();
        list.IsSearchResults.Should().BeFalse();
        list.DiskFolder.Should().Be("C:/work");
        list.WorkingFolder.Should().Be("C:/work");
    }

    // ── What a tool sees (%P, %T) ────────────────────────────────────────────

    [Fact]
    public async Task ToolContext_ActiveSearchList_IsReportedAsTheDiskFolderItWorksIn()
    {
        var list = ListOverDisk(out var disk);
        await FeedAsync(list, disk);

        var (fs, path) = PanelToolContext.FolderOf(list, asTarget: false);

        fs.Should().BeSameAs(disk);
        path.Should().Be(new VfsPath("C:/work"));
    }

    [Fact]
    public async Task ToolContext_TargetSearchList_StaysTheMountSoTheTargetMacroIsRefused()
    {
        var list = ListOverDisk(out var disk);
        var view = await FeedAsync(list, disk);

        var (fs, path) = PanelToolContext.FolderOf(list, asTarget: true);

        fs.Should().BeSameAs(view);
        TCV.Tools.UserToolsMacroExpander.DiskFolderOf(fs, path).Should().BeNull();
    }

    [Fact]
    public async Task ToolContext_TargetHitFolder_IsTheDiskFolder()
    {
        var list = ListOverDisk(out var disk);
        await FeedAsync(list, disk);
        list.CurrentPath = "C:/work/sub";

        var (fs, path) = PanelToolContext.FolderOf(list, asTarget: true);

        TCV.Tools.UserToolsMacroExpander.DiskFolderOf(fs, path).Should().Be(@"C:\work\sub");
    }

    [Fact]
    public void ToolContext_TargetArchive_StaysTheArchive()
    {
        var list = ListOverDisk(out _);
        var archive = new InMemoryFileSystem("zip");
        list.Mount(archive, "/docs");

        var (fs, path) = PanelToolContext.FolderOf(list, asTarget: true);

        fs.Should().BeSameAs(archive);
        path.Should().Be(new VfsPath("/docs"));
        TCV.Tools.UserToolsMacroExpander.DiskFolderOf(fs, path).Should().BeNull();
    }

    // ── The hit list ─────────────────────────────────────────────────────────
    // Renames and moves go by the hit's own path, as RenameEntryOperation and Multi-Rename pass it.

    [Fact]
    public async Task Rename_ThroughTheView_KeepsTheHitUnderItsNewName()
    {
        var disk = new InMemoryFileSystem("file");
        disk.AddDirectory("C:/work");
        disk.AddFile("C:/work/a.txt", [1]);
        disk.AddFile("C:/work/b.txt", [2]);
        var hits = await HitsAsync(disk, "C:/work/a.txt", "C:/work/b.txt");
        var view = new SearchResultsFileSystem(disk, hits);

        await view.RenameAsync(hits[0].Path, "renamed.txt", CancellationToken.None);

        var names = (await view.ListAsync(new VfsPath("/"))).Select(e => e.Name);
        names.Should().BeEquivalentTo("renamed.txt", "b.txt");
    }

    [Fact]
    public async Task Rename_OfAFolderHoldingHits_KeepsTheHitsInsideIt()
    {
        var disk = new InMemoryFileSystem("file");
        disk.AddDirectory("C:/work");
        disk.AddDirectory("C:/work/sub");
        disk.AddFile("C:/work/sub/b.txt", [2]);
        var hits = await HitsAsync(disk, "C:/work/sub", "C:/work/sub/b.txt");
        var view = new SearchResultsFileSystem(disk, hits);

        // InMemoryFileSystem renames the folder alone; the disk would take its files along.
        await view.RenameAsync(hits[0].Path, "moved", CancellationToken.None);
        disk.AddFile("C:/work/moved/b.txt", [2]);

        var paths = (await view.ListAsync(new VfsPath("/"))).Select(e => e.Path.Value);
        paths.Should().BeEquivalentTo("/C:/work/moved", "/C:/work/moved/b.txt");
    }

    [Fact]
    public async Task Move_ThroughTheView_KeepsTheHitAtItsNewPath()
    {
        var disk = new InMemoryFileSystem("file");
        disk.AddDirectory("C:/work");
        disk.AddDirectory("C:/other");
        disk.AddFile("C:/work/a.txt", [1]);
        var hits = await HitsAsync(disk, "C:/work/a.txt");
        var view = new SearchResultsFileSystem(disk, hits);

        await view.MoveAsync(hits[0].Path, new VfsPath("/C:/other/a2.txt"), CancellationToken.None);

        var hit = (await view.ListAsync(new VfsPath("/"))).Should().ContainSingle().Subject;
        hit.Name.Should().Be("a2.txt");
    }

    [Fact]
    public async Task Delete_ThroughTheView_StillDropsTheHit()
    {
        var disk = new InMemoryFileSystem("file");
        disk.AddDirectory("C:/work");
        disk.AddFile("C:/work/a.txt", [1]);
        disk.AddFile("C:/work/b.txt", [2]);
        var hits = await HitsAsync(disk, "C:/work/a.txt", "C:/work/b.txt");
        var view = new SearchResultsFileSystem(disk, hits);

        await view.DeleteAsync(hits[0].Path, recursive: false, CancellationToken.None);

        (await view.ListAsync(new VfsPath("/"))).Select(e => e.Name).Should().Equal("b.txt");
    }

    [Fact]
    public async Task SharedFolder_HitsFromOneFolder_IsThatFolder()
    {
        ListOverDisk(out var disk);
        disk.AddFile("C:/work/c.txt", [3]);
        var hits = await HitsAsync(disk, "C:/work/a.txt", "C:/work/c.txt");

        SearchResultsFileSystem.SharedFolder(hits).Should().Be(hits[0].Path.Parent);
    }

    [Fact]
    public async Task SharedFolder_HitsFromSeveralFolders_IsNone()
    {
        ListOverDisk(out var disk);

        SearchResultsFileSystem.SharedFolder(await HitsAsync(disk, "C:/work/a.txt", "C:/work/sub/b.txt"))
            .Should().BeNull();
        SearchResultsFileSystem.SharedFolder([]).Should().BeNull();
    }

    // ── F5/F6 from the list: the view is the file system under it (ADR-0028) ──

    [Fact]
    public void FileSystemIdentity_ViewAndTheFileSystemUnderIt_AreOne()
    {
        var disk = new InMemoryFileSystem("file");
        var view = new SearchResultsFileSystem(disk, []);

        FileSystemIdentity.Same(view, disk).Should().BeTrue();
        FileSystemIdentity.Same(disk, view).Should().BeTrue();
        FileSystemIdentity.Same(view, new InMemoryFileSystem("file")).Should().BeFalse();
        FileSystemIdentity.IsLocalDisk(view).Should().BeTrue();
        FileSystemIdentity.IsLocalDisk(new SearchResultsFileSystem(new InMemoryFileSystem("sftp"), [])).Should().BeFalse();
    }

    [Fact]
    public async Task MoveOperation_FromTheListToAFolderOfTheSameDisk_RenamesAndTheHitFollows()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/work");
        fs.AddDirectory("/other");
        fs.AddFile("/work/a.txt", [1]);
        var hits = await HitsAsync(fs, "/work/a.txt");
        var view = new SearchResultsFileSystem(fs, hits);

        var op = new MoveOperation(view, fs, hits, new VfsPath("/other"),
            new TCV.App.Operations.Conflict.SkipStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/other/a.txt").Should().Equal(1);
        fs.HasFile("/work/a.txt").Should().BeFalse();
        // A rename through the view keeps the hit; a copy and a delete would have dropped it.
        (await view.ListAsync(new VfsPath("/"))).Should().ContainSingle()
            .Which.Path.Should().Be(new VfsPath("/other/a.txt"));
    }

    [Fact]
    public async Task CopyOperation_FolderFromTheListIntoItself_IsRefused()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/work/proj/sub");
        fs.AddFile("/work/proj/a.txt", [1]);
        var hits = await HitsAsync(fs, "/work/proj");
        var view = new SearchResultsFileSystem(fs, hits);

        var op = new CopyOperation(view, fs, hits, new VfsPath("/work/proj/sub"),
            new TCV.App.Operations.Conflict.SkipStrategy(), new CopyOptions(), NullLogger<CopyOperation>.Instance);

        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None))
            .Should().ThrowAsync<DestinationInsideSourceException>();
    }

    // ── F8 and the Recycle Bin ───────────────────────────────────────────────

    private static readonly string FixedDrivePath =
        Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "tcv-recycle-probe", "a.txt");

    [Fact]
    public void RecycleBin_LooksThroughTheSearchView_ToTheDisk()
    {
        var disk = new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance);
        var view = new SearchResultsFileSystem(disk, []);
        var path = new VfsPath(FixedDrivePath);

        RecycleBin.IsAvailableFor(view, path).Should().Be(RecycleBin.IsAvailableFor(disk, path))
            .And.Be(RecycleBin.IsAvailableForWindowsPath(FixedDrivePath));
    }

    [Fact]
    public void RecycleBin_SearchViewOverSomethingElse_HasNoBin()
    {
        var view = new SearchResultsFileSystem(new InMemoryFileSystem("file"), []);

        RecycleBin.IsAvailableFor(view, new VfsPath(FixedDrivePath)).Should().BeFalse();
    }

    [Fact]
    public void DeleteOperation_OnSearchHits_LogsTheRecycleBinLikeTheDisk()
    {
        var disk = new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance);
        var view = new SearchResultsFileSystem(disk, []);
        IFileEntry hit = new FileEntry(new VfsPath(FixedDrivePath), "a.txt", false, 1, DateTime.Now, FileAttributes.Normal);

        var throughView = new DeleteOperation(view, [hit], moveToRecycleBin: true, NullLogger<DeleteOperation>.Instance);
        var onDisk = new DeleteOperation(disk, [hit], moveToRecycleBin: true, NullLogger<DeleteOperation>.Instance);

        throughView.LogDescriptor.Destination.Should().Be(onDisk.LogDescriptor.Destination)
            .And.Be(RecycleBin.IsAvailableForWindowsPath(FixedDrivePath) ? OperationLogDescriptor.RecycleBinToken : null);
    }
}
