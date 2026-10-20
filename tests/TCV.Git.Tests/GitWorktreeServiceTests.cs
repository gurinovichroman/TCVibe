using System.Diagnostics;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>
/// Exercises the worktree service against a real repository. The list/add/remove trio is a thin
/// wrapper over libgit2, and the parts most likely to be wrong — that removal has to delete the
/// directory before pruning, that a branch cannot be checked out twice — only show up against git.
/// </summary>
public class GitWorktreeServiceTests
{
    private readonly GitWorktreeService _service = new(NullLogger<GitWorktreeService>.Instance);

    [Fact]
    public async Task ListAsync_FreshRepository_ReturnsOnlyTheMainCheckout()
    {
        using var repo = new TempRepository("wt-list");

        var list = await _service.ListAsync(repo.Path);

        list.Should().ContainSingle();
        list[0].IsMain.Should().BeTrue();
        list[0].IsCurrent.Should().BeTrue();
        list[0].Branch.Should().Be(repo.InitialBranch);
        list[0].IsDirty.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_NotARepository_ReturnsEmpty()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            (await _service.ListAsync(dir)).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_CountsUncommittedChanges()
    {
        using var repo = new TempRepository("wt-dirty");
        repo.WriteFile("new-file.txt");

        var list = await _service.ListAsync(repo.Path);

        list[0].DirtyCount.Should().Be(1);
        list[0].StateLabel.Should().Be("1±");
    }

    [Fact]
    public async Task AddAsync_CreatesTheBranchAndChecksItOutInTheNewDirectory()
    {
        using var repo = new TempRepository("wt-add");
        var target = repo.SiblingPath("feature");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "feature/login", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeTrue(result.Message);
        Directory.Exists(target).Should().BeTrue();
        File.Exists(Path.Combine(target, "README.md")).Should().BeTrue();
        GitHeadReader.ReadBranch(target).Should().Be("feature/login");
    }

    [Fact]
    public async Task AddAsync_WorktreeRootDoesNotExistYet_CreatesIt()
    {
        // The default root, <repo>-worktrees, is not there before the first worktree; libgit2 only
        // creates the last path level.
        using var repo = new TempRepository("wt-noroot");
        var target = Path.Combine(repo.SiblingPath("worktrees"), "task");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "task", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeTrue(result.Message);
        File.Exists(Path.Combine(target, "README.md")).Should().BeTrue();
        GitHeadReader.ReadBranch(target).Should().Be("task");
    }

    [Fact]
    public async Task AddAsync_ThenList_ShowsBothCheckouts()
    {
        using var repo = new TempRepository("wt-both");
        var target = repo.SiblingPath("second");

        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "second", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        var list = await _service.ListAsync(repo.Path);

        list.Should().HaveCount(2);
        list.Should().ContainSingle(w => w.IsMain);
        list.Should().ContainSingle(w => w.Branch == "second" && !w.IsMain);
    }

    [Fact]
    public async Task ListAsync_CalledFromInsideALinkedWorktree_StillSeesEveryCheckout()
    {
        // Worktrees are only enumerable through the main repository; a linked worktree's own git
        // directory lists nothing, which is why ResolveMainGitDirectory exists.
        using var repo = new TempRepository("wt-from-linked");
        var target = repo.SiblingPath("linked");

        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "linked", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        var list = await _service.ListAsync(target);

        list.Should().HaveCount(2);
        list.Should().ContainSingle(w => w.IsCurrent && w.Branch == "linked");
    }

    [Fact]
    public async Task AddAsync_FolderNamedAfterTheBranch_StillCreatesAUsableWorktree()
    {
        // The name collision LibGit2Sharp handles by taking a different code path that ignores the
        // target directory entirely — and "branch scratch → folder scratch" is the common case.
        using var repo = new TempRepository("wt-samename");
        var target = repo.SiblingPath("scratch");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "scratch", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeTrue(result.Message);
        Directory.Exists(target).Should().BeTrue();
        File.Exists(Path.Combine(target, "README.md")).Should().BeTrue();
        GitHeadReader.ReadBranch(target).Should().Be("scratch");
    }

    [Fact]
    public async Task AddAsync_LeavesNoPlaceholderBranchBehind()
    {
        // Add creates a branch named after the worktree as a side effect; left alone it would show
        // up in every branch list from then on.
        using var temp = new TempRepository("wt-nojunk");

        await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "feature/login", CreateBranch: true, BaseCommittish: "",
            TargetPath: temp.SiblingPath("feature-login")));

        using var repo = new Repository(temp.Path);
        var branches = repo.Branches.Select(b => b.FriendlyName).ToList();

