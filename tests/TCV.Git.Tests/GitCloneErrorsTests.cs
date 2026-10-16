using FluentAssertions;
using TCV.Git;

namespace TCV.Git.Tests;

public class GitCloneErrorsTests
{
    [Theory]
    [InlineData("https://github.com/o/private.git", "unexpected http status code: 401")]
    [InlineData("https://github.com/o/private.git", "remote authentication required but no callback set")]
    [InlineData("https://host/o/r.git", "too many redirects or authentication replays")]
    [InlineData("https://host/o/r.git", "request failed with status code: 403")]
    public void Describe_AuthenticationFailures_AreClassifiedSoTheDialogCanAskForCredentials(string url, string raw)
    {
        var result = GitCloneErrors.Describe(url, raw);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.Authentication);
        result.Message.Should().Contain("access token");
    }

    [Fact]
    public void Describe_CertificateProblem_KeepsTheRawReason()
    {
        var result = GitCloneErrors.Describe("https://host/r.git", "the SSL certificate is invalid");

        result.Kind.Should().Be(GitFailureKind.Certificate);
        result.Message.Should().Contain("certificate");
    }

    [Fact]
    public void Describe_SshFailure_SaysSshKeysAreNotSupported()
    {
        var result = GitCloneErrors.Describe("git@github.com:o/r.git", "connection refused");

        result.Message.Should().Contain("SSH").And.Contain("https");
    }

    [Fact]
    public void Describe_AnythingElse_PassesTheMessageThrough()
    {
        GitCloneErrors.Describe("https://host/r.git", "something odd").Message.Should().Be("something odd");
    }

    [Theory]
    [InlineData("https://user:s3cret@host/o/r.git", "https://***@host/o/r.git")]
    [InlineData("https://host/o/r.git", "https://host/o/r.git")]
    [InlineData("https://host/o/r@2.git", "https://host/o/r@2.git")]
    [InlineData("git@github.com:o/r.git", "git@github.com:o/r.git")]
    public void RedactUrl_HidesInlineCredentialsOnly(string url, string expected)
    {
        GitCloneService.RedactUrl(url).Should().Be(expected);
    }

    [Fact]
    public void GitCredentials_ToString_DoesNotLeakTheSecret()
    {
        new GitCredentials("me", "hunter2").ToString().Should().NotContain("hunter2");
    }

    [Theory]
    [InlineData(@"D:\my repos\thing")]
    [InlineData(@"\\server\share\my repos\thing")]
    public void LooksLikeRepositoryUrl_LocalPathWithSpaces_IsAccepted(string path)
    {
        GitCloneTarget.LooksLikeRepositoryUrl(path).Should().BeTrue();
        GitCloneTarget.RepositoryName(path).Should().Be("thing");
    }

    [Fact]
    public void LooksLikeRepositoryUrl_RemoteUrlWithSpaces_IsStillRejected()
    {
        GitCloneTarget.LooksLikeRepositoryUrl("https://host/o/my repo.git").Should().BeFalse();
    }
}
