using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>
/// ROADMAP 6.54: an SSH link passed every check, was even pasted in from the clipboard, and then
/// always failed — this build of libgit2 has no SSH transport. A well-known host's link is now
/// cloned over its https twin; any other SSH link is refused before anything touches the network.
/// </summary>
public class GitCloneUrlTests
{
    [Theory]
    [InlineData("git@github.com:owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("git@github.com:owner/repo", "https://github.com/owner/repo")]
    [InlineData("ssh://git@github.com/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("ssh://git@github.com:22/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("ssh://git@ssh.github.com:443/owner/repo.git", "https://github.com/owner/repo.git")]
    [InlineData("git@gitlab.com:group/sub/repo.git", "https://gitlab.com/group/sub/repo.git")]
    [InlineData("git@bitbucket.org:team/repo.git", "https://bitbucket.org/team/repo.git")]
    [InlineData("git@codeberg.org:owner/repo.git", "https://codeberg.org/owner/repo.git")]
    [InlineData("  git@GitHub.com:owner/repo.git/  ", "https://github.com/owner/repo.git")]
    public void Resolve_SshLinkToAWellKnownHost_IsRewrittenToHttps(string url, string expected)
    {
        var resolved = GitCloneTarget.Resolve(url);

        resolved.Status.Should().Be(GitCloneUrlStatus.RewrittenToHttps);
        resolved.Url.Should().Be(expected);
        resolved.CanClone.Should().BeTrue();
    }

    [Fact]
    public void Resolve_RewrittenLink_DropsTheSshUserAndNamesTheHttpsHost()
    {
        // A user name means nothing over https and would only make the host ask for its password.
        var resolved = GitCloneTarget.Resolve("ssh://alice@gitlab.com/owner/repo.git");

        resolved.Url.Should().Be("https://gitlab.com/owner/repo.git");
        resolved.Host.Should().Be("gitlab.com");
    }

    [Fact]
    public void Resolve_RewrittenLink_KeepsTheFolderName()
    {
        var resolved = GitCloneTarget.Resolve("git@github.com:owner/my-repo.git");

        GitCloneTarget.RepositoryName(resolved.Url).Should().Be("my-repo");
    }

    [Theory]
    [InlineData("git@git.example.com:owner/repo.git", "git.example.com")]
    [InlineData("ssh://git@git.example.com:2222/owner/repo.git", "git.example.com")]
    [InlineData("deploy@10.0.0.5:repos/thing.git", "10.0.0.5")]
    [InlineData("ssh://git@[::1]:22/repo.git", "[::1]")]
    public void Resolve_SshLinkToAnUnknownHost_IsRefusedNamingTheHost(string url, string host)
    {
        var resolved = GitCloneTarget.Resolve(url);

        resolved.Status.Should().Be(GitCloneUrlStatus.SshUnsupported);
        resolved.Host.Should().Be(host);
        resolved.CanClone.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/owner/repo.git")]
    [InlineData("http://host/owner/repo.git")]
    [InlineData("git://host/owner/repo.git")]
    [InlineData("file:///d:/repos/thing")]
    [InlineData(@"D:\repos\thing")]
    [InlineData(@"\\share\repos\thing")]
    public void Resolve_EveryOtherForm_IsUsedAsTyped(string url)
    {
        var resolved = GitCloneTarget.Resolve("  " + url + " ");

        resolved.Status.Should().Be(GitCloneUrlStatus.Ready);
        resolved.Url.Should().Be(url);
        resolved.CanClone.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("just some text")]
    [InlineData("https://host/")]
    public void Resolve_NotAUrl_IsInvalid(string? url)
    {
        var resolved = GitCloneTarget.Resolve(url);

        resolved.Status.Should().Be(GitCloneUrlStatus.Invalid);
        resolved.CanClone.Should().BeFalse();
    }

    [Fact]
    public async Task CloneAsync_SshLinkToAnUnknownHost_IsRefusedBeforeTouchingTheDisk()
    {
        var service = new GitCloneService(NullLogger<GitCloneService>.Instance);
        var target = Path.Combine(Path.GetTempPath(), $"tcv_clone_ssh_{Guid.NewGuid():N}");

        var result = await service.CloneAsync("git@git.example.invalid:owner/repo.git", target);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.SshUnsupported);
        result.Detail.Should().Be("git.example.invalid");
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public void Describe_SshFailure_IsClassifiedWithItsHost()
    {
        var result = GitCloneErrors.Describe("git@git.example.com:o/r.git", "unsupported URL protocol");

        result.Kind.Should().Be(GitFailureKind.SshUnsupported);
        result.Detail.Should().Be("git.example.com");
    }

    [Fact]
    public void Describe_CertificateProblem_IsClassifiedWithTheReason()
    {
        var result = GitCloneErrors.Describe("https://host/r.git", "the SSL certificate is invalid");

        result.Kind.Should().Be(GitFailureKind.Certificate);
        result.Detail.Should().Contain("certificate");
    }

    [Fact]
    public void Describe_UnclassifiedFailure_CarriesTheRawTextAsTheDetail()
    {
        // The UI wraps it in a sentence of its own; showing libgit2's English bare is what 6.53 removed.
        var result = GitCloneErrors.Describe("https://host/r.git", "something odd");

        result.Kind.Should().Be(GitFailureKind.Unexpected);
        result.Detail.Should().Be("something odd");
    }
}
