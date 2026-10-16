using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public class FileProgressTests
{
    [Fact]
    public void Percentage_CalculatesCorrectly()
    {
        var progress = new FileProgress(500, 1000, "file.txt", TimeSpan.FromSeconds(1));
        progress.Percentage.Should().BeApproximately(50.0, 0.001);
    }

    [Fact]
    public void Percentage_WhenTotalIsZero_ReturnsZero()
    {
        var progress = new FileProgress(0, 0, "file.txt", TimeSpan.Zero);
        progress.Percentage.Should().Be(0);
    }

    [Fact]
    public void Percentage_WhenComplete_Returns100()
    {
        var progress = new FileProgress(1000, 1000, "file.txt", TimeSpan.FromSeconds(2));
        progress.Percentage.Should().BeApproximately(100.0, 0.001);
    }

    [Fact]
    public void Record_EqualityIsValueBased()
    {
        var p1 = new FileProgress(100, 200, "a.txt", TimeSpan.FromSeconds(1));
        var p2 = new FileProgress(100, 200, "a.txt", TimeSpan.FromSeconds(1));
        p1.Should().Be(p2);
    }
}
