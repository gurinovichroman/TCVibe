using System.IO;
using FluentAssertions;
using TCV.App.FileSystem;
using TCV.App.ViewModels;

namespace TCV.App.Tests;

/// <summary>
/// The drive selector (6.52): it follows drives that come and go after startup, and on a path that
/// is on no listed drive — a UNC share — it stops claiming the panel is on the previous drive.
/// </summary>
public class DriveListTests
{
    private static readonly string[] Drives = [@"C:\", @"D:\"];

    // ── Which drive the selector shows ────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Projects\app", 0)]
    [InlineData(@"d:\", 1)]
    [InlineData("D:", 1)]
    public void Resolve_PathOnAListedDrive_SelectsIt(string path, int index)
    {
        var (driveIndex, location) = DriveSelection.Resolve(path, Drives);

        driveIndex.Should().Be(index);
        location.Should().BeEmpty();
    }

    [Fact]
    public void Resolve_UncPath_SelectsNothing_AndShowsTheShare()
    {
        var (driveIndex, location) = DriveSelection.Resolve(@"\\server\share\docs\2026", Drives);

        driveIndex.Should().Be(-1, "the previous drive must not stay selected on a network share");
        location.Should().Be(@"\\server\share");
    }

    [Fact]
    public void Resolve_DriveMissingFromTheList_SelectsNothing_AndShowsTheDrive()
    {
        // A drive that is not ready (an empty card reader, a sleeping network drive) is not listed.
        var (driveIndex, location) = DriveSelection.Resolve(@"z:\backup", Drives);

        driveIndex.Should().Be(-1);
        location.Should().Be(@"Z:\");
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Resolve_NoPath_SelectsNothing_AndShowsNothing(string? path)
    {
        DriveSelection.Resolve(path, Drives).Should().Be((-1, string.Empty));
    }

    // ── Refreshing the list in place ──────────────────────────────────────────

    private static DriveEntry Drive(string name, string label = "", long free = 1) =>
        new(name, label, "NTFS", 100, free, DriveType.Fixed);

    [Fact]
    public void Merge_ReusesTheEntriesOfDrivesThatLookTheSame()
    {
        // Same name and label: the selector keeps its selected object, so nothing flickers. Free space
        // does not make a drive "different" — it changes all the time (the kept entry takes the new numbers).
        var c = Drive(@"C:\", "System");
        var shown = new[] { c, Drive(@"E:\", "Stick") };

        var merged = DriveBarViewModel.Merge(shown, [Drive(@"C:\", "System", free: 999), Drive(@"F:\", "Card")]);

        merged.Select(d => d.Name).Should().Equal(@"C:\", @"F:\");
        merged[0].Should().BeSameAs(c);
    }

    [Fact]
    public void Merge_AKeptEntry_TakesTheFreshSpace_AndTellsTheTooltip()
    {
        // The drive button's tooltip says «N free of M»: a kept entry that kept its start-up numbers would
        // go on saying how much room the drive had when the program started.
        var c = Drive(@"C:\", "System", free: 50);
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        DriveBarViewModel.Merge([c], [Drive(@"C:\", "System", free: 20)]).Single().Should().BeSameAs(c);

        c.FreeSpace.Should().Be(20);
        changed.Should().Contain(nameof(DriveEntry.SpaceText));
    }

    [Fact]
    public void Merge_ASameSizedDrive_RaisesNothing()
    {
        var c = Drive(@"C:\", "System", free: 50);
        var changed = new List<string?>();
        c.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        DriveBarViewModel.Merge([c], [Drive(@"C:\", "System", free: 50)]);

        changed.Should().BeEmpty();
    }

    [Fact]
    public void Merge_ARelabelledDrive_GetsTheFreshEntry()
    {
        var shown = new[] { Drive(@"E:\", "Old") };
        var fresh = Drive(@"E:\", "New");

        DriveBarViewModel.Merge(shown, [fresh]).Single().Should().BeSameAs(fresh);
    }

    // ── Hearing about drives ──────────────────────────────────────────────────

    private const int WmDeviceChange = 0x0219;

    [Theory]
    [InlineData(WmDeviceChange, 0x8000, 2, true)]    // a volume arrived (USB stick, card, DVD)
    [InlineData(WmDeviceChange, 0x8004, 2, true)]    // a volume was removed
    [InlineData(WmDeviceChange, 0x8000, 5, false)]   // a device interface (a mouse, a phone)
    [InlineData(WmDeviceChange, 0x0007, null, false)] // DBT_DEVNODES_CHANGED: any device at all
    [InlineData(0x0010, 0x8000, 2, false)]           // not a device message
    public void IsVolumeEvent_OnlyVolumesComingAndGoing(int message, long eventCode, int? deviceType, bool expected)
    {
        DriveManager.IsVolumeEvent(message, eventCode, deviceType).Should().Be(expected);
    }

    [Fact]
    public async Task NotifyDevicesChanged_SeveralInARow_RaiseOneChange()
    {
        // A card reader reports each slot: the drive list is read once, not once per message.
        using var manager = new DriveManager();
        var raised = 0;
        manager.DrivesChanged += (_, _) => Interlocked.Increment(ref raised);

        manager.NotifyDevicesChanged();
        manager.NotifyDevicesChanged();
        manager.NotifyDevicesChanged();

        for (var i = 0; i < 100 && Volatile.Read(ref raised) == 0; i++)
        {
            await Task.Delay(20);
        }

        await Task.Delay(700);
        raised.Should().Be(1);
    }

    [Fact]
    public async Task NotifyDevicesChanged_AfterDispose_RaisesNothing()
    {
        // A WM_DEVICECHANGE or a poll in flight while the window closes must not bring the timer back.
        var manager = new DriveManager();
        var raised = 0;
        manager.DrivesChanged += (_, _) => Interlocked.Increment(ref raised);
        manager.Dispose();

        manager.NotifyDevicesChanged();

        await Task.Delay(700);
        raised.Should().Be(0);
    }
}
