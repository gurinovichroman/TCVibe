using FluentAssertions;
using TCV.Git;

namespace TCV.Git.Tests;

public class GitWorktreePathsTests
{
    [Theory]
    [InlineData(@"D:\wt\feature", @"D:\wt\feature", true)]
    [InlineData(@"D:\wt\feature\src\a.cs", @"D:\wt\feature", true)]
    [InlineData(@"d:\WT\Feature\src", @"D:\wt\feature\", true)]
    [InlineData(@"D:\wt\feature-2", @"D:\wt\feature", false)]
    [InlineData(@"D:\wt", @"D:\wt\feature", false)]
    [InlineData(@"E:\wt\feature", @"D:\wt\feature", false)]
    [InlineData("", @"D:\wt\feature", false)]
    [InlineData(@"D:\wt\feature", "", false)]
    public void IsUnder_ComparesPathsTheWayWindowsDoes(string path, string root, bool expected)
    {
        GitWorktreePaths.IsUnder(path, root).Should().Be(expected);
    }

    [Fact]
    public void UniqueName_FreeName_IsKept()
    {
        GitWorktreePaths.UniqueName("repo/feature", new[] { "other" }).Should().Be("repo/feature");
    }

    [Fact]
    public void UniqueName_TakenName_GetsTheFirstFreeSuffix_IgnoringCase()
    {
        var taken = new[] { "Repo/Feature", "repo/feature (2)" };

        GitWorktreePaths.UniqueName("repo/feature", taken).Should().Be("repo/feature (3)");
    }
}
