using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace TCV.Git.Tests;

/// <summary>
/// The places where a git action could lose work or leak it: removing a worktree that changed after
/// it was listed, creating a branch on a dirty tree, the diff an AI draft is built from, and the
/// failures the dialogs have to be able to put into the user's language.
/// </summary>
public class GitSafetyScenarioTests
{
    private readonly GitWriteService _write = new(NullLogger<GitWriteService>.Instance);
    private readonly GitWorktreeService _worktrees = new(NullLogger<GitWorktreeService>.Instance);
    private readonly GitStatusProvider _status = new(NullLogger<GitStatusProvider>.Instance);

    private async Task<(string Path, string Name)> AddWorktreeAsync(TempRepository repo, string branch)
    {
        var target = repo.SiblingPath(branch);
        var created = await _worktrees.AddAsync(new GitWorktreeRequest(
            repo.Path, branch, CreateBranch: true, BaseCommittish: "", TargetPath: target));
        created.Success.Should().BeTrue(created.Message);

        var listed = (await _worktrees.ListAsync(repo.Path)).Single(w => !w.IsMain);
        return (target, listed.Name);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A leaked temp directory is not worth failing an otherwise green test over.
        }
    }

    // ── Worktree removal re-reads what it is about to destroy ─────────────────

    [Fact]
    public async Task RemoveAsync_ChangesAppearedAfterTheListing_IsRefusedWithTheFreshCount()
    {
        using var repo = new TempRepository("wt-dirty");
        var (path, name) = await AddWorktreeAsync(repo, "agent-task");
        try
        {
            // Listed clean, then an agent wrote into it.
            File.WriteAllText(Path.Combine(path, "new-work.txt"), "unsaved\n");
            File.WriteAllText(Path.Combine(path, "README.md"), "edited\n");

            var result = await _worktrees.RemoveAsync(repo.Path, name, force: false, acceptedDirtyCount: 0);

            result.Success.Should().BeFalse();
            result.Kind.Should().Be(GitFailureKind.DirtyWorkingTree);
            result.Detail.Should().Be("2");
            File.Exists(Path.Combine(path, "new-work.txt")).Should().BeTrue("nothing may be deleted on a refusal");
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public async Task RemoveAsync_TheConfirmedChanges_AreRemoved()
    {
        using var repo = new TempRepository("wt-accept");
        var (path, name) = await AddWorktreeAsync(repo, "accepted");
        try
        {
            File.WriteAllText(Path.Combine(path, "README.md"), "edited\n");

            var result = await _worktrees.RemoveAsync(repo.Path, name, force: false, acceptedDirtyCount: 1);

            result.Success.Should().BeTrue(result.Message);
            Directory.Exists(path).Should().BeFalse();
        }
        finally
        {
            DeleteQuietly(path);
        }
    }

    [Fact]
    public void WorktreeInfo_UnknownDirtyCount_CountsAsDirty()
    {
        var info = new GitWorktreeInfo(
            "n", "p", "b", IsMain: false, IsCurrent: false, IsLocked: false, Missing: false,
            DirtyCount: GitWorktreeInfo.UnknownDirtyCount, AheadBy: 0, BehindBy: 0, HasUpstream: false);

        info.IsDirty.Should().BeTrue();
        info.StateLabel.Should().Be("?±");
    }

    [Fact]
    public async Task AddAsync_BranchHeldByTheMainCheckout_NamesWhereItIsCheckedOut()
    {
        using var repo = new TempRepository("wt-held");
        var target = repo.SiblingPath("held");

        var result = await _worktrees.AddAsync(new GitWorktreeRequest(
            repo.Path, repo.InitialBranch, CreateBranch: false, BaseCommittish: "", TargetPath: target));

        result.Success.Should().BeFalse();
        result.Kind.Should().Be(GitFailureKind.CheckedOutElsewhere);
        result.Detail.Should().BeEquivalentTo(Path.GetFullPath(repo.Path).TrimEnd('\\'));
    }

    // ── Branch creation carries uncommitted work, switching does not ──────────

    [Fact]
    public async Task CheckoutBranchAsync_CreateOnADirtyTree_CarriesTheChangesOntoTheNewBranch()
    {
        using var repo = new TempRepository("branch-dirty");
        var readme = repo.WriteFile("README.md", "work in progress\n");

        var result = await _write.CheckoutBranchAsync(repo.Path, "feature/wip", create: true);

        result.Success.Should().BeTrue(result.Message);
        File.ReadAllText(readme).Should().Be("work in progress\n");
        using var git = new Repository(repo.Path);
        git.Head.FriendlyName.Should().Be("feature/wip");
        git.RetrieveStatus("README.md").Should().Be(FileStatus.ModifiedInWorkdir);
    }

    // ── The AI draft's diff never carries untracked files ─────────────────────

    [Fact]
    public async Task GetDiffAsync_NothingStaged_LeavesUntrackedFilesOut()
    {
        using var repo = new TempRepository("diff-untracked");
        repo.WriteFile("README.md", "changed line\n");
        repo.WriteFile("secrets.local.yaml", "token: SUPER-SECRET\n");

        var diff = await _write.GetDiffAsync(repo.Path, stagedOnly: false);

        diff.Should().Contain("changed line");
        diff.Should().NotContain("SUPER-SECRET");
    }

    [Fact]
    public async Task GetDiffAsync_OnlyUntrackedFiles_IsEmpty()
    {
        using var repo = new TempRepository("diff-only-untracked");
        repo.WriteFile("notes.txt", "SUPER-SECRET\n");

        (await _write.GetDiffAsync(repo.Path, stagedOnly: false)).Should().BeEmpty();
    }

    // ── Ctrl+G on a folder ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetDiffVsHeadAsync_Folder_ShowsTheChangesBelowItOnly()
    {
        using var repo = new TempRepository("diff-folder");
        repo.WriteFile("src/a.txt", "a\n");
        repo.WriteFile("docs/b.txt", "b\n");
        using (var git = new Repository(repo.Path))
        {
            LibGit2Sharp.Commands.Stage(git, "*");
            git.Commit("add", TempRepository.Signature, TempRepository.Signature);
        }

        repo.WriteFile("src/a.txt", "a changed\n");
        repo.WriteFile("docs/b.txt", "b changed\n");

        var diff = await _status.GetDiffVsHeadAsync(Path.Combine(repo.Path, "src"));

        diff.Text.Should().Contain("a changed").And.NotContain("b changed");
    }

    [Fact]
    public async Task GetDiffVsHeadAsync_RepositoryRoot_ShowsTheWholeWorkingTree()
    {
        using var repo = new TempRepository("diff-root");
        repo.WriteFile("README.md", "root change\n");
        repo.WriteFile("sub/new.txt", "new file\n");

        var diff = await _status.GetDiffVsHeadAsync(repo.Path + Path.DirectorySeparatorChar);

        diff.Text.Should().Contain("root change").And.Contain("new file");
    }

    // ── Failures the dialogs localise ──────────────────────────────────────────

    [Fact]
    public async Task Failures_CarryAKindAndTheVariablePart()
    {
        using var repo = new TempRepository("kinds");

        (await _write.StashAsync(repo.Path, "x")).Kind.Should().Be(GitFailureKind.NothingToStash);

        var missing = await _write.CheckoutBranchAsync(repo.Path, "nope", create: false);
        missing.Kind.Should().Be(GitFailureKind.NoSuchBranch);
        missing.Detail.Should().Be("nope");

        var taken = await _write.CheckoutBranchAsync(repo.Path, repo.InitialBranch, create: true);
        taken.Kind.Should().Be(GitFailureKind.BranchExists);

        (await _write.CheckoutBranchAsync(repo.Path, "bad..name", create: true)).Kind
            .Should().Be(GitFailureKind.InvalidBranchName);
    }
}
