using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>
/// The write minimum against a real repository: staging, committing, branching and stashing all
/// have failure modes (a dirty tree, an absent identity, an empty index) that only a repository
/// produces, and each of them has to come back as a message rather than an exception.
/// </summary>
public class GitWriteServiceTests
{
    private readonly GitWriteService _git = new(NullLogger<GitWriteService>.Instance);

    [Fact]
    public async Task GetStatusAsync_CleanRepository_ReportsNothingToCommit()
    {
        using var repo = new TempRepository("w-clean");

        var status = await _git.GetStatusAsync(repo.Path);

        status.IsRepository.Should().BeTrue();
        status.Branch.Should().Be(repo.InitialBranch);
        status.Files.Should().BeEmpty();
        status.HasStagedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task GetStatusAsync_NotARepository_ReturnsEmpty()
    {
        var status = await _git.GetStatusAsync(Path.GetTempPath());

        status.IsRepository.Should().BeFalse();
    }

    [Fact]
    public async Task StageAsync_AbsolutePanelPath_LandsInTheIndex()
    {
        // Panel selections arrive as absolute Windows paths; the index speaks forward-slashed
        // relative ones, and passing the wrong shape stages nothing while reporting success.
        using var repo = new TempRepository("w-stage");
        var absolute = repo.WriteFile("src/new.txt");

        var result = await _git.StageAsync(repo.Path, new[] { absolute });

        result.Success.Should().BeTrue(result.Message);

        var status = await _git.GetStatusAsync(repo.Path);
        status.HasStagedChanges.Should().BeTrue();
        status.Staged.Should().ContainSingle(f => f.RelativePath == "src/new.txt");
    }

    [Fact]
    public async Task UnstageAsync_TakesItBackOutWithoutTouchingTheWorkingTree()
    {
        using var repo = new TempRepository("w-unstage");
        var absolute = repo.WriteFile("new.txt");
        await _git.StageAsync(repo.Path, new[] { absolute });

        var result = await _git.UnstageAsync(repo.Path, new[] { absolute });

        result.Success.Should().BeTrue(result.Message);
        File.Exists(absolute).Should().BeTrue();

        var status = await _git.GetStatusAsync(repo.Path);
        status.HasStagedChanges.Should().BeFalse();
        status.Unstaged.Should().ContainSingle(f => f.RelativePath == "new.txt");
    }

    [Fact]
    public async Task StageAsync_PathOutsideTheRepository_IsIgnored()
    {
        using var repo = new TempRepository("w-outside");

        var result = await _git.StageAsync(repo.Path, new[] { Path.Combine(Path.GetTempPath(), "elsewhere.txt") });

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Nothing to stage");
        result.Kind.Should().Be(GitFailureKind.NoPathsInRepository, "the refusal is shown in the user's language (6.65)");
    }

    [Fact]
    public async Task UnstageAndRestoreAsync_PathOutsideTheRepository_AreClassified()
    {
        using var repo = new TempRepository("w-outside-2");
        var outside = new[] { Path.Combine(Path.GetTempPath(), "elsewhere.txt") };

        (await _git.UnstageAsync(repo.Path, outside)).Kind.Should().Be(GitFailureKind.NoPathsInRepository);
        (await _git.RestoreAsync(repo.Path, outside)).Kind.Should().Be(GitFailureKind.NoPathsInRepository);
    }

    [Fact]
    public async Task DeleteBranchAsync_CheckedOutBranch_IsRefusedWithItsName()
    {
        using var repo = new TempRepository("w-del-current");

        var result = await _git.DeleteBranchAsync(repo.Path, repo.InitialBranch, force: true);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.CurrentBranch);
        result.Detail.Should().Be(repo.InitialBranch);
    }

    [Fact]
    public async Task CommitAsync_StagedContent_CreatesACommit()
    {
        using var temp = new TempRepository("w-commit");
        var absolute = temp.WriteFile("committed.txt");
        await _git.StageAsync(temp.Path, new[] { absolute });

        var result = await _git.CommitAsync(temp.Path, "add a file", stageAll: false);

        result.Success.Should().BeTrue(result.Message);

        using var repo = new Repository(temp.Path);
        repo.Head.Tip.Message.Should().Contain("add a file");
        repo.Commits.Count().Should().Be(2);
    }

    [Fact]
    public async Task CommitAsync_StageAll_PicksUpTrackedChangesButNotUntrackedOnes()
    {
        // Sweeping untracked files into a commit is how secrets get committed — "-a" must not.
        using var temp = new TempRepository("w-commit-all");
        temp.WriteFile("README.md", "changed\n");
        temp.WriteFile("secret.env", "TOKEN=1\n");

        var result = await _git.CommitAsync(temp.Path, "update readme", stageAll: true);

        result.Success.Should().BeTrue(result.Message);

        var status = await _git.GetStatusAsync(temp.Path);
        status.Unstaged.Should().ContainSingle(f => f.RelativePath == "secret.env");
        status.Files.Should().NotContain(f => f.RelativePath == "README.md");
    }

    [Fact]
    public async Task CommitAsync_EmptyMessage_IsRefused()
    {
        using var repo = new TempRepository("w-nomsg");

        var result = await _git.CommitAsync(repo.Path, "   ", stageAll: true);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("empty");
        result.Kind.Should().Be(GitFailureKind.EmptyCommitMessage);
    }

