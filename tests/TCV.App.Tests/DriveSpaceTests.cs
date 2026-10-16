using FluentAssertions;
using TCV.App.FileSystem;

namespace TCV.App.Tests;

public class DriveSpaceTests
{
    [Theory]
    [InlineData(@"C:\Users\me", @"C:\")]
    [InlineData(@"d:\", @"D:\")]
    [InlineData("C:/Projects", @"C:\")]
    [InlineData(@"\\server\share\docs\a", @"\\server\share\")]
    [InlineData(@"\\server\share", @"\\server\share\")]
    [InlineData(@"\\server", "")]
    [InlineData(@"\\?\C:\long", "")]
    [InlineData("C:", "")]
    [InlineData("/docs", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void RootOf_WindowsPath_GivesTheVolumeRoot(string? path, string expected)
    {
        DriveSpace.RootOf(path).Should().Be(expected);
    }

    [Fact]
    public void UsedFraction_IsTheShareThatIsNotFree()
    {
        new DriveSpace(Free: 25, Total: 100).UsedFraction.Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void UsedFraction_UnknownSize_IsZeroRatherThanNaN()
    {
        new DriveSpace(Free: 0, Total: 0).UsedFraction.Should().Be(0);
    }

    [Theory]
    [InlineData(9, 100, true)]
    [InlineData(10, 100, false)]
    [InlineData(50, 100, false)]
    [InlineData(0, 0, false)]
    public void IsLow_UnderATenthLeft(long free, long total, bool expected)
    {
        new DriveSpace(free, total).IsLow.Should().Be(expected);
    }
}
