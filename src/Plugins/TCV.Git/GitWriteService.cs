using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace TCV.Git;

/// <summary>
/// The write half of git: stage, unstage, commit, branch, stash. Deliberately the minimum that
/// keeps a review loop inside the file manager — the moment "what changed?" has to be answered in
/// another window, the panel stops being where the work happens.
/// <para>
/// Every call opens a repository, does its work and disposes it, on a thread-pool thread
/// (INV-06 / TRAP-06). Nothing here holds a handle between calls: a worktree with an open handle
/// cannot be removed, and this service runs alongside <see cref="GitWorktreeService"/>.
/// </para>
/// </summary>
public sealed class GitWriteService
{
    /// <summary>
    /// Paths are file names, not patterns: without this a file called <c>notes[1].md</c> is read as a
    /// glob that matches <c>notes1.md</c>, so staging it silently stages nothing. Unmatched paths
    /// are not an error — a file deleted from disk is still a valid thing to stage.
    /// </summary>
    private static readonly ExplicitPathsOptions LiteralPaths = new() { ShouldFailOnUnmatchedPath = false };

    private static readonly StageOptions LiteralStage = new() { ExplicitPathsOptions = LiteralPaths };

    private readonly ILogger<GitWriteService> _logger;

    public GitWriteService(ILogger<GitWriteService> logger) => _logger = logger;

    /// <summary>Reads what the repository containing <paramref name="anyPath"/> would commit.
    /// Returns <see cref="GitWorkingTreeStatus.Empty"/> outside a repository.</summary>
    public Task<GitWorkingTreeStatus> GetStatusAsync(string anyPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null) return GitWorkingTreeStatus.Empty;

