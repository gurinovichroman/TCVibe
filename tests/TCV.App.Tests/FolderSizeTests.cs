using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.ViewModels;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>The size of a folder: the walk that adds it up, and Space and «Folder sizes» in a panel.</summary>
public class FolderSizeTests
{
    private static InMemoryFileSystem Project()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddFile("/C:/work/src/a.cs", new byte[100]);
        fs.AddFile("/C:/work/src/deep/er/b.cs", new byte[50]);
        fs.AddDirectory("/C:/work/src/empty");
        fs.AddFile("/C:/work/readme.md", new byte[7]);
        return fs;
    }

    // ── The walk ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CountAsync_NestedFolders_AddsEveryFileAtAnyDepth()
    {
        var result = await FolderSizeCounter.CountAsync(Project(), new VfsPath("/C:/work/src"), null, CancellationToken.None);

        result.Bytes.Should().Be(150);
        result.Files.Should().Be(2);
        result.Folders.Should().Be(3);   // deep, deep/er, empty
        result.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task CountAsync_ALinkedFolder_IsCountedButNotEntered()
    {
        var fs = Project();
        fs.AddFile("/C:/work/src/link/elsewhere.bin", new byte[1000]);

        var result = await FolderSizeCounter.CountAsync(fs, new VfsPath("/C:/work/src"), e => e.Name == "link", CancellationToken.None);

        result.Bytes.Should().Be(150, "a junction's content lives elsewhere and would be counted twice");
        result.Folders.Should().Be(4);
    }

    [Fact]
    public async Task CountAsync_AnUnreadableSubfolder_CountsTheRestAndSaysItIsIncomplete()
    {
        var fs = Project();
        fs.FailListingOf = "/C:/work/src/deep";

        var result = await FolderSizeCounter.CountAsync(fs, new VfsPath("/C:/work/src"), null, CancellationToken.None);

        result.Bytes.Should().Be(100);
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public async Task CountAsync_ASubfolderListingTimesOut_CountsTheRestAndSaysItIsIncomplete()
    {
        var fs = Project();
        fs.FailListingOf = "/C:/work/src/deep";
        fs.FailListingWith = () => new TaskCanceledException("HTTP timeout");

        var result = await FolderSizeCounter.CountAsync(fs, new VfsPath("/C:/work/src"), null, CancellationToken.None);

        result.Bytes.Should().Be(100, "a cloud drive's timeout is an unreadable folder, not the walk being stopped");
        result.Complete.Should().BeFalse();
    }

    [Fact]
    public async Task CountAsync_Cancelled_Throws()
    {
        var act = () => FolderSizeCounter.CountAsync(Project(), new VfsPath("/C:/work/src"), null, new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void ReparsePointIsLink_IsTheDefaultTestOffTheDisk()
    {
        var junction = new FileEntry(new VfsPath("/x/j"), "j", true, 0, DateTime.Now, FileAttributes.Directory | FileAttributes.ReparsePoint);
        var folder = new FileEntry(new VfsPath("/x/f"), "f", true, 0, DateTime.Now, FileAttributes.Directory);

        FolderSizeCounter.ReparsePointIsLink(junction).Should().BeTrue();
        FolderSizeCounter.ReparsePointIsLink(folder).Should().BeFalse();
    }

    // ── The panel ─────────────────────────────────────────────────────────────

    private static async Task<(FileListViewModel List, FileEntryViewModel Src, FileEntryViewModel Readme)> PanelOn(InMemoryFileSystem fs)
    {
        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance) { CurrentPath = "/C:/work" };
        var src = new FileEntryViewModel((await fs.GetEntryAsync(new VfsPath("/C:/work/src")))!);
        var readme = new FileEntryViewModel((await fs.GetEntryAsync(new VfsPath("/C:/work/readme.md")))!);
        list.Files.Clear();
        list.Files.Add(src);
        list.Files.Add(readme);
        list.SelectedItem = src;
        return (list, src, readme);
    }

    [Fact]
    public async Task SpaceOnAFolder_MarksItMeasuresItAndMovesDown()
    {
        var (list, src, readme) = await PanelOn(Project());
        var announced = 0;
        list.FolderSizesChanged += (_, _) => announced++;

        list.ToggleSelectWithSizeAndAdvance();
        await list.FolderSizesMeasured;

        src.IsSelected.Should().BeTrue();
        src.FolderSize.Should().Be(150);
        src.IsMeasuringSize.Should().BeFalse();
        src.SizeDisplay.Should().NotBe("—");
        list.SelectedItem.Should().BeSameAs(readme);
        announced.Should().Be(1, "the status line adds the size of a marked folder when it arrives");
    }

    [Fact]
    public async Task SpaceUnmarkingAFolder_MeasuresNothing()
    {
        var (list, src, _) = await PanelOn(Project());
        list.ToggleSelect(src);

        list.ToggleSelectWithSizeAndAdvance();
        await list.FolderSizesMeasured;

        src.IsSelected.Should().BeFalse();
        src.FolderSize.Should().BeNull();
        src.SizeDisplay.Should().Be("—");
    }

    [Fact]
    public async Task SpaceOnAFile_JustMarksIt()
    {
        var (list, _, readme) = await PanelOn(Project());
        list.SelectedItem = readme;

        list.ToggleSelectWithSizeAndAdvance();
        await list.FolderSizesMeasured;

        readme.IsSelected.Should().BeTrue();
        readme.FolderSize.Should().BeNull();
    }

    [Fact]
    public async Task FolderSizes_MeasuresEveryFolderWithoutMarkingAny()
    {
        var (list, src, _) = await PanelOn(Project());

        list.MeasureAllFolderSizes();
        await list.FolderSizesMeasured;

        src.FolderSize.Should().Be(150);
        list.SelectedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task AFolderPartlyUnreadable_ShowsItsSizeAsALowerBound()
    {
        var fs = Project();
        fs.FailListingOf = "/C:/work/src/deep";
        var (list, src, _) = await PanelOn(fs);

        list.MeasureAllFolderSizes();
        await list.FolderSizesMeasured;

        src.FolderSizeComplete.Should().BeFalse();
        src.SizeDisplay.Should().StartWith("≥");
    }
}
