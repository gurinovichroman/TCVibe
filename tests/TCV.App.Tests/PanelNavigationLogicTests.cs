using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Collections;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>Sorting, breadcrumbs and typed-path navigation of a panel.</summary>
public class PanelNavigationLogicTests
{
    // ── Natural name order ────────────────────────────────────────────────────

    [Fact]
    public void NaturalCompare_NumbersInNames_SortByValue()
    {
        var names = new[] { "file10.txt", "file2.txt", "file1.txt", "File3.txt" };

        names.Order(NaturalStringComparer.Instance).Should()
            .Equal("file1.txt", "file2.txt", "File3.txt", "file10.txt");
    }

    [Theory]
    [InlineData("a1", "ab")]          // a number sorts before text at the same position
    [InlineData("v2", "v02a")]        // leading zeros do not change the value
    [InlineData("abc", "abcd")]       // a prefix comes first
    [InlineData("x99999999999999999999", "x100000000000000000000")] // longer than a long
    public void NaturalCompare_Pairs_OrderAsExplorer(string first, string second)
    {
        NaturalStringComparer.Instance.Compare(first, second).Should().BeNegative();
        NaturalStringComparer.Instance.Compare(second, first).Should().BePositive();
    }

    [Fact]
    public void NaturalCompare_SameNameDifferentLeadingZeros_IsStableNotEqual()
    {
        NaturalStringComparer.Instance.Compare("a01", "a1").Should().NotBe(0);
    }

    // ── Listing order ─────────────────────────────────────────────────────────

    private static IFileEntry Entry(string name, long size = 0, bool dir = false, int day = 1) =>
        new FileEntry(new VfsPath("/x/" + name), name, dir, size, new DateTime(2026, 1, day),
            dir ? FileAttributes.Directory : FileAttributes.Normal);

    [Fact]
    public void FileEntryComparer_EqualSizes_FallBackToNaturalNameOrder()
    {
        var entries = new[] { Entry("b10", 5), Entry("b2", 5), Entry("a", 9) };

        var sorted = entries.Order(new FileEntryComparer(SortColumn.Size, descending: false, foldersFirst: true));

        sorted.Select(e => e.Name).Should().Equal("b2", "b10", "a");
    }

    [Fact]
    public void FileEntryComparer_Descending_KeepsFoldersFirstAndNamesAscendingOnTies()
    {
        var entries = new[] { Entry("z", 1, day: 2), Entry("dir", dir: true), Entry("b", 1, day: 2), Entry("old", 1, day: 1) };

        var sorted = entries.Order(new FileEntryComparer(SortColumn.Date, descending: true, foldersFirst: true));

        sorted.Select(e => e.Name).Should().Equal("dir", "b", "z", "old");
    }

    [Fact]
    public void FileEntryComparer_ByNameDescending_ReversesNaturalOrder()
    {
        var entries = new[] { Entry("f2"), Entry("f10"), Entry("f1") };

        var sorted = entries.Order(new FileEntryComparer(SortColumn.Name, descending: true, foldersFirst: false));

        sorted.Select(e => e.Name).Should().Equal("f10", "f2", "f1");
    }

    [Fact]
    public void FileEntryComparer_BySizeWithMeasuredFolders_PutsTheHeaviestFolderFirst()
    {
        var entries = new[] { Entry("bin", dir: true), Entry("node_modules", dir: true), Entry("src", dir: true) };
        var measured = new Dictionary<string, long> { ["bin"] = 50, ["node_modules"] = 900, ["src"] = 10 };

        var sorted = entries.Order(new FileEntryComparer(SortColumn.Size, descending: true, foldersFirst: true,
            sizeOf: e => measured[e.Name]));

        sorted.Select(e => e.Name).Should().Equal("node_modules", "bin", "src");
    }

    // ── Breadcrumbs ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildBreadcrumbs_DrivePath_StartsWithTheDriveRoot()
    {
        var segments = FileListViewModel.BuildBreadcrumbs(@"C:\Projects\app", mounted: false);

        segments.Select(s => s.Path).Should().Equal(@"C:\", @"C:\Projects", @"C:\Projects\app");
        segments.Select(s => s.DisplayName).Should().Equal("C:", "Projects", "app");
    }

    [Fact]
    public void BuildBreadcrumbs_UncPath_KeepsServerAndShareTogether()
    {
        var segments = FileListViewModel.BuildBreadcrumbs(@"\\srv\share\docs", mounted: false);

        segments.Select(s => s.Path).Should().Equal(@"\\srv\share\", @"\\srv\share\docs");
        segments[0].DisplayName.Should().Be(@"\\srv\share");
    }

    [Fact]
    public void BuildBreadcrumbs_MountedPath_KeepsRootedVfsPaths()
    {
        var segments = FileListViewModel.BuildBreadcrumbs("/home/user", mounted: true);

        segments.Select(s => s.Path).Should().Equal("/home", "/home/user");
    }

    // ── Typed address ─────────────────────────────────────────────────────────

    private static FileListViewModel MountedList(out InMemoryFileSystem archive)
    {
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        archive = new InMemoryFileSystem("zip");
        archive.AddDirectory("/docs");
        list.Mount(archive, "/");
        return list;
    }

    [Fact]
    public async Task TryNavigateToTypedPath_MountedFolderThatExists_GoesThereInsideTheMount()
    {
        var list = MountedList(out _);

        var ok = await list.TryNavigateToTypedPathAsync("/docs");

        ok.Should().BeTrue();
        list.IsMounted.Should().BeTrue();
        list.CurrentPath.Should().Be("/docs");
    }

    [Fact]
    public async Task TryNavigateToTypedPath_MountedFolderThatIsMissing_StaysAndReportsFalse()
    {
        var list = MountedList(out _);

        var ok = await list.TryNavigateToTypedPathAsync("/nope");

        ok.Should().BeFalse();
        list.CurrentPath.Should().Be("/");
    }

    [Fact]
    public async Task TryNavigateToTypedPath_DiskPathWhileMounted_LeavesTheMount()
    {
        var list = MountedList(out var archive);
        var disk = Path.GetTempPath();

        var ok = await list.TryNavigateToTypedPathAsync(disk);

        ok.Should().BeTrue();
        list.IsMounted.Should().BeFalse("a disk path must not be listed through the archive");
        archive.IsDisposed.Should().BeTrue();
        list.CurrentPath.Should().Be(Path.GetFullPath(disk));
    }

    [Fact]
    public async Task TryNavigateToTypedPath_RelativePathOnDisk_IsRejected()
    {
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);

        (await list.TryNavigateToTypedPathAsync("src")).Should().BeFalse();
    }

    [Fact]
    public void ReturnToDisk_PopsEveryMountAndShowsTheDiskFolderUnderneath()
    {
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance)
        {
            CurrentPath = "/C:/work",
        };
        list.Mount(new InMemoryFileSystem("zip"), "/");
        list.Mount(new InMemoryFileSystem("zip"), "/inner");

        list.ReturnToDisk();

        list.IsMounted.Should().BeFalse();
        list.CurrentPath.Should().Be("/C:/work");
    }
}
