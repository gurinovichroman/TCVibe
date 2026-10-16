using FluentAssertions;
using LibGit2Sharp;

namespace TCV.Git.Tests;

public class GitHeadReaderTests
{
    [Fact]
    public void ReadBranch_InsideRepository_ReturnsTheCheckedOutBranch()
    {
        using var repo = new TempRepository("head");

        GitHeadReader.ReadBranch(repo.Path).Should().Be(repo.InitialBranch);
    }

    [Fact]
    public void ReadBranch_FromASubdirectory_WalksUpToTheRepository()
    {
        using var repo = new TempRepository("head-sub");
        var nested = Path.Combine(repo.Path, "src", "deep");
        Directory.CreateDirectory(nested);

        GitHeadReader.ReadBranch(nested).Should().Be(repo.InitialBranch);
    }

    [Fact]
    public void ReadBranch_AfterCheckout_FollowsHead()
    {
        using var temp = new TempRepository("head-switch");
        using (var repo = new Repository(temp.Path))
        {
            var branch = repo.Branches.Add("feature/x", repo.Head.Tip);
            Commands.Checkout(repo, branch);
        }

        GitHeadReader.ReadBranch(temp.Path).Should().Be("feature/x");
    }

    [Fact]
    public void ReadBranch_DetachedHead_ReturnsTheShortCommitId()
    {
        using var temp = new TempRepository("head-detached");
        string sha;
        using (var repo = new Repository(temp.Path))
        {
            sha = repo.Head.Tip.Sha;
            Commands.Checkout(repo, repo.Head.Tip);
        }

        GitHeadReader.ReadBranch(temp.Path).Should().Be(sha[..7]);
    }

    [Fact]
    public void ReadBranch_InsideALinkedWorktree_ReadsThatWorktreesHead()
    {
        // The reason this class is not a two-liner: inside a worktree ".git" is a file pointing at
        // <main>/.git/worktrees/<name>, and that directory holds the worktree's own HEAD.
        using var temp = new TempRepository("head-wt");
        var worktreePath = temp.SiblingPath("wt");

        var service = new GitWorktreeService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitWorktreeService>.Instance);
        service.AddAsync(new GitWorktreeRequest(
            temp.Path, "feature/wt", CreateBranch: true, BaseCommittish: "", TargetPath: worktreePath))
            .GetAwaiter().GetResult();

        GitHeadReader.ReadBranch(worktreePath).Should().Be("feature/wt");
        GitHeadReader.ReadBranch(temp.Path).Should().Be(temp.InitialBranch);
    }

    [Fact]
    public void FindGitDirectory_InsideALinkedWorktree_ResolvesTheGitFile()
    {
        using var temp = new TempRepository("head-gitfile");
        var worktreePath = temp.SiblingPath("wt2");

        var service = new GitWorktreeService(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<GitWorktreeService>.Instance);
        service.AddAsync(new GitWorktreeRequest(
            temp.Path, "wt2", CreateBranch: true, BaseCommittish: "", TargetPath: worktreePath))
            .GetAwaiter().GetResult();

        var gitDir = GitHeadReader.FindGitDirectory(worktreePath);

        gitDir.Should().NotBeNull();
        Directory.Exists(gitDir).Should().BeTrue();
        gitDir!.Replace('\\', '/').Should().Contain("/worktrees/");
    }

    [Fact]
    public void ReadBranch_PlainDirectory_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            GitHeadReader.ReadBranch(dir).Should().BeEmpty();
            GitHeadReader.FindGitDirectory(dir).Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReadBranch_NoPath_ReturnsEmpty(string? path)
    {
        GitHeadReader.ReadBranch(path).Should().BeEmpty();
    }

    [Fact]
    public void ReadBranch_NonExistentPath_DoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "nope");

        GitHeadReader.ReadBranch(missing).Should().BeEmpty();
    }
}
