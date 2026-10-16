using FluentAssertions;
using TCV.App.FileSystem;
using TCV.App.ViewModels;
using TCV.Core.Domain;

namespace TCV.App.Tests;

public class StatusBarViewModelTests
{
    private readonly StatusBarViewModel _sut = new(new DriveManager());

    private static FileEntryViewModel Entry(string name, bool isDirectory, long size) =>
        new(new FileEntry(new VfsPath("C:/tmp/" + name), name, isDirectory, size,
            DateTime.Now, System.IO.FileAttributes.Normal));

    [Fact]
    public void Update_UnmeasuredFolder_AddsNothing()
    {
        var selected = new[]
        {
            Entry("a.txt", isDirectory: false, size: 100),
            Entry("b.txt", isDirectory: false, size: 250),
            Entry("folder", isDirectory: true, size: 999),
        };

        _sut.Update(selected, selected, path: @"C:\tmp");

        _sut.SelectedCount.Should().Be(3);
        _sut.SelectedSizedCount.Should().Be(2);
        _sut.SelectedBytes.Should().Be(350);
        _sut.SelectedInfo.Should().NotBeEmpty();
    }

    [Fact]
    public void Update_MeasuredFolder_AddsItsSize()
    {
        var folder = Entry("folder", isDirectory: true, size: 0);
        folder.FolderSize = 4096;
        var selected = new[] { Entry("a.txt", isDirectory: false, size: 100), folder };

        _sut.Update(selected, selected, path: @"C:\tmp");

        _sut.SelectedSizedCount.Should().Be(2);
        _sut.SelectedBytes.Should().Be(4196);
    }

    [Fact]
    public void Update_OnlyFoldersNoneMeasured_ShowsNoSize()
    {
        var selected = new[] { Entry("src", isDirectory: true, size: 0) };

        _sut.Update(selected, selected, path: @"C:\tmp");

        _sut.SelectedSizedCount.Should().Be(0);
        _sut.SelectedInfo.Should().NotContain("(");
    }

    [Fact]
    public void Update_CountsTheListingWithoutTheParentRow()
    {
        var listing = new[]
        {
            FileEntryViewModel.CreateParent(),
            Entry("src", isDirectory: true, size: 0),
            Entry("a.txt", isDirectory: false, size: 10),
            Entry("b.txt", isDirectory: false, size: 20),
        };

        _sut.Update([], listing, path: @"C:\tmp");

        _sut.TotalCount.Should().Be(3);
        _sut.FolderCount.Should().Be(1);
        _sut.FileCount.Should().Be(2);
        _sut.TotalBytes.Should().Be(30);
    }

    [Fact]
    public void Update_NoSelection_SummarisesTheFolderInsteadOfGoingBlank()
    {
        _sut.Update([], [Entry("a.txt", isDirectory: false, size: 1)], path: @"C:\tmp");

        _sut.SelectedInfo.Should().NotBeEmpty();
    }

    [Fact]
    public void Update_EmptyFolderWithoutSelection_ShowsNothing()
    {
        _sut.Update([], [FileEntryViewModel.CreateParent()], path: @"C:\tmp");

        _sut.SelectedInfo.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_DriveRoot_MeasuresFreeSpaceOffTheCallingThread()
    {
        _sut.Update([], [], path: @"C:\");
        await _sut.FreeSpaceRefreshed;

        _sut.CurrentDrive.Should().Be(@"C:\");
        _sut.FreeSpace.Should().BePositive();
        _sut.TotalSpace.Should().BeGreaterThanOrEqualTo(_sut.FreeSpace);
        _sut.HasFreeSpace.Should().BeTrue();
        _sut.FreeSpaceText.Should().NotBeEmpty();
        _sut.UsedFraction.Should().BeInRange(0, 1);
        _sut.FreeSpaceInfo.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData("C:")]      // shorter than a full drive root
    [InlineData("/docs")]   // inside an archive
    [InlineData("")]
    public async Task Update_PathWithoutDrive_ClearsDriveInsteadOfThrowing(string path)
    {
        _sut.Update([], [], @"C:\");   // start with a drive set
        await _sut.FreeSpaceRefreshed;

        var act = () => _sut.Update([], [], path);

        act.Should().NotThrow();
        await _sut.FreeSpaceRefreshed;
        _sut.CurrentDrive.Should().BeEmpty();
        _sut.HasFreeSpace.Should().BeFalse();
        _sut.FreeSpaceText.Should().BeEmpty();
        _sut.FreeSpaceInfo.Should().BeEmpty();
    }
}
