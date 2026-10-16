using FluentAssertions;
using TCV.App.Services;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// ROADMAP 6.53: a git failure reached the user as libgit2's English. Every classified failure is
/// now said in the user's language; only an unclassified one (<see cref="GitFailureKind.None"/>)
/// still falls back to the service's own message.
/// </summary>
public class GitResultTextTests
{
    private const string RawEnglish = "libgit2 said something in English";

    public static TheoryData<GitFailureKind> ClassifiedKinds()
    {
        var data = new TheoryData<GitFailureKind>();
        foreach (var kind in Enum.GetValues<GitFailureKind>().Where(k => k != GitFailureKind.None))
        {
            data.Add(kind);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ClassifiedKinds))]
    public void Describe_EveryClassifiedFailure_IsLocalisedRatherThanTheRawMessage(GitFailureKind kind)
    {
        var text = GitResultText.Describe(GitOperationResult.Fail(RawEnglish, kind, "detail"));

        text.Should().NotBe(RawEnglish, $"{kind} needs a localised sentence in GitResultText");
    }

    [Fact]
    public void Describe_Success_IsTheMessage()
    {
        GitResultText.Describe(GitOperationResult.Ok("done")).Should().Be("done");
    }

    [Fact]
    public void Describe_SuccessThatLeftItsFolder_IsLocalisedRatherThanTheRawReason()
    {
        // A success used to be its message as is; this one carries the file system's English reason.
        var result = new GitOperationResult(true, RawEnglish, GitFailureKind.DirectoryNotDeleted, @"D:\wt\scratch");

        GitResultText.Describe(result).Should().NotBe(RawEnglish);
    }

    [Fact]
    public void SshUnsupported_UnknownHost_StillReadsAsASentence()
    {
        GitResultText.SshUnsupported(string.Empty).Should().NotBeNullOrWhiteSpace();
    }
}
