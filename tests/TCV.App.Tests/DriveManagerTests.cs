using System.IO;
using FluentAssertions;
using TCV.App.FileSystem;

namespace TCV.App.Tests;

public class DriveManagerTests
{
    [Fact]
    public void GetDrives_ReturnsAtLeastOneDrive()
    {
        var manager = new DriveManager();
        var drives = manager.GetDrives();
        drives.Should().NotBeEmpty();
    }

    [Fact]
    public void GetDrives_AllDrivesHaveName()
    {
        var manager = new DriveManager();
        var drives = manager.GetDrives();
        drives.Should().AllSatisfy(d => d.Name.Should().NotBeNullOrEmpty());
    }

    [Fact]
    public void GetDrives_SystemDriveIsPresent()
    {
        var manager = new DriveManager();
        var drives = manager.GetDrives();
        var systemDriveLetter = Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 2);
        drives.Should().Contain(d => d.Name.StartsWith(systemDriveLetter, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void DriveEntry_DisplayName_IncludesLabel()
    {
        var entry = new DriveEntry("C:\\", "System", "NTFS", 100_000, 50_000, DriveType.Fixed);
        entry.DisplayName.Should().Contain("System");
        entry.DisplayName.Should().Contain("C:");
    }

    [Fact]
    public void DriveEntry_DisplayName_WhenNoLabel_ShowsName()
    {
        var entry = new DriveEntry("D:\\", "", "NTFS", 100_000, 50_000, DriveType.Fixed);
        entry.DisplayName.Should().Be("D:\\");
    }
}
