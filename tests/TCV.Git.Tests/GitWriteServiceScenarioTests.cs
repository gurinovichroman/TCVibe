using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>
/// The scenarios the first round of tests left out: the destructive restore, the diff feeding the
/// commit draft, linked worktrees (where agents actually work), conflicts, detached HEAD, an
/// empty repository, awkward file names, and the branch guard rails.
/// </summary>
public class GitWriteServiceScenarioTests
{
    private readonly GitWriteService _git = new(NullLogger<GitWriteService>.Instance);
    private readonly GitWorktreeService _worktrees = new(NullLogger<GitWorktreeService>.Instance);

    private static string Read(TempRepository repo, string relative) =>
        File.ReadAllText(Path.Combine(repo.Path, relative));

    private static void CommitAll(TempRepository repo, string message)
    {
        using var raw = new Repository(repo.Path);
        Commands.Stage(raw, "*");
        raw.Commit(message, TempRepository.Signature, TempRepository.Signature);
    }

    // ── RestoreAsync (the destructive revert) ─────────────────────────────

    [Fact]
    public async Task RestoreAsync_ModifiedFile_GoesBackToTheCommittedContent()
    {
        using var repo = new TempRepository("s-restore");
        repo.WriteFile("README.md", "scribbled over\n");

        var result = await _git.RestoreAsync(repo.Path, new[] { Path.Combine(repo.Path, "README.md") });

        result.Success.Should().BeTrue(result.Message);
        Read(repo, "README.md").Should().Be("hello\n");
        (await _git.GetStatusAsync(repo.Path)).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreAsync_StagedAndModified_DropsBothSides()
    {
        using var repo = new TempRepository("s-restore-both");
        var file = repo.WriteFile("README.md", "staged version\n");
        await _git.StageAsync(repo.Path, new[] { file });
        File.WriteAllText(file, "and edited again\n");

        var result = await _git.RestoreAsync(repo.Path, new[] { file });

        result.Success.Should().BeTrue(result.Message);
        Read(repo, "README.md").Should().Be("hello\n");
        (await _git.GetStatusAsync(repo.Path)).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreAsync_FileDeletedFromDisk_ComesBack()
    {
        using var repo = new TempRepository("s-restore-deleted");
        File.Delete(Path.Combine(repo.Path, "README.md"));

        var result = await _git.RestoreAsync(repo.Path, new[] { "README.md" });

        result.Success.Should().BeTrue(result.Message);
        Read(repo, "README.md").Should().Be("hello\n");
    }

    [Fact]
    public async Task GetStatusAsync_StagedRename_CarriesTheOldPathSoARevertCanRestoreIt()
    {
        // An agent's «git mv»: the row names the new path, and restoring only that left the original
        // deleted. The old path travels with the row, and restoring both brings the file back.
        using var repo = new TempRepository("s-restore-rename");
        using (var raw = new Repository(repo.Path))
            Commands.Move(raw, "README.md", "RENAMED.md");

        var status = await _git.GetStatusAsync(repo.Path);
        var renamed = status.Files.Should()
            .ContainSingle(f => f.Status == GitFileStatus.Renamed && f.IsStaged).Subject;
        renamed.RelativePath.Should().Be("RENAMED.md");
        renamed.OldRelativePath.Should().Be("README.md");

        var result = await _git.RestoreAsync(repo.Path, new[] { renamed.RelativePath, renamed.OldRelativePath });

        result.Success.Should().BeTrue(result.Message);
        Read(repo, "README.md").Should().Be("hello\n");
    }

    [Fact]
    public async Task RestoreAsync_LeavesOtherModifiedFilesAlone()
    {
        using var repo = new TempRepository("s-restore-scope");
        repo.WriteFile("other.txt", "keep me\n");
        CommitAll(repo, "add other");
        repo.WriteFile("other.txt", "edited but not restored\n");
        repo.WriteFile("README.md", "restore me\n");

        await _git.RestoreAsync(repo.Path, new[] { "README.md" });

        Read(repo, "README.md").Should().Be("hello\n");
        Read(repo, "other.txt").Should().Be("edited but not restored\n");
    }

    [Fact]
    public async Task RestoreAsync_NoPaths_IsRefused()
    {
        using var repo = new TempRepository("s-restore-none");

        (await _git.RestoreAsync(repo.Path, Array.Empty<string>())).Success.Should().BeFalse();
    }

    [Fact]
    public async Task RestoreAsync_UnbornRepository_SaysThereIsNothingToRestoreFrom()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tcv_unborn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);
            File.WriteAllText(Path.Combine(path, "a.txt"), "x");

            var result = await _git.RestoreAsync(path, new[] { "a.txt" });

            result.Success.Should().BeFalse();
            File.Exists(Path.Combine(path, "a.txt")).Should().BeTrue("a refused restore must not delete anything");
        }
        finally
        {
            TempRepository.DeleteTree(path);
        }
    }

    // ── GetDiffAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiffAsync_UnstagedChange_ShowsUpUnlessStagedOnly()
    {
        using var repo = new TempRepository("s-diff");
        repo.WriteFile("README.md", "hello world\n");

        var all = await _git.GetDiffAsync(repo.Path, stagedOnly: false);
        var staged = await _git.GetDiffAsync(repo.Path, stagedOnly: true);

        all.Should().Contain("+hello world");
        staged.Should().BeEmpty("nothing is in the index yet");
    }

    [Fact]
    public async Task GetDiffAsync_StagedChange_ShowsInStagedOnly()
    {
        using var repo = new TempRepository("s-diff-staged");
        var file = repo.WriteFile("README.md", "hello staged\n");
        await _git.StageAsync(repo.Path, new[] { file });

        (await _git.GetDiffAsync(repo.Path, stagedOnly: true)).Should().Contain("+hello staged");
    }

    [Fact]
    public async Task GetDiffAsync_LongDiff_IsCutAtTheLimit()
    {
        using var repo = new TempRepository("s-diff-cut");
        repo.WriteFile("README.md", new string('x', 5_000) + "\n");

        (await _git.GetDiffAsync(repo.Path, stagedOnly: false, maxChars: 200)).Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task GetDiffAsync_OutsideARepository_IsEmpty()
    {
        (await _git.GetDiffAsync(Path.GetTempPath(), stagedOnly: false)).Should().BeEmpty();
    }

    // ── Linked worktrees: the place agents actually work ────────────────────

    private async Task<string> AddWorktreeAsync(TempRepository repo, string branch)
    {
        var target = repo.SiblingPath(branch);
        var added = await _worktrees.AddAsync(new GitWorktreeRequest(
            repo.Path, branch, CreateBranch: true, BaseCommittish: "", TargetPath: target));
        added.Success.Should().BeTrue(added.Message);
        return target;
    }

    [Fact]
    public async Task Stage_InsideALinkedWorktree_StagesThereAndNotInTheMainCheckout()
    {
        using var repo = new TempRepository("s-wt-stage");
        var wt = await AddWorktreeAsync(repo, "feature-a");
        var file = Path.Combine(wt, "added.txt");
        File.WriteAllText(file, "x\n");

        var result = await _git.StageAsync(wt, new[] { file });

        result.Success.Should().BeTrue(result.Message);
        (await _git.GetStatusAsync(wt)).Staged.Should().ContainSingle(f => f.RelativePath == "added.txt");
        (await _git.GetStatusAsync(repo.Path)).Files.Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_InsideALinkedWorktree_LandsOnThatWorktreesBranch()
    {
        using var repo = new TempRepository("s-wt-commit");
        var wt = await AddWorktreeAsync(repo, "feature-c");
        File.WriteAllText(Path.Combine(wt, "c.txt"), "c\n");
        await _git.StageAsync(wt, new[] { "c.txt" });

        var result = await _git.CommitAsync(wt, "commit in worktree", stageAll: false);

        result.Success.Should().BeTrue(result.Message);
        using var raw = new Repository(repo.Path);
        raw.Branches["feature-c"].Tip.MessageShort.Should().Be("commit in worktree");
        raw.Head.Tip.MessageShort.Should().Be("init", "the main checkout's branch did not move");
        // The identity is set only in the repository's shared .git/config (TempRepository): a worktree
        // must read it, not fall back to the machine's global name or fail without one (TRAP-90).
        raw.Branches["feature-c"].Tip.Author.Name.Should().Be(TempRepository.Signature.Name);
    }

    [Fact]
    public async Task Checkout_OfABranchAnotherWorktreeHolds_IsRefusedWithTheirPath()
    {
        using var repo = new TempRepository("s-wt-held");
        var wt = await AddWorktreeAsync(repo, "held-elsewhere");

        var fromMain = await _git.CheckoutBranchAsync(repo.Path, "held-elsewhere", create: false);

        fromMain.Success.Should().BeFalse();
        fromMain.Kind.Should().Be(GitFailureKind.CheckedOutElsewhere);
        fromMain.Detail.Should().Be(wt.TrimEnd('\\', '/'));
    }

    [Fact]
    public async Task Checkout_FromLinkedWorktreeToTheMainBranch_IsRefused()
    {
        using var repo = new TempRepository("s-wt-main-held");
        var wt = await AddWorktreeAsync(repo, "side");

        var result = await _git.CheckoutBranchAsync(wt, repo.InitialBranch, create: false);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.CheckedOutElsewhere);
    }

    [Fact]
    public async Task ListBranchDetailsAsync_FlagsCurrentAndHeldBranches()
    {
        using var repo = new TempRepository("s-branches");
        var wt = await AddWorktreeAsync(repo, "in-worktree");

        var branches = await _git.ListBranchDetailsAsync(repo.Path);

        branches[0].Should().Match<GitBranchInfo>(b => b.Name == repo.InitialBranch && b.IsCurrent);
        branches.Single(b => b.Name == "in-worktree").CheckedOutAt.Should().Be(wt.TrimEnd('\\', '/'));
        branches.Single(b => b.Name == "in-worktree").IsBlocked.Should().BeTrue();
    }

    // ── Dirty checkout and stash ─────────────────────────────────────────────

    [Fact]
    public async Task Checkout_DirtyTree_ReportsTheDirtyKindSoTheDialogCanOfferAStash()
    {
        using var repo = new TempRepository("s-dirty");
        using (var raw = new Repository(repo.Path)) raw.CreateBranch("elsewhere");
        repo.WriteFile("README.md", "dirty\n");

        var result = await _git.CheckoutBranchAsync(repo.Path, "elsewhere", create: false);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.DirtyWorkingTree);
    }

    [Fact]
    public async Task StashPopAsync_OntoConflictingChanges_KeepsTheStashEntry()
    {
        using var repo = new TempRepository("s-pop-conflict");
        repo.WriteFile("README.md", "stashed version\n");
        (await _git.StashAsync(repo.Path, "wip")).Success.Should().BeTrue();
        repo.WriteFile("README.md", "different local edit\n");

        var result = await _git.StashPopAsync(repo.Path);

        result.Success.Should().BeFalse();
        (await _git.StashCountAsync(repo.Path)).Should().Be(1, "a failed pop must not lose the work in the stash");
        Read(repo, "README.md").Should().Be("different local edit\n");
    }

    [Fact]
    public async Task GetStashInfoAsync_NamesTheTopEntry()
    {
        using var repo = new TempRepository("s-stash-info");
        repo.WriteFile("README.md", "wip\n");
        await _git.StashAsync(repo.Path, "my experiment");

        var info = await _git.GetStashInfoAsync(repo.Path);

        info.Count.Should().Be(1);
        info.TopMessage.Should().Contain("my experiment");
    }

    // ── Commit edge cases ─────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_NothingStaged_ReportsTheNothingToCommitKind()
    {
        using var repo = new TempRepository("s-empty-commit");
        repo.WriteFile("only-untracked.txt");

        var result = await _git.CommitAsync(repo.Path, "message", stageAll: true);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.NothingToCommit);
    }

    [Fact]
    public async Task CommitAsync_FirstCommitOfAnEmptyRepository_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tcv_unborn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            using (var raw = new Repository(Repository.Init(path)))
            {
                raw.Config.Set("user.name", "TCV Test");
                raw.Config.Set("user.email", "test@example.invalid");
            }

            File.WriteAllText(Path.Combine(path, "first.txt"), "1\n");
            (await _git.GetStatusAsync(path)).Files.Should().ContainSingle(f => f.Status == GitFileStatus.Untracked);
            await _git.StageAsync(path, new[] { "first.txt" });

            var result = await _git.CommitAsync(path, "first", stageAll: false);

            result.Success.Should().BeTrue(result.Message);
            using var check = new Repository(path);
            check.Head.Tip.MessageShort.Should().Be("first");
        }
        finally
        {
            TempRepository.DeleteTree(path);
        }
    }

    [Fact]
    public async Task CheckoutBranchAsync_CreateOnAnEmptyRepository_ExplainsInsteadOfThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tcv_unborn_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            Repository.Init(path);

            var result = await _git.CheckoutBranchAsync(path, "topic", create: true);

            result.Success.Should().BeFalse();
            result.Message.Should().Contain("no commits");
        }
        finally
        {
            TempRepository.DeleteTree(path);
        }
    }

    // ── Detached HEAD ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_DetachedHead_ReportsTheShortCommitId()
    {
        using var repo = new TempRepository("s-detached");
        string sha;
        using (var raw = new Repository(repo.Path))
        {
            sha = raw.Head.Tip.Sha;
            Commands.Checkout(raw, raw.Head.Tip);
        }

        var status = await _git.GetStatusAsync(repo.Path);

        status.Branch.Should().Be(sha[..7]);
        GitHeadReader.LooksLikeCommitId(status.Branch).Should().BeTrue();
    }

    [Fact]
    public async Task CommitAsync_OnDetachedHead_StillCommits()
    {
        using var repo = new TempRepository("s-detached-commit");
        using (var raw = new Repository(repo.Path)) Commands.Checkout(raw, raw.Head.Tip);
        var file = repo.WriteFile("d.txt");
        await _git.StageAsync(repo.Path, new[] { file });

        (await _git.CommitAsync(repo.Path, "on a detached head", false)).Success.Should().BeTrue();
    }

    // ── Conflicts ─────────────────────────────────────────────────────────────

    private static void MakeConflict(TempRepository repo)
    {
        using var raw = new Repository(repo.Path);
        var other = raw.CreateBranch("other");

        Commands.Checkout(raw, other);
        File.WriteAllText(Path.Combine(repo.Path, "README.md"), "from other\n");
        Commands.Stage(raw, "README.md");
        raw.Commit("other edit", TempRepository.Signature, TempRepository.Signature);

        Commands.Checkout(raw, raw.Branches[repo.InitialBranch]);
        File.WriteAllText(Path.Combine(repo.Path, "README.md"), "from main\n");
        Commands.Stage(raw, "README.md");
        raw.Commit("main edit", TempRepository.Signature, TempRepository.Signature);

        raw.Merge(raw.Branches["other"], TempRepository.Signature);
    }

    [Fact]
    public async Task GetStatusAsync_MergeConflict_ShowsTheFileAsUnmerged()
    {
        using var repo = new TempRepository("s-conflict");
        MakeConflict(repo);

        var status = await _git.GetStatusAsync(repo.Path);

        var conflict = status.Files.Should().ContainSingle(f => f.Status == GitFileStatus.Conflict).Subject;
        conflict.RelativePath.Should().Be("README.md");
        conflict.StatusLabel.Should().Be("U");
    }

    // ── Awkward file names ───────────────────────────────────────────────────

    [Theory]
    [InlineData("notes[1].md")]
    [InlineData("a b c.txt")]
    [InlineData("файл.txt")]
    [InlineData("#hash!.txt")]
    public async Task StageCommitRestore_FileNamesThatLookLikePatterns_AreTakenLiterally(string name)
    {
        using var repo = new TempRepository("s-names");
        var file = repo.WriteFile(name, "one\n");

        (await _git.StageAsync(repo.Path, new[] { file })).Success.Should().BeTrue();
        (await _git.GetStatusAsync(repo.Path)).Staged.Should().ContainSingle(f => f.RelativePath == name);

        (await _git.CommitAsync(repo.Path, "add " + name, false)).Success.Should().BeTrue();

        File.WriteAllText(file, "two\n");
        (await _git.RestoreAsync(repo.Path, new[] { file })).Success.Should().BeTrue();
        File.ReadAllText(file).Should().Be("one\n");
    }

    // ── Branch deletion ───────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteBranchAsync_MergedBranch_IsDeleted()
    {
        using var repo = new TempRepository("s-del");
        using (var raw = new Repository(repo.Path)) raw.CreateBranch("done");

        var result = await _git.DeleteBranchAsync(repo.Path, "done", force: false);

        result.Success.Should().BeTrue(result.Message);
        (await _git.ListBranchesAsync(repo.Path)).Should().NotContain("done");
    }

    [Fact]
    public async Task DeleteBranchAsync_CurrentBranch_IsRefused()
    {
        using var repo = new TempRepository("s-del-current");

        var result = await _git.DeleteBranchAsync(repo.Path, repo.InitialBranch, force: true);

        result.Success.Should().BeFalse();
        (await _git.ListBranchesAsync(repo.Path)).Should().Contain(repo.InitialBranch);
    }

    [Fact]
    public async Task DeleteBranchAsync_BranchWithUniqueCommits_NeedsForce()
    {
        using var repo = new TempRepository("s-del-unmerged");
        using (var raw = new Repository(repo.Path))
        {
            var topic = raw.CreateBranch("topic");
            Commands.Checkout(raw, topic);
            File.WriteAllText(Path.Combine(repo.Path, "t.txt"), "t\n");
            Commands.Stage(raw, "t.txt");
            raw.Commit("only on topic", TempRepository.Signature, TempRepository.Signature);
            Commands.Checkout(raw, raw.Branches[repo.InitialBranch]);
        }

        var gentle = await _git.DeleteBranchAsync(repo.Path, "topic", force: false);
        gentle.Success.Should().BeFalse();
        gentle.Kind.Should().Be(GitFailureKind.Unmerged);
        (await _git.ListBranchesAsync(repo.Path)).Should().Contain("topic");

        var forced = await _git.DeleteBranchAsync(repo.Path, "topic", force: true);
        forced.Success.Should().BeTrue(forced.Message);
    }

    [Fact]
    public async Task DeleteBranchAsync_BranchHeldByAnotherWorktree_IsRefused()
    {
        using var repo = new TempRepository("s-del-held");
        await AddWorktreeAsync(repo, "busy");

        var result = await _git.DeleteBranchAsync(repo.Path, "busy", force: true);

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.CheckedOutElsewhere);
    }
}