            try
            {
                using var repo = new Repository(gitDir);
                ct.ThrowIfCancellationRequested();

                var root = NormalizeDirectory(repo.Info.WorkingDirectory);
                var files = new List<GitChangedFile>();

                foreach (var entry in repo.RetrieveStatus(new StatusOptions
                {
                    IncludeUntracked = true,
                    RecurseUntrackedDirs = true,
                    IncludeIgnored = false,
                }))
                {
                    files.AddRange(GitStatusMapper.ToChangedFiles(entry));
                }

                return new GitWorkingTreeStatus(
                    root,
                    BranchLabel(repo.Head),
                    files.OrderByDescending(f => f.IsStaged)
                         .ThenBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase)
                         .ToList());
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read git status at {Path}", anyPath);
                return GitWorkingTreeStatus.Empty;
            }
        }, ct);

    /// <summary>Stages the given paths (absolute or repo-relative). Directories stage recursively,
    /// as <c>git add</c> does.</summary>
    public Task<GitOperationResult> StageAsync(
        string anyPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, root) =>
        {
            var specs = ToPathSpecs(paths, root);
            if (specs.Count == 0) return GitOperationResult.Fail("Nothing to stage.", GitFailureKind.NoPathsInRepository);

            Commands.Stage(repo, specs, LiteralStage);
            return GitOperationResult.Ok($"{specs.Count}");
        }, ct);

    /// <summary>Takes the given paths back out of the index, leaving the working tree untouched.</summary>
    public Task<GitOperationResult> UnstageAsync(
        string anyPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, root) =>
        {
            var specs = ToPathSpecs(paths, root);
            if (specs.Count == 0) return GitOperationResult.Fail("Nothing to unstage.", GitFailureKind.NoPathsInRepository);

            Commands.Unstage(repo, specs, LiteralPaths);
            return GitOperationResult.Ok($"{specs.Count}");
        }, ct);

    /// <summary>
    /// Commits the index. With <paramref name="stageAll"/> the tracked modifications are staged
    /// first (<c>git commit -a</c>) — untracked files are still left alone, because sweeping them in
    /// is how secrets get committed.
    /// </summary>
    public Task<GitOperationResult> CommitAsync(
        string anyPath, string message, bool stageAll, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, _) =>
        {
            if (string.IsNullOrWhiteSpace(message))
                return GitOperationResult.Fail("Commit message is empty.", GitFailureKind.EmptyCommitMessage);

            // Identity comes from git's own config chain (local → global → xdg → system), so a
            // commit made here is attributed exactly as one made from the command line. Checked
            // before "-a" stages anything: a commit refused for a missing name must not leave the
            // index changed behind the dialog's back.
            var signature = BuildSignature(repo);
            if (signature is null)
                return GitOperationResult.Fail(
                    "git user.name / user.email are not configured.", GitFailureKind.NoIdentity);

            if (stageAll)
            {
                var tracked = repo
                    .RetrieveStatus(new StatusOptions { IncludeUntracked = false, IncludeIgnored = false })
                    .Where(e => e.State is not (FileStatus.Unaltered or FileStatus.Ignored))
                    .Select(e => e.FilePath)
                    .ToList();

                if (tracked.Count > 0) Commands.Stage(repo, tracked, LiteralStage);
            }

            // Checked here rather than left to libgit2's exception: an empty commit is the one
            // failure the dialog can prevent, and a stable Kind lets it say so in the user's language.
            if (!HasStagedChanges(repo))
                return GitOperationResult.Fail("Nothing to commit.", GitFailureKind.NothingToCommit);

            var commit = repo.Commit(message, signature, signature);
            _logger.LogInformation("Committed {Sha} on {Branch}", commit.Sha[..7], repo.Head.FriendlyName);
            return GitOperationResult.Ok(commit.Sha[..7]);
        }, ct);

    /// <summary>True when the index differs from HEAD (or, on an unborn branch, holds anything).</summary>
    private static bool HasStagedChanges(Repository repo) =>
        repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false, IncludeIgnored = false })
            .Any(e => GitStatusMapper.StagedStatus(e.State) is not null);

    /// <summary>
    /// Throws away the working-tree and index changes of the given <em>tracked</em> paths, putting them
    /// back to HEAD. This is the one-click revert behind the «Agent changes» dashboard (6.12): the
    /// point of a review surface is that a bad edit costs one keystroke to undo.
    /// <para>
    /// Untracked files are not this method's job and must not be passed here — there is nothing in HEAD
    /// to restore them to, so removing them is a file deletion and belongs in the operation queue with
    /// a confirmation of its own (INV-10 / INV-11).
    /// </para>
    /// </summary>
    public Task<GitOperationResult> RestoreAsync(
        string anyPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, root) =>
        {
            var specs = ToPathSpecs(paths, root);
            if (specs.Count == 0) return GitOperationResult.Fail("Nothing to restore.", GitFailureKind.NoPathsInRepository);

            if (repo.Head.Tip is null)
                return GitOperationResult.Fail("The repository has no commits to restore from.", GitFailureKind.NoCommits);

            // Index first, then working tree: a file that is both staged and modified needs both sides
            // reset, and unstaging afterwards would put the change straight back into the index.
            Commands.Unstage(repo, specs, LiteralPaths);
            repo.CheckoutPaths(
                repo.Head.Tip.Sha,
                specs,
                new CheckoutOptions { CheckoutModifiers = CheckoutModifiers.Force });

            _logger.LogInformation("Restored {Count} path(s) to HEAD in {Root}", specs.Count, root);
            return GitOperationResult.Ok($"{specs.Count}");
        }, ct);

    /// <summary>
    /// Unified diff of the working tree against HEAD, truncated to <paramref name="maxChars"/>.
    /// Feeds the AI commit-message draft (6.14), which needs to see the change rather than just the
    /// file list — a message written from file names alone says "update three files".
    /// </summary>
    /// <param name="stagedOnly">Compare HEAD to the index only, matching what a plain commit would take.
    /// Otherwise the tracked working-tree changes are included — never untracked files: they are not
    /// what <c>commit -a</c> takes, and a new file that is not gitignored yet is exactly where a secret
    /// sits before it would be sent off to a model.</param>
    public Task<string> GetDiffAsync(
        string anyPath, bool stagedOnly, int maxChars = 12_000, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null) return string.Empty;

            try
            {
                using var repo = new Repository(gitDir);
                ct.ThrowIfCancellationRequested();

                var tree = repo.Head.Tip?.Tree;
                if (stagedOnly)
                {
                    using var staged = repo.Diff.Compare<Patch>(tree, DiffTargets.Index);
                    return Truncate(staged.Content, maxChars);
                }

                // A working-directory diff always carries untracked files along (LibGit2Sharp sets
                // that flag itself), so the tracked paths are named explicitly instead.
                var tracked = TrackedChangedPaths(repo);
                if (tracked.Count == 0) return string.Empty;

                using var patch = repo.Diff.Compare<Patch>(
                    tree, DiffTargets.Index | DiffTargets.WorkingDirectory, tracked, LiteralPaths);
                return Truncate(patch.Content, maxChars);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The draft is a convenience; without a diff the prompt still has the file list.
                _logger.LogWarning(ex, "Failed to read the diff at {Path}", anyPath);
                return string.Empty;
            }
        }, ct);

    /// <summary>
    /// Lines added and removed in the working tree against HEAD, over <paramref name="relativePaths"/>
    /// only — the «+304 −65» of the agent strip. An untracked file counts as all added. Zeros outside a
    /// repository or when the paths are empty; never throws for a path that is gone.
    /// </summary>
    public Task<GitLineStats> GetLineStatsAsync(
        string anyPath, IReadOnlyList<string> relativePaths, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            if (relativePaths.Count == 0)
            {
                return GitLineStats.None;
            }

            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null)
            {
                return GitLineStats.None;
            }

            try
            {
                using var repo = new Repository(gitDir);
                ct.ThrowIfCancellationRequested();

                var paths = relativePaths.Select(p => p.Replace('\\', '/')).ToList();
                var stats = repo.Diff.Compare<PatchStats>(
                    repo.Head.Tip?.Tree,
                    DiffTargets.Index | DiffTargets.WorkingDirectory,
                    paths,
                    LiteralPaths,
                    new CompareOptions { IncludeUnmodified = false });
                return new GitLineStats(stats.TotalLinesAdded, stats.TotalLinesDeleted);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A number in a status strip: without it the strip still says what changed.
                _logger.LogWarning(ex, "Failed to count changed lines at {Path}", anyPath);
                return GitLineStats.None;
            }
        }, ct);

    private static string Truncate(string? content, int maxChars)
    {
        content ??= string.Empty;
        return content.Length > maxChars ? content[..maxChars] : content;
    }

    /// <summary>Changed paths git already tracks (both sides of a staged rename included).</summary>
    private static List<string> TrackedChangedPaths(Repository repo)
    {
        var paths = new List<string>();
        foreach (var entry in repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false, IncludeIgnored = false }))
        {
            if (entry.State is FileStatus.Unaltered or FileStatus.Ignored) continue;

            paths.Add(entry.FilePath);
            if (entry.HeadToIndexRenameDetails?.OldFilePath is { Length: > 0 } old) paths.Add(old);
        }

        return paths;
    }

    /// <summary>Local branches, current one first; used to fill the branch switcher.</summary>
    public Task<IReadOnlyList<string>> ListBranchesAsync(string anyPath, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null) return Array.Empty<string>();

            try
            {
                using var repo = new Repository(gitDir);
                return repo.Branches
                    .Where(b => !b.IsRemote)
                    .Select(b => b.FriendlyName)
                    .OrderByDescending(n => string.Equals(n, repo.Head.FriendlyName, StringComparison.Ordinal))
                    .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list branches at {Path}", anyPath);
                return Array.Empty<string>();
            }
        }, ct);

    /// <summary>Local branches with their checkout state; the current one first, then the ones
    /// another worktree holds are flagged so the dialog can grey them out instead of failing later.</summary>
    public Task<IReadOnlyList<GitBranchInfo>> ListBranchDetailsAsync(string anyPath, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GitBranchInfo>>(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null) return Array.Empty<GitBranchInfo>();

            try
            {
                using var repo = new Repository(gitDir);
                var owners = GitWorktreeService.MapBranchOwners(repo);
                var current = repo.Head.FriendlyName;

                return repo.Branches
                    .Where(b => !b.IsRemote)
                    .Select(b => new GitBranchInfo(
                        b.FriendlyName,
                        string.Equals(b.FriendlyName, current, StringComparison.Ordinal),
                        owners.GetValueOrDefault(b.FriendlyName)))
                    .OrderByDescending(b => b.IsCurrent)
                    .ThenBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list branches at {Path}", anyPath);
                return Array.Empty<GitBranchInfo>();
            }
        }, ct);

    /// <summary>
    /// Deletes a local branch. Refuses the branch in use anywhere (HEAD of this or another worktree)
    /// and, unless <paramref name="force"/>, a branch whose commits are not reachable from HEAD —
    /// that is the one case where deleting loses work, so the caller confirms it separately
    /// (INV-11). Never touches remotes.
    /// </summary>
    public Task<GitOperationResult> DeleteBranchAsync(
        string anyPath, string branch, bool force, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, _) =>
        {
            var target = repo.Branches[branch];
            if (target is null || target.IsRemote)
                return GitOperationResult.Fail($"No such branch: {branch}", GitFailureKind.NoSuchBranch, branch);

            if (target.IsCurrentRepositoryHead)
                return GitOperationResult.Fail(
                    $"Cannot delete the checked-out branch: {branch}", GitFailureKind.CurrentBranch, branch);

            if (GitWorktreeService.FindBranchOwner(repo, branch) is { } owner)
                return GitOperationResult.Fail(
                    $"Branch '{branch}' is checked out in another worktree: {owner}",
                    GitFailureKind.CheckedOutElsewhere, owner);

            if (!force && !IsMergedIntoHead(repo, target))
                return GitOperationResult.Fail(
                    $"Branch '{branch}' has commits that are not in the current branch.", GitFailureKind.Unmerged, branch);

            repo.Branches.Remove(target);
            _logger.LogInformation("Deleted branch {Branch} (forced: {Force})", branch, force);
            return GitOperationResult.Ok(branch);
        }, ct);

    private static bool IsMergedIntoHead(Repository repo, Branch branch)
    {
        var tip = branch.Tip;
        var head = repo.Head.Tip;
        if (tip is null) return true;      // an unborn branch holds nothing
        if (head is null) return false;

        return repo.ObjectDatabase.FindMergeBase(tip, head)?.Sha == tip.Sha;
    }

    /// <summary>
    /// Switches to <paramref name="branch"/>, creating it from HEAD when
    /// <paramref name="create"/> is set. Switching is refused while the tree is dirty: libgit2's
    /// default strategy would silently keep the changes on top of the other branch, which is not what
    /// "switch branch" means to anyone reading the panel afterwards. Creating is not: the new branch
    /// starts at HEAD, so nothing in the tree changes and the uncommitted work simply moves onto it —
    /// which is usually why the branch is being created (<c>git checkout -b</c>).
    /// </summary>
    public Task<GitOperationResult> CheckoutBranchAsync(
        string anyPath, string branch, bool create, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, _) =>
        {
            if (!GitRefName.IsValidBranch(branch))
                return GitOperationResult.Fail($"'{branch}' is not a valid branch name.", GitFailureKind.InvalidBranchName, branch);

            var existing = repo.Branches[branch];
            if (create && existing is not null)
                return GitOperationResult.Fail($"Branch already exists: {branch}", GitFailureKind.BranchExists, branch);
            if (!create && existing is null)
                return GitOperationResult.Fail($"No such branch: {branch}", GitFailureKind.NoSuchBranch, branch);

            if (!create && GitWorktreeService.FindBranchOwner(repo, branch) is { } owner)
                return GitOperationResult.Fail(
                    $"Branch '{branch}' is already checked out in another worktree: {owner}",
                    GitFailureKind.CheckedOutElsewhere, owner);

            if (!create
                && repo.RetrieveStatus(new StatusOptions { IncludeUntracked = false, IncludeIgnored = false })
                    .Any(e => e.State is not (FileStatus.Unaltered or FileStatus.Ignored)))
                return GitOperationResult.Fail(
                    "Working tree has uncommitted changes — commit or stash them first.",
                    GitFailureKind.DirtyWorkingTree);

            if (create && repo.Head.Tip is null)
                return GitOperationResult.Fail(
                    "The repository has no commits yet — commit first, then branch.", GitFailureKind.NoCommits);

            var target = create ? repo.Branches.Add(branch, repo.Head.Tip) : existing!;
            Commands.Checkout(repo, target);

            _logger.LogInformation("Checked out {Branch} at {Path}", branch, repo.Info.WorkingDirectory);
            return GitOperationResult.Ok(branch);
        }, ct);

    /// <summary>Stashes the working tree, untracked files included — a stash that leaves the new
    /// files behind does not actually give you a clean tree.</summary>
    public Task<GitOperationResult> StashAsync(
        string anyPath, string message, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, _) =>
        {
            var signature = BuildSignature(repo);
            if (signature is null)
                return GitOperationResult.Fail("git user.name / user.email are not configured.", GitFailureKind.NoIdentity);

            var stash = repo.Stashes.Add(
                signature,
                string.IsNullOrWhiteSpace(message) ? "TCVibe" : message,
                StashModifiers.IncludeUntracked);

            return stash is null
                ? GitOperationResult.Fail("Nothing to stash.", GitFailureKind.NothingToStash)
                : GitOperationResult.Ok(stash.Message ?? string.Empty);
        }, ct);

    /// <summary>Restores and drops the most recent stash. Conflicts leave the stash in place — the
    /// entry must survive a failed apply, or the work in it is simply gone.</summary>
    public Task<GitOperationResult> StashPopAsync(string anyPath, CancellationToken ct = default) =>
        MutateAsync(anyPath, (repo, _) =>
        {
            if (!repo.Stashes.Any()) return GitOperationResult.Fail("No stash entries.", GitFailureKind.NoStash);

            var status = repo.Stashes.Pop(0);
            return status switch
            {
                StashApplyStatus.Applied => GitOperationResult.Ok(),
                StashApplyStatus.Conflicts => GitOperationResult.Fail("Conflicts — the stash was kept.", GitFailureKind.Conflicts),
                StashApplyStatus.UncommittedChanges =>
                    GitOperationResult.Fail("Uncommitted changes would be overwritten — the stash was kept.", GitFailureKind.DirtyWorkingTree),
                // NotFound: the entry went between the check above and the pop (another git client).
                _ => GitOperationResult.Fail("Stash entry not found.", GitFailureKind.NoStash),
            };
        }, ct);

    /// <summary>Count and top entry of the stash. The stash is shared by every worktree of the
    /// repository, so "pop" in one checkout can apply work stashed from another — the message
    /// (<c>On branch: text</c>) is how the confirmation says so.</summary>
    public Task<GitStashInfo> GetStashInfoAsync(string anyPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null) return new GitStashInfo(0, string.Empty);

            try
            {
                using var repo = new Repository(gitDir);
                var entries = repo.Stashes.ToList();
                return new GitStashInfo(entries.Count, entries.FirstOrDefault()?.Message ?? string.Empty);
            }
            catch
            {
                return new GitStashInfo(0, string.Empty);
            }
        }, ct);

    /// <summary>Number of stash entries, for labelling the "pop" action honestly.</summary>
    public Task<int> StashCountAsync(string anyPath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null) return 0;

            try
            {
                using var repo = new Repository(gitDir);
                return repo.Stashes.Count();
            }
            catch
            {
                return 0;
            }
        }, ct);

    /// <summary>
    /// Shared shell for the mutations: resolve the repository, run the body off the UI thread, turn
    /// whatever libgit2 throws into a message the dialog can show rather than an unhandled crash.
    /// </summary>
    /// <summary>
    /// The identity a commit or stash is made with, from git's config chain (local → global → xdg →
    /// system). TRAP-90: LibGit2Sharp builds a repository's local level from <c>&lt;gitdir&gt;/config</c>,
    /// and in a linked worktree that is <c>.git/worktrees/&lt;name&gt;</c> — the shared <c>.git/config</c> is
    /// never read. An identity set only for the repository then vanished in every worktree (the commit
    /// was refused, or made under the global name), so a worktree reads the common config itself.
    /// </summary>
    private static Signature? BuildSignature(Repository repo)
    {
        var now = DateTimeOffset.Now;
        var commonConfig = CommonConfigPath(repo.Info.Path);
        if (commonConfig is null)
        {
            return repo.Config.BuildSignature(now);
        }

        using var config = Configuration.BuildFrom(commonConfig);
        return config.BuildSignature(now);
    }

    /// <summary>The shared <c>config</c> of a linked worktree's repository, or null for a main checkout.</summary>
    internal static string? CommonConfigPath(string gitDir)
    {
        var commonDirFile = Path.Combine(gitDir, "commondir");
        if (!File.Exists(commonDirFile))
        {
            return null;
        }

        // Written by git as a path relative to the worktree's git dir ("../.."), absolute by some tools.
        var commonDir = File.ReadAllText(commonDirFile).Trim();
        if (commonDir.Length == 0)
        {
            return null;
        }

        var config = Path.Combine(Path.GetFullPath(commonDir, gitDir), "config");
        return File.Exists(config) ? config : null;
    }

    private Task<GitOperationResult> MutateAsync(
        string anyPath, Func<Repository, string, GitOperationResult> body, CancellationToken ct) =>
        Task.Run(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(anyPath);
            if (gitDir is null)
                return GitOperationResult.Fail($"Not a git repository: {anyPath}", GitFailureKind.NotARepository, anyPath);

            try
            {
                using var repo = new Repository(gitDir);
                ct.ThrowIfCancellationRequested();
                return body(repo, NormalizeDirectory(repo.Info.WorkingDirectory));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (CheckoutConflictException ex)
            {
                // Untracked files in the way of a checkout: the dirty check ignores them on purpose
                // (they survive a switch), so this is where they surface.
                _logger.LogInformation(ex, "Checkout blocked by files in the way at {Path}", anyPath);
                return GitOperationResult.Fail(ex.Message, GitFailureKind.CheckoutConflict);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Git write failed at {Path}", anyPath);
                return GitErrors.FromException(ex, anyPath);
            }
        }, ct);

    /// <summary>
    /// Normalises panel paths into repo-relative path specs. Panel selections arrive as absolute
    /// Windows paths while the index speaks forward-slashed relative ones, and passing the wrong
    /// shape stages nothing while reporting success.
    /// </summary>
    private static List<string> ToPathSpecs(IReadOnlyList<string>? paths, string repositoryRoot)
    {
        var specs = new List<string>();
        if (paths is null) return specs;

        foreach (var raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = raw.Trim();
            if (Path.IsPathRooted(path) && repositoryRoot.Length > 0)
            {
                try
                {
                    path = Path.GetRelativePath(repositoryRoot, path);
                }
                catch
                {
                    continue;   // another drive: not in this repository at all
                }
            }

            path = path.Replace('\\', '/').TrimStart('/');
            if (path.Length == 0 || path.StartsWith("../", StringComparison.Ordinal)) continue;

            specs.Add(path);
        }

        return specs;
    }

    private static string BranchLabel(Branch head)
    {
        if (head.FriendlyName is { Length: > 0 } friendly && !friendly.StartsWith('('))
            return friendly;

        var sha = head.Tip?.Sha;
        return sha is { Length: >= 7 } ? sha[..7] : string.Empty;
    }

    private static string NormalizeDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path) ? string.Empty : Path.GetFullPath(path).TrimEnd('\\', '/');
}