    [Fact]
    public async Task CommitAsync_NothingStaged_FailsWithoutThrowing()
    {
        using var repo = new TempRepository("w-empty");

        var result = await _git.CommitAsync(repo.Path, "nothing here", stageAll: false);

        result.Success.Should().BeFalse();
        result.Message.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ListBranchesAsync_PutsTheCurrentBranchFirst()
    {
        using var temp = new TempRepository("w-branches");
        using (var repo = new Repository(temp.Path))
        {
            repo.Branches.Add("aaa", repo.Head.Tip);
            repo.Branches.Add("zzz", repo.Head.Tip);
        }

        var branches = await _git.ListBranchesAsync(temp.Path);

        branches.Should().HaveCount(3);
        branches[0].Should().Be(temp.InitialBranch);
    }

    [Fact]
    public async Task CheckoutBranchAsync_Create_MakesAndSwitchesToTheBranch()
    {
        using var repo = new TempRepository("w-newbranch");

        var result = await _git.CheckoutBranchAsync(repo.Path, "feature/new", create: true);

        result.Success.Should().BeTrue(result.Message);
        GitHeadReader.ReadBranch(repo.Path).Should().Be("feature/new");
    }

    [Fact]
    public async Task CheckoutBranchAsync_DirtyTree_IsRefusedInsteadOfCarryingChangesOver()
    {
        // Switching, not creating: a new branch starts at HEAD and takes the changes along
        // (GitSafetyScenarioTests), but another branch would get them dumped on top of it.
        using var repo = new TempRepository("w-dirty-switch");
        using (var git = new LibGit2Sharp.Repository(repo.Path)) git.CreateBranch("feature/dirty");
        repo.WriteFile("README.md", "uncommitted\n");

        var result = await _git.CheckoutBranchAsync(repo.Path, "feature/dirty", create: false);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("uncommitted");
        GitHeadReader.ReadBranch(repo.Path).Should().Be(repo.InitialBranch);
    }

    [Fact]
    public async Task CheckoutBranchAsync_UnknownBranch_IsRefused()
    {
        using var repo = new TempRepository("w-nobranch");

        var result = await _git.CheckoutBranchAsync(repo.Path, "does-not-exist", create: false);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No such branch");
    }

    [Fact]
    public async Task CheckoutBranchAsync_InvalidName_IsRefused()
    {
        using var repo = new TempRepository("w-badbranch");

        var result = await _git.CheckoutBranchAsync(repo.Path, "bad name", create: true);

        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task StashAsync_ThenPop_RoundTripsTheWorkingTree()
    {
        using var repo = new TempRepository("w-stash");
        repo.WriteFile("README.md", "work in progress\n");
        repo.WriteFile("brand-new.txt");

        var stashed = await _git.StashAsync(repo.Path, "wip");
        stashed.Success.Should().BeTrue(stashed.Message);

        // Untracked files go into the stash too — a stash that leaves them behind does not
        // actually hand back a clean tree.
        File.Exists(Path.Combine(repo.Path, "brand-new.txt")).Should().BeFalse();
        File.ReadAllText(Path.Combine(repo.Path, "README.md")).Should().Be("hello\n");
        (await _git.StashCountAsync(repo.Path)).Should().Be(1);

        var popped = await _git.StashPopAsync(repo.Path);

        popped.Success.Should().BeTrue(popped.Message);
        File.ReadAllText(Path.Combine(repo.Path, "README.md")).Should().Be("work in progress\n");
        File.Exists(Path.Combine(repo.Path, "brand-new.txt")).Should().BeTrue();
        (await _git.StashCountAsync(repo.Path)).Should().Be(0);
    }

    [Fact]
    public async Task StashAsync_CleanTree_ReportsNothingToStash()
    {
        using var repo = new TempRepository("w-stash-clean");

        var result = await _git.StashAsync(repo.Path, "nothing");

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("Nothing to stash");
    }

    [Fact]
    public async Task StashPopAsync_EmptyStash_IsRefused()
    {
        using var repo = new TempRepository("w-stash-empty");

        var result = await _git.StashPopAsync(repo.Path);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No stash");
        result.Kind.Should().Be(GitFailureKind.NoStash);
    }

    [Fact]
    public async Task GetStatusAsync_FileStagedThenModified_AppearsOnBothSides()
    {
        // Exactly what git reports, and collapsing it into one row is how "I staged it, why isn't
        // it in the commit?" happens.
        using var repo = new TempRepository("w-both-sides");
        var absolute = repo.WriteFile("twice.txt", "first\n");
        await _git.StageAsync(repo.Path, new[] { absolute });
        File.WriteAllText(absolute, "second\n");

        var status = await _git.GetStatusAsync(repo.Path);

        status.Staged.Should().ContainSingle(f => f.RelativePath == "twice.txt");
        status.Unstaged.Should().ContainSingle(f => f.RelativePath == "twice.txt");
    }

    [Fact]
    public async Task WriteOperations_OutsideARepository_FailWithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var staged = await _git.StageAsync(dir, new[] { "x" });
            staged.Success.Should().BeFalse();
            staged.Kind.Should().Be(GitFailureKind.NotARepository);
            staged.Detail.Should().Be(dir);
            (await _git.CommitAsync(dir, "m", false)).Success.Should().BeFalse();
            (await _git.CheckoutBranchAsync(dir, "b", true)).Success.Should().BeFalse();
            (await _git.StashAsync(dir, "s")).Success.Should().BeFalse();
            (await _git.StashPopAsync(dir)).Success.Should().BeFalse();
            (await _git.ListBranchesAsync(dir)).Should().BeEmpty();
            (await _git.StashCountAsync(dir)).Should().Be(0);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
