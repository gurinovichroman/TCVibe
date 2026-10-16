using FluentAssertions;

namespace TCV.Git.Tests;

public class GitCloneTargetTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo.git", "repo")]
    [InlineData("https://github.com/owner/repo", "repo")]
    [InlineData("https://github.com/owner/repo/", "repo")]
    [InlineData("http://host/owner/repo.git", "repo")]
    [InlineData("ssh://git@host:22/owner/repo.git", "repo")]
    [InlineData("git://host/owner/repo.git", "repo")]
    [InlineData("git@github.com:owner/repo.git", "repo")]
    [InlineData("git@github.com:repo.git", "repo")]
    [InlineData("https://user:token@host/owner/repo.git", "repo")]
    [InlineData(@"D:\repos\thing", "thing")]
    [InlineData(@"\\share\repos\thing", "thing")]
    public void RepositoryName_DerivesTheFolder(string url, string expected)
    {
        GitCloneTarget.RepositoryName(url).Should().Be(expected);
    }

    [Fact]
    public void RepositoryName_IgnoresQueryAndFragment()
    {
        // Browser address bars hand over more than the repository.
        GitCloneTarget.RepositoryName("https://host/owner/repo?tab=readme#top").Should().Be("repo");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("https://host/")]
    public void RepositoryName_NothingUsable_ReturnsEmpty(string? url)
    {
        GitCloneTarget.RepositoryName(url).Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo.git")]
    [InlineData("ssh://git@host/owner/repo")]
    [InlineData("file:///d:/repos/thing")]
    [InlineData(@"D:\repos\thing")]
    public void LooksLikeRepositoryUrl_AcceptsEveryFormGitAccepts(string url)
    {
        GitCloneTarget.LooksLikeRepositoryUrl(url).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("repo")]
    [InlineData("https://host/ with space")]
    public void LooksLikeRepositoryUrl_RejectsTheRest(string? url)
    {
        GitCloneTarget.LooksLikeRepositoryUrl(url).Should().BeFalse();
    }

    [Fact]
    public void RepositoryName_DriveLetterIsNotAnScpHost()
    {
        // "D:\repos\thing" has a colon at index 1 like "host:path" does; reading it as an scp URL
        // would name the folder after the entire path.
        GitCloneTarget.RepositoryName(@"D:\repos\thing").Should().Be("thing");
    }
}