        branches.Should().BeEquivalentTo(new[] { temp.InitialBranch, "feature/login" });
    }

    [Fact]
    public async Task AddAsync_BaseCommittish_BranchesFromThere()
    {
        using var temp = new TempRepository("wt-base");
        string firstCommit;
        using (var repo = new Repository(temp.Path))
        {
            firstCommit = repo.Head.Tip.Sha;
            temp.WriteFile("second.txt");
            Commands.Stage(repo, "second.txt");
            repo.Commit("second", TempRepository.Signature, TempRepository.Signature);
        }

        var target = temp.SiblingPath("from-base");
        var result = await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "from-base", CreateBranch: true, BaseCommittish: firstCommit, TargetPath: target));

        result.Success.Should().BeTrue(result.Message);
        File.Exists(Path.Combine(target, "second.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_FailingAfterCreatingTheBranch_RollsItBack()
    {
        // Otherwise the obvious next move — fix the path, press Create again — fails with "branch
        // already exists" and points at the wrong problem.
        using var temp = new TempRepository("wt-rollback");

        // A path Windows cannot name gets past our own checks and fails inside libgit2 — after the
        // branch has been created.
        var result = await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "rolled-back", CreateBranch: true, BaseCommittish: "",
            TargetPath: temp.SiblingPath("bad|name")));

        result.Success.Should().BeFalse();

        using var repo = new Repository(temp.Path);
        repo.Branches["rolled-back"].Should().BeNull();
    }

    [Fact]
    public async Task AddAsync_UnresolvableBase_IsRefused()
    {
        using var repo = new TempRepository("wt-badbase");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "nowhere", CreateBranch: true, BaseCommittish: "no-such-ref",
            TargetPath: repo.SiblingPath("nowhere")));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("start point");
    }

    [Fact]
    public async Task AddAsync_ExistingBranch_ChecksItOutWithoutCreating()
    {
        using var temp = new TempRepository("wt-existing");
        using (var repo = new Repository(temp.Path))
            repo.Branches.Add("ready", repo.Head.Tip);

        var target = temp.SiblingPath("ready");
        var result = await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "ready", CreateBranch: false, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeTrue(result.Message);
        GitHeadReader.ReadBranch(target).Should().Be("ready");
    }

    [Fact]
    public async Task AddAsync_BranchAlreadyCheckedOut_IsRefusedWithAReadableReason()
    {
        using var repo = new TempRepository("wt-taken");
        var target = repo.SiblingPath("taken");

        // The initial branch is checked out in the main worktree; git allows one worktree per branch.
        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, repo.InitialBranch, CreateBranch: false, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("already checked out");
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_ExistingBranchRequestedAsNew_IsRefused()
    {
        using var temp = new TempRepository("wt-dup");
        using (var repo = new Repository(temp.Path))
            repo.Branches.Add("dup", repo.Head.Tip);

        var result = await _service.AddAsync(new GitWorktreeRequest(
            temp.Path, "dup", CreateBranch: true, BaseCommittish: "", TargetPath: temp.SiblingPath("dup")));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("already exists");
    }

    [Fact]
    public async Task AddAsync_InvalidBranchName_IsRefusedBeforeTouchingTheDisk()
    {
        using var repo = new TempRepository("wt-badname");
        var target = repo.SiblingPath("bad");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "bad name", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeFalse();
        Directory.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_NonEmptyTargetDirectory_IsRefused()
    {
        using var repo = new TempRepository("wt-occupied");
        var target = repo.SiblingPath("occupied");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "someone-elses.txt"), "x");

        var result = await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "occupied", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("not empty");
        File.Exists(Path.Combine(target, "someone-elses.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task AddAsync_NotARepository_IsRefused()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var result = await _service.AddAsync(new GitWorktreeRequest(
                dir, "x", CreateBranch: true, BaseCommittish: "", TargetPath: Path.Combine(dir, "wt")));

            result.Success.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheDirectoryAndDropsItFromTheList()
    {
        // Removal prunes git's record first and then deletes the checkout (the record is what knows
        // which folder to delete, and it disappears from the lookup once the folder is gone).
        using var repo = new TempRepository("wt-remove");
        var target = repo.SiblingPath("scratch");

        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "scratch", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        // The dashboard removes by the name git knows the worktree under, which is not always the
        // branch — see UniqueWorktreeName.
        var listed = (await _service.ListAsync(repo.Path)).Single(w => !w.IsMain);
        var result = await _service.RemoveAsync(repo.Path, listed.Name, force: false);

        result.Success.Should().BeTrue(result.Message);
        Directory.Exists(target).Should().BeFalse();
        (await _service.ListAsync(repo.Path)).Should().ContainSingle(w => w.IsMain);
    }

    [Fact]
    public async Task RemoveAsync_CheckoutHoldingAJunction_DeletesItAndLeavesWhatTheJunctionPointsTo()
    {
        // pnpm fills node_modules with junctions into its store. libgit2's prune removes the checkout
        // and copes with them; whatever it leaves behind goes through DirectoryTree, which never enters
        // a junction (TRAP-50). Either way the folder goes and the store stays.
        using var repo = new TempRepository("wt-junction");
        repo.WriteFile(".gitignore", "node_modules/\n");
        using (var git = new Repository(repo.Path))
        {
            Commands.Stage(git, ".gitignore");
            git.Commit("ignore node_modules", TempRepository.Signature, TempRepository.Signature);
        }

        var target = repo.SiblingPath("scratch");
        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "scratch", CreateBranch: true, BaseCommittish: "", TargetPath: target));
        var store = repo.SiblingPath("store");
        Directory.CreateDirectory(store);
        File.WriteAllText(Path.Combine(store, "keep.txt"), "k");
        Directory.CreateDirectory(Path.Combine(target, "node_modules"));
        if (!TryCreateJunction(Path.Combine(target, "node_modules", "pkg"), store))
        {
            return;   // no mklink on this machine
        }

        var listed = (await _service.ListAsync(repo.Path)).Single(w => !w.IsMain);
        var result = await _service.RemoveAsync(repo.Path, listed.Name, force: false);

        result.Success.Should().BeTrue(result.Message);
        Directory.Exists(target).Should().BeFalse(result.Message);
        File.Exists(Path.Combine(store, "keep.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task RemoveAsync_FolderHeldOpen_IsRemovedFromGitAndNamesTheFolderLeftBehind()
    {
        // An editor or a terminal holding a file in the checkout stops the folder from going, but
        // git has forgotten the worktree by then: that is a removal with a leftover, not a failure.
        using var repo = new TempRepository("wt-held");
        var target = repo.SiblingPath("scratch");
        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "scratch", CreateBranch: true, BaseCommittish: "", TargetPath: target));
        var listed = (await _service.ListAsync(repo.Path)).Single(w => !w.IsMain);

        GitOperationResult result;
        using (new FileStream(Path.Combine(target, "held.txt"), FileMode.Create, FileAccess.Write, FileShare.None))
        {
            result = await _service.RemoveAsync(repo.Path, listed.Name, force: false, acceptedDirtyCount: int.MaxValue);
        }

        result.Success.Should().BeTrue(result.Message);
        result.Kind.Should().Be(GitFailureKind.DirectoryNotDeleted);
        result.Detail.Should().Be(listed.Path);
        result.Message.Should().NotBeEmpty("the reason is the only clue to what holds the folder");
        (await _service.ListAsync(repo.Path)).Should().ContainSingle(w => w.IsMain);
    }

    [Fact]
    public async Task RemoveAsync_UnknownWorktree_IsRefused()
    {
        using var repo = new TempRepository("wt-unknown");

        var result = await _service.RemoveAsync(repo.Path, "never-existed", force: false);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("No such worktree");
        result.Kind.Should().Be(GitFailureKind.NoSuchWorktree);
        result.Detail.Should().Be("never-existed");
    }

    [Fact]
    public async Task ListAsync_WorktreeDirectoryDeletedByHand_IsShownAsMissing()
    {
        // libgit2 stops listing a worktree whose folder is gone, but its record stays in
        // .git/worktrees. The dashboard must show it, or nobody can ever prune it.
        using var repo = new TempRepository("wt-missing");
        var target = repo.SiblingPath("ghost");

        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "ghost", CreateBranch: true, BaseCommittish: "", TargetPath: target));
        TempRepository.DeleteTree(target);

        var list = await _service.ListAsync(repo.Path);

        list.Should().HaveCount(2);
        var ghost = list.Single(w => !w.IsMain);
        ghost.Missing.Should().BeTrue();
        ghost.State.Should().Be(GitWorktreeState.Missing);
        ghost.Path.Should().EndWith("ghost", "the row still says where the folder used to be");
    }

    [Fact]
    public async Task RemoveAsync_StaleRecord_PrunesItAndTheRepositoryStaysUsable()
    {
        using var repo = new TempRepository("wt-prune");
        var target = repo.SiblingPath("stale");
        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "stale", CreateBranch: true, BaseCommittish: "", TargetPath: target));
        TempRepository.DeleteTree(target);
        var ghost = (await _service.ListAsync(repo.Path)).Single(w => w.Missing);

        var result = await _service.RemoveAsync(repo.Path, ghost.Name, force: false);

        result.Success.Should().BeTrue(result.Message);
        (await _service.ListAsync(repo.Path)).Should().ContainSingle(w => w.IsMain);
        Directory.Exists(Path.Combine(repo.Path, ".git", "worktrees", ghost.Name)).Should().BeFalse();
    }

    [Fact]
    public async Task AddAsync_AfterTheFolderWasDeletedByHand_CanReuseTheSameBranchAndPath()
    {
        // The everyday accident: the worktree folder is deleted in the panel, then the same task is
        // started again. The dangling record must not block the name or the branch.
        using var repo = new TempRepository("wt-reuse");
        var target = repo.SiblingPath("again");
        var request = new GitWorktreeRequest(
            repo.Path, "again", CreateBranch: true, BaseCommittish: "", TargetPath: target);
        await _service.AddAsync(request);
        TempRepository.DeleteTree(target);

        var second = await _service.AddAsync(request with { CreateBranch = false });

        second.Success.Should().BeTrue(second.Message);
        Directory.Exists(target).Should().BeTrue();
        (await _service.ListAsync(repo.Path)).Count(w => !w.IsMain).Should().Be(1);
    }

    [Theory]
    [InlineData(true, false, 0, "missing")]
    [InlineData(false, true, 0, "locked")]
    [InlineData(false, false, 3, "3±")]
    [InlineData(false, false, 0, "clean")]
    public void StateLabel_ReportsTheWorstThingThatIsTrue(
        bool missing, bool locked, int dirty, string expected)
    {
        var info = new GitWorktreeInfo(
            "n", "p", "b", IsMain: false, IsCurrent: false, IsLocked: locked, Missing: missing,
            DirtyCount: dirty, AheadBy: 0, BehindBy: 0, HasUpstream: false);

        info.StateLabel.Should().Be(expected);
    }

    [Fact]
    public void ResolveMainGitDirectory_OutsideAnyRepository_ReturnsNull()
    {
        GitWorktreeService.ResolveMainGitDirectory(Path.GetTempPath()).Should().BeNull();
    }

    [Fact]
    public async Task ResolveMainGitDirectory_InsideALinkedWorktree_PointsAtTheMainRepository()
    {
        using var repo = new TempRepository("wt-resolve");
        var target = repo.SiblingPath("linked");

        await _service.AddAsync(new GitWorktreeRequest(
            repo.Path, "linked", CreateBranch: true, BaseCommittish: "", TargetPath: target));

        GitWorktreeService.ResolveMainGitDirectory(target)
            .Should().Be(Path.GetFullPath(Path.Combine(repo.Path, ".git")));
    }

    [Fact]
    public void ResolveMainGitDirectory_RepositoryLivingUnderAFolderCalledWorktrees_IsNotTruncated()
    {
        // Searching the whole path for "worktrees" cut D:\worktrees\myrepo\.git down to "D:" and
        // every worktree operation in such a repository failed with nothing to point at.
        var root = Path.Combine(Path.GetTempPath(), $"tcv_wtname_{Guid.NewGuid():N}");
        var repoPath = Path.Combine(root, "worktrees", "myrepo");
        Directory.CreateDirectory(repoPath);
        try
        {
            Repository.Init(repoPath);

            GitWorktreeService.ResolveMainGitDirectory(repoPath)
                .Should().Be(Path.GetFullPath(Path.Combine(repoPath, ".git")));
        }
        finally
        {
            TempRepository.DeleteTree(root);
        }
    }

    [Theory]
    [InlineData(0, 0, true, "=")]
    [InlineData(2, 0, true, "↑2")]
    [InlineData(0, 3, true, "↓3")]
    [InlineData(2, 3, true, "↑2 ↓3")]
    [InlineData(2, 3, false, "")]
    public void AheadBehindLabel_SaysNothingWithoutAnUpstream(
        int ahead, int behind, bool hasUpstream, string expected)
    {
        var info = new GitWorktreeInfo(
            "n", "p", "b", IsMain: false, IsCurrent: false, IsLocked: false, Missing: false,
            DirtyCount: 0, AheadBy: ahead, BehindBy: behind, HasUpstream: hasUpstream);

        info.AheadBehindLabel.Should().Be(expected);
    }

    /// <summary>A junction needs no privilege, unlike a symlink. False when even that is unavailable.</summary>
    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
            {
                return false;
            }

            process.WaitForExit(10_000);

            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch
        {
            return false;
        }
    }
}
