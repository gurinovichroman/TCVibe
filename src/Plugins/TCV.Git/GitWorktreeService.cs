using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;
using TCV.Core.Domain;

namespace TCV.Git;

/// <summary>What "make me a worktree for this task" needs to know. Assembled by the dialog so the
/// service stays free of UI defaults.</summary>
/// <param name="AnyPathInRepository">Any directory inside the repository the worktree branches off.</param>
/// <param name="Branch">Branch to check out in the new worktree.</param>
/// <param name="CreateBranch">True to create <paramref name="Branch"/>; false to reuse an existing one.</param>
/// <param name="BaseCommittish">Branch/commit the new branch starts from; empty means current HEAD.</param>
/// <param name="TargetPath">Absolute directory to create. Must not exist, or must be empty.</param>
public sealed record GitWorktreeRequest(
    string AnyPathInRepository,
    string Branch,
    bool CreateBranch,
    string BaseCommittish,
    string TargetPath);

/// <summary>
/// Lists, creates and removes git worktrees — the load-bearing piece under running several agents
/// at once, because a worktree is what lets two branches be checked out (and edited) side by side
/// instead of serialised through one working directory.
/// <para>
/// LibGit2Sharp is synchronous native code, so every entry point hops to a thread pool thread
/// (INV-06 / TRAP-06) and repository handles never outlive the call that opened them: git refuses
/// to remove a worktree anything still holds open.
/// </para>
/// </summary>
public sealed class GitWorktreeService
{
    private readonly ILogger<GitWorktreeService> _logger;

    public GitWorktreeService(ILogger<GitWorktreeService> logger) => _logger = logger;

    /// <summary>
    /// Every worktree of the repository containing <paramref name="anyPathInRepository"/>, main
    /// checkout first. Returns an empty list when the path is not in a repository at all.
    /// </summary>
    public Task<IReadOnlyList<GitWorktreeInfo>> ListAsync(
        string anyPathInRepository, CancellationToken ct = default) =>
        Task.Run<IReadOnlyList<GitWorktreeInfo>>(() =>
        {
            var mainGitDir = ResolveMainGitDirectory(anyPathInRepository);
            if (mainGitDir is null) return Array.Empty<GitWorktreeInfo>();

            var currentRoot = NormalizeDirectory(WorkingDirectoryOf(anyPathInRepository));
            var result = new List<GitWorktreeInfo>();

            try
            {
                using var repo = new Repository(mainGitDir);
                ct.ThrowIfCancellationRequested();

                var mainPath = NormalizeDirectory(repo.Info.WorkingDirectory);
                if (mainPath.Length > 0)
                {
                    result.Add(Describe(
                        repo,
                        name: Path.GetFileName(mainPath),
                        path: mainPath,
                        isMain: true,
                        isCurrent: PathsEqual(mainPath, currentRoot),
                        isLocked: false));
                }

                // libgit2 stops listing a worktree whose directory was deleted by hand (and enumerating
                // repo.Worktrees can throw on such a record, losing every *other* worktree too), but the
                // record stays in .git/worktrees. So the registry is walked by name: live entries are
                // described, dangling ones are shown as "missing" rows the dashboard can prune. An
                // invisible leftover is one nobody ever cleans up.
                var stale = FindStale(mainGitDir).ToDictionary(w => w.Name, StringComparer.Ordinal);

                foreach (var worktree in EnumerateLive(repo, stale.Keys))
                {
                    ct.ThrowIfCancellationRequested();
                    result.Add(DescribeLinked(worktree, currentRoot));
                }

                foreach (var ghost in stale.Values)
                {
                    result.Add(new GitWorktreeInfo(
                        ghost.Name, ghost.ExpectedPath, string.Empty,
                        IsMain: false, IsCurrent: false, IsLocked: ghost.IsLocked, Missing: true,
                        DirtyCount: 0, AheadBy: 0, BehindBy: 0, HasUpstream: false));
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to list worktrees for {Path}", anyPathInRepository);
            }

            return result;
        }, ct);

    /// <summary>
    /// Creates the worktree. The directory must not already hold files: git would refuse anyway, and
    /// checking here turns a native error into a sentence the dialog can show.
    /// </summary>
    public Task<GitOperationResult> AddAsync(GitWorktreeRequest request, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            ArgumentNullException.ThrowIfNull(request);

            if (!GitRefName.IsValidBranch(request.Branch))
                return GitOperationResult.Fail(
                    $"'{request.Branch}' is not a valid branch name.", GitFailureKind.InvalidBranchName, request.Branch);

            if (Directory.Exists(request.TargetPath)
                && Directory.EnumerateFileSystemEntries(request.TargetPath).Any())
                return GitOperationResult.Fail(
                    $"Directory is not empty: {request.TargetPath}", GitFailureKind.DirectoryNotEmpty, request.TargetPath);

            var mainGitDir = ResolveMainGitDirectory(request.AnyPathInRepository);
            if (mainGitDir is null)
            {
                return GitOperationResult.Fail(
                    $"Not a git repository: {request.AnyPathInRepository}",
                    GitFailureKind.NotARepository, request.AnyPathInRepository);
            }

            var createdBranch = false;
            string? registeredName = null;

            try
            {
                // A registration whose directory is gone would make libgit2 refuse the name (and the
                // branch it held) for a worktree that no longer exists; clearing it is what
                // `git worktree prune` does and is always safe for an unlocked, dangling entry.
                PruneStale(mainGitDir);

                using var repo = new Repository(mainGitDir);
                ct.ThrowIfCancellationRequested();

                var existing = repo.Branches[request.Branch];
                if (request.CreateBranch && existing is not null)
                    return GitOperationResult.Fail(
                        $"Branch already exists: {request.Branch}", GitFailureKind.BranchExists, request.Branch);
                if (!request.CreateBranch && existing is null)
                    return GitOperationResult.Fail(
                        $"No such branch: {request.Branch}", GitFailureKind.NoSuchBranch, request.Branch);

                // A branch already checked out elsewhere cannot be checked out again — git allows one
                // worktree per branch, and the native error for it is unreadable.
                if (existing is not null && CheckedOutAt(repo, request.Branch) is { } owner)
                    return GitOperationResult.Fail(
                        $"Branch is already checked out in another worktree: {request.Branch}",
                        GitFailureKind.CheckedOutElsewhere, owner);

                var folderName = Path.GetFileName(request.TargetPath.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(folderName))
                {
                    return GitOperationResult.Fail(
                        $"Invalid worktree path: {request.TargetPath}", GitFailureKind.InvalidPath, request.TargetPath);
                }

                if (request.CreateBranch)
                {
                    var fromHead = string.IsNullOrWhiteSpace(request.BaseCommittish);
                    var startPoint = fromHead ? repo.Head.Tip : ResolveCommit(repo, request.BaseCommittish);

                    // A repository without a commit has no HEAD to branch from. "Cannot resolve start
                    // point:" with nothing after the colon blamed a field the user had left empty.
                    if (startPoint is null && fromHead)
                    {
                        return GitOperationResult.Fail(
                            "The repository has no commits yet to branch from.", GitFailureKind.NoCommits);
                    }

                    if (startPoint is null)
                    {
                        return GitOperationResult.Fail(
                            $"Cannot resolve start point: {request.BaseCommittish}",
                            GitFailureKind.NoSuchRevision, request.BaseCommittish);
                    }

                    // "a/b" while "a" exists, or the reverse: a ref is a file, and a file cannot also be
                    // a folder. libgit2's own words for it are about a directory it could not remove.
                    var clash = GitRefName.FindPathConflict(
                        request.Branch, repo.Branches.Where(b => !b.IsRemote).Select(b => b.FriendlyName));
                    if (clash is not null)
                    {
                        return GitOperationResult.Fail(
                            $"Branch name collides with the existing branch {clash}: {request.Branch}",
                            GitFailureKind.BranchNameConflict, clash);
                    }

                    repo.Branches.Add(request.Branch, startPoint);
                    createdBranch = true;
                }

                // libgit2 insists on creating the directory itself and fails outright if it is
                // already there. An empty one the user pointed at is fine to clear out of the way;
                // a non-empty one was rejected above.
                if (Directory.Exists(request.TargetPath)) Directory.Delete(request.TargetPath);

                var worktreeName = UniqueWorktreeName(repo, folderName, request.Branch);
                var worktree = repo.Worktrees.Add(
                    request.Branch, worktreeName, request.TargetPath, isLocked: false);
                registeredName = worktreeName;

                FinishWorktree(repo, worktree, worktreeName, request.Branch);

                _logger.LogInformation(
                    "Created worktree {Name} at {Path} on {Branch}", worktreeName, request.TargetPath, request.Branch);
                return GitOperationResult.Ok(request.TargetPath);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to add worktree at {Path}", request.TargetPath);

                // The branch was created a few lines before the worktree failed. Leaving it behind
                // makes the obvious next move — press Create again — fail with "branch already
                // exists", which points at the wrong problem entirely. The rollback opens its own
                // repository: `using var repo` above is disposed on the way out of the try block,
                // so the handle this catch can see is already dead.
                // A worktree that was registered but never checked out is an empty folder on a
                // branch: reporting it as created would move the panel into nothing and run the setup
                // command there. It goes first, since it holds the branch the next step removes.
                if (registeredName is not null) RollBackWorktree(mainGitDir, registeredName, request.TargetPath);
                if (createdBranch) RollBackBranch(mainGitDir, request.Branch);

                // Classified, not passed through: libgit2's English reached the agent launcher
                // verbatim (ROADMAP 6.53).
                return GitErrors.FromException(ex, request.AnyPathInRepository);
            }
        }, ct);

    /// <summary>
    /// Removes a worktree: prunes the record git keeps of it, then deletes its directory. That order
    /// matters — once the directory is gone, <c>repo.Worktrees[name]</c> no longer resolves, so
    /// deleting first would leave the registration behind with nothing left to prune it by.
    /// <para>
    /// Destructive, so the caller confirms first (INV-11); this method only refuses the cases the
    /// user cannot see — a main checkout, a lock they did not ask to override, and changes they were
    /// not told about. The confirmation was built from a count read when the list was loaded, and an
    /// agent may have written into the worktree since: the count is read again here, and anything
    /// above <paramref name="acceptedDirtyCount"/> (or a count that cannot be read at all) refuses
    /// with <see cref="GitFailureKind.DirtyWorkingTree"/> and the fresh count as the detail.
    /// </para>
    /// </summary>
    /// <param name="acceptedDirtyCount">How many uncommitted changes the user agreed to lose;
    /// <see cref="int.MaxValue"/> accepts whatever is there, including an unreadable count.</param>
    public Task<GitOperationResult> RemoveAsync(
        string anyPathInRepository, string worktreeName, bool force, CancellationToken ct = default,
        int acceptedDirtyCount = 0) =>
        Task.Run(() =>
        {
            var mainGitDir = ResolveMainGitDirectory(anyPathInRepository);
            if (mainGitDir is null)
                return GitOperationResult.Fail(
                    $"Not a git repository: {anyPathInRepository}", GitFailureKind.NotARepository, anyPathInRepository);

            try
            {
                using var repo = new Repository(mainGitDir);
                ct.ThrowIfCancellationRequested();

                var worktree = TryLookup(repo, worktreeName);
                if (worktree is null)
                {
                    var stale = FindStale(mainGitDir).FirstOrDefault(
                        w => string.Equals(w.Name, worktreeName, StringComparison.Ordinal));
                    if (stale is null)
                        return GitOperationResult.Fail(
                            $"No such worktree: {worktreeName}", GitFailureKind.NoSuchWorktree, worktreeName);
                    if (stale.IsLocked && !force)
                        return GitOperationResult.Fail(
                            $"Worktree is locked: {worktreeName}", GitFailureKind.WorktreeLocked, worktreeName);

                    DirectoryTree.Delete(stale.RegistrationDirectory);
                    _logger.LogInformation("Pruned stale worktree registration {Name}", worktreeName);
                    return GitOperationResult.Ok(worktreeName);
                }

                if (worktree.IsLocked && !force)
                    return GitOperationResult.Fail(
                        $"Worktree is locked: {worktreeName}", GitFailureKind.WorktreeLocked, worktreeName);

                // Read the path while the worktree still resolves: it is the only thing that knows
                // which directory to delete afterwards.
                var path = TryGetWorktreePath(worktree);

                if (acceptedDirtyCount != int.MaxValue && path is not null && Directory.Exists(path))
                {
                    var dirty = CountDirtyAt(worktree);
                    if (dirty == GitWorktreeInfo.UnknownDirtyCount || dirty > acceptedDirtyCount)
                        return GitOperationResult.Fail(
                            $"Worktree has uncommitted changes: {worktreeName}",
                            GitFailureKind.DirtyWorkingTree,
                            dirty == GitWorktreeInfo.UnknownDirtyCount ? "?" : dirty.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                bool pruned;
                try
                {
                    pruned = repo.Worktrees.Prune(worktree, ifLocked: force);
                }
                catch (LibGit2SharpException ex) when (!Directory.Exists(Path.Combine(mainGitDir, "worktrees", worktreeName)))
                {
                    // libgit2 drops the registration first and deletes the checkout second, so a file held
                    // open in the checkout fails the call after git has already forgotten the worktree.
                    _logger.LogDebug(ex, "Worktree {Name} was unregistered, its checkout was not deleted", worktreeName);
                    pruned = true;
                }

                if (!pruned)
                    return GitOperationResult.Fail(
                        $"git refused to prune the worktree: {worktreeName}", GitFailureKind.WorktreeNotPruned, worktreeName);

                if (path is not null && Directory.Exists(path))
                {
                    try
                    {
                        DirectoryTree.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        // The registration is already gone, so the worktree is genuinely removed —
                        // report the leftover folder instead of pretending the whole thing failed.
                        _logger.LogWarning(ex, "Worktree {Name} was pruned but {Path} could not be deleted", worktreeName, path);
                        return new GitOperationResult(true, ex.Message, GitFailureKind.DirectoryNotDeleted, path);
                    }
                }

                _logger.LogInformation("Removed worktree {Name}", worktreeName);
                return GitOperationResult.Ok(path ?? worktreeName);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to remove worktree {Name}", worktreeName);
                return GitErrors.FromException(ex, anyPathInRepository);
            }
        }, ct);

    /// <summary>
    /// Undoes a worktree that was registered but never became usable: its record under
    /// <c>.git/worktrees</c> and the directory libgit2 created. The directory was verified empty (or
    /// absent) before the attempt, so whatever is in it now belongs to this attempt.
    /// </summary>
    private void RollBackWorktree(string mainGitDir, string worktreeName, string targetPath)
    {
        try
        {
            var registration = Path.Combine(mainGitDir, "worktrees", worktreeName);
            if (Directory.Exists(registration)) DirectoryTree.Delete(registration);
            if (Directory.Exists(targetPath)) DirectoryTree.Delete(targetPath);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not roll back the worktree {Name}", worktreeName);
        }
    }

    /// <summary>Removes a branch created moments before a worktree failed to appear, so retrying
    /// the same request is not blocked by the leftovers of the last attempt.</summary>
    private void RollBackBranch(string gitDirectory, string branch)
    {
        try
        {
            using var repo = new Repository(gitDirectory);
            if (repo.Branches[branch] is not null) repo.Branches.Remove(branch);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not roll back the branch {Branch}", branch);
        }
    }

    /// <summary>
    /// Cleans up after <c>WorktreeCollection.Add</c>, which has two behaviours worth knowing about
    /// (both verified against LibGit2Sharp 0.30, both silent):
    /// <list type="number">
    /// <item>it registers the worktree but leaves its working directory <em>empty</em> — HEAD points
    /// at the branch while not a single file has been written, so the checkout has to be forced;</item>
    /// <item>it creates a branch named after the worktree as a side effect, which would show up in
    /// every branch list from then on.</item>
    /// </list>
    /// A failed checkout is thrown on, not logged away: the worktree would be an empty folder, and
    /// <see cref="AddAsync"/> rolls it back instead of reporting it as ready.
    /// </summary>
    private void FinishWorktree(Repository repo, Worktree worktree, string worktreeName, string branch)
    {
        try
        {
            using var wtRepo = worktree.WorktreeRepository;
            Commands.Checkout(wtRepo, branch, new CheckoutOptions
            {
                CheckoutModifiers = CheckoutModifiers.Force,
            });
        }
        finally
        {
            RemovePlaceholderBranch(repo, worktreeName);
        }
    }

    private void RemovePlaceholderBranch(Repository repo, string worktreeName)
    {
        // Only ever the branch Add invented for itself — UniqueWorktreeName guarantees the name is
        // not the branch the user asked for and was not already taken.
        try
        {
            if (repo.Branches[worktreeName] is not null) repo.Branches.Remove(worktreeName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not remove the placeholder branch {Name}", worktreeName);
        }
    }

    /// <summary>
    /// Picks the git-internal name for a new worktree. It must differ from the branch: given equal
    /// strings LibGit2Sharp takes a different code path that ignores the target directory entirely
    /// and leaves nothing behind. It must also be free as a branch name, since Add creates one.
    /// </summary>
    private static string UniqueWorktreeName(Repository repo, string preferred, string branch)
    {
        var name = preferred;
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var collides = string.Equals(name, branch, StringComparison.OrdinalIgnoreCase)
                           || repo.Branches[name] is not null
                           || TryLookup(repo, name) is not null
                           || Directory.Exists(Path.Combine(repo.Info.Path, "worktrees", name));
            if (!collides) return name;

            name = $"{preferred}-{suffix}";
        }

        return $"{preferred}-{Guid.NewGuid():N}"[..Math.Min(preferred.Length + 9, 40)];
    }

    /// <summary>A worktree registration whose checkout directory no longer exists.</summary>
    /// <param name="Name">Name under <c>.git/worktrees</c>.</param>
    /// <param name="ExpectedPath">Where the checkout used to be; empty when the record is unreadable.</param>
    /// <param name="IsLocked">A <c>locked</c> marker file is present — pruning needs force.</param>
    /// <param name="RegistrationDirectory">The <c>.git/worktrees/&lt;name&gt;</c> folder itself.</param>
    internal sealed record StaleWorktree(string Name, string ExpectedPath, bool IsLocked, string RegistrationDirectory);

    /// <summary>Registrations under <c>.git/worktrees</c> whose <c>gitdir</c> pointer leads nowhere.</summary>
    internal static IReadOnlyList<StaleWorktree> FindStale(string mainGitDir)
    {
        var result = new List<StaleWorktree>();
        var registry = Path.Combine(mainGitDir, "worktrees");
        if (!Directory.Exists(registry)) return result;

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(registry))
            {
                var name = Path.GetFileName(directory);
                var pointer = Path.Combine(directory, "gitdir");
                var locked = File.Exists(Path.Combine(directory, "locked"));

                string target;
                try
                {
                    target = File.Exists(pointer) ? File.ReadAllText(pointer).Trim() : string.Empty;
                }
                catch (IOException)
                {
                    continue;   // being written right now; not ours to judge
                }

                if (target.Length > 0 && (File.Exists(target) || Directory.Exists(target))) continue;

                var expected = target.Length == 0
                    ? string.Empty
                    : NormalizeDirectory(Path.GetDirectoryName(target.Replace('/', Path.DirectorySeparatorChar)));
                result.Add(new StaleWorktree(name, expected, locked, directory));
            }
        }
        catch (IOException)
        {
            // A registry that cannot be enumerated simply reports no leftovers.
        }

        return result;
    }

    /// <summary>
    /// The worktrees whose records are intact, found by walking <c>.git/worktrees</c> by name instead
    /// of enumerating <c>repo.Worktrees</c>: that enumeration throws on a single dangling record and
    /// would take every healthy worktree down with it.
    /// </summary>
    private static IEnumerable<Worktree> EnumerateLive(Repository main, IEnumerable<string> skip)
    {
        var registry = Path.Combine(main.Info.Path, "worktrees");
        if (!Directory.Exists(registry)) yield break;

        var skipped = new HashSet<string>(skip, StringComparer.Ordinal);
        foreach (var directory in Directory.EnumerateDirectories(registry))
        {
            var name = Path.GetFileName(directory);
            if (skipped.Contains(name)) continue;

            Worktree? worktree;
            try
            {
                worktree = TryLookup(main, name);
            }
            catch (LibGit2SharpException)
            {
                continue;   // a record libgit2 cannot resolve is not a usable worktree
            }

            if (worktree is not null) yield return worktree;
        }
    }

    /// <summary>Looks a worktree up by name; a record libgit2 cannot resolve counts as absent.</summary>
    private static Worktree? TryLookup(Repository repo, string name)
    {
        try
        {
            return repo.Worktrees[name];
        }
        catch (LibGit2SharpException)
        {
            return null;
        }
    }

    /// <summary>Deletes every unlocked stale registration; returns how many went.</summary>
    internal int PruneStale(string mainGitDir)
    {
        var pruned = 0;
        foreach (var stale in FindStale(mainGitDir))
        {
            if (stale.IsLocked) continue;

            try
            {
                DirectoryTree.Delete(stale.RegistrationDirectory);
                pruned++;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not prune stale worktree {Name}", stale.Name);
            }
        }

        return pruned;
    }

    /// <summary>
    /// Which other checkout, if any, has <paramref name="branch"/> checked out. git allows a branch in
    /// one worktree at a time and libgit2 does not enforce it on a plain checkout, so switching
    /// branches would otherwise leave two working trees on one branch.
    /// </summary>
    internal static string? FindBranchOwner(Repository current, string branch) =>
        MapBranchOwners(current).GetValueOrDefault(branch);

    /// <summary>Branch name → working directory for every checkout <em>other than</em> the one
    /// <paramref name="current"/> was opened in. Detached checkouts hold no branch and are skipped.</summary>
    internal static Dictionary<string, string> MapBranchOwners(Repository current)
    {
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        var currentRoot = NormalizeDirectory(current.Info.WorkingDirectory);
        var mainGitDir = ResolveMainGitDirectory(currentRoot);
        if (mainGitDir is null) return owners;

        try
        {
            using var main = new Repository(mainGitDir);

            var mainPath = NormalizeDirectory(main.Info.WorkingDirectory);
            if (mainPath.Length > 0 && !PathsEqual(mainPath, currentRoot) && HeadBranch(main) is { } mainBranch)
                owners.TryAdd(mainBranch, mainPath);

            foreach (var worktree in EnumerateLive(main, FindStale(main.Info.Path).Select(w => w.Name)))
            {
                try
                {
                    using var wtRepo = worktree.WorktreeRepository;
                    var path = NormalizeDirectory(wtRepo.Info.WorkingDirectory);
                    if (PathsEqual(path, currentRoot)) continue;
                    if (HeadBranch(wtRepo) is { } branch) owners.TryAdd(branch, path);
                }
                catch
                {
                    // A worktree that cannot be opened is not holding the branch in any way that matters.
                }
            }
        }
        catch
        {
            // No worktree information means no guard, not a failed checkout.
        }

        return owners;
    }

    private static string? HeadBranch(IRepository repo) =>
        repo.Head.FriendlyName is { Length: > 0 } name && !name.StartsWith('(') ? name : null;

    /// <summary>
    /// Git directory of the repository that owns <paramref name="path"/> — the <em>main</em> one,
    /// even when called from inside a linked worktree. Worktrees are only listed and created through
    /// the main repository, and a linked worktree's own git dir is
    /// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>, which enumerates nothing.
    /// <para>
    /// The step up is decided by the git dir's own last two components, not by searching the whole
    /// path for "worktrees": a repository that merely lives under such a folder — say
    /// <c>D:\worktrees\myrepo</c> — would otherwise be cut down to <c>D:\</c> and every worktree
    /// operation in it would fail for no visible reason.
    /// </para>
    /// </summary>
    public static string? ResolveMainGitDirectory(string? path)
    {
        var gitDir = GitHeadReader.FindGitDirectory(path);
        if (gitDir is null) return null;

        var full = Path.GetFullPath(gitDir).TrimEnd('\\', '/');

        // A plain checkout's git dir is ".git" and is already the main one.
        if (string.Equals(Path.GetFileName(full), ".git", StringComparison.OrdinalIgnoreCase))
            return full;

        var parent = Path.GetDirectoryName(full);
        if (parent is null
            || !string.Equals(Path.GetFileName(parent), "worktrees", StringComparison.OrdinalIgnoreCase))
            return full;

        // <main>/worktrees/<name> → <main>, but only if what is left really is a git directory:
        // a repository whose checkout is itself called "worktrees" reaches this line too.
        var main = Path.GetDirectoryName(parent);
        return main is not null && File.Exists(Path.Combine(main, "HEAD")) ? main : full;
    }

    /// <summary>Working directory of the checkout containing <paramref name="path"/> — the worktree
    /// root, not the main repository's, so "which row am I in?" answers correctly.</summary>
    private static string WorkingDirectoryOf(string? path) =>
        GitHeadReader.FindWorkingDirectory(path) ?? string.Empty;

    private GitWorktreeInfo DescribeLinked(Worktree worktree, string currentRoot)
    {
        try
        {
            using var wtRepo = worktree.WorktreeRepository;
            var path = NormalizeDirectory(wtRepo.Info.WorkingDirectory);
            return Describe(
                wtRepo,
                worktree.Name,
                path,
                isMain: false,
                isCurrent: PathsEqual(path, currentRoot),
                isLocked: worktree.IsLocked);
        }
        catch (Exception ex)
        {
            // A worktree whose directory was deleted by hand still has a record; showing it as
            // "missing" is the only way the user can prune it from here.
            _logger.LogDebug(ex, "Worktree {Name} could not be opened", worktree.Name);
            return new GitWorktreeInfo(
                worktree.Name, string.Empty, string.Empty,
                IsMain: false, IsCurrent: false, IsLocked: SafeIsLocked(worktree), Missing: true,
                DirtyCount: 0, AheadBy: 0, BehindBy: 0, HasUpstream: false);
        }
    }

    private static bool SafeIsLocked(Worktree worktree)
    {
        try { return worktree.IsLocked; } catch { return false; }
    }

    private static GitWorktreeInfo Describe(
        IRepository repo, string name, string path, bool isMain, bool isCurrent, bool isLocked)
    {
        var head = repo.Head;
        var tracking = head.IsTracking ? head.TrackingDetails : null;

        return new GitWorktreeInfo(
            Name: name,
            Path: path,
            Branch: BranchLabel(head),
            IsMain: isMain,
            IsCurrent: isCurrent,
            IsLocked: isLocked,
            Missing: false,
            DirtyCount: CountDirty(repo),
            AheadBy: tracking?.AheadBy ?? 0,
            BehindBy: tracking?.BehindBy ?? 0,
            HasUpstream: tracking is not null);
    }

    private static string BranchLabel(Branch head)
    {
        // A detached HEAD has a synthetic friendly name ("(no branch)"); the short id is the only
        // thing that actually identifies where the worktree is.
        if (head.FriendlyName is { Length: > 0 } friendly && !friendly.StartsWith('('))
            return friendly;

        var sha = head.Tip?.Sha;
        return sha is { Length: >= 7 } ? sha[..7] : string.Empty;
    }

    private static int CountDirty(IRepository repo)
    {
        try
        {
            var status = repo.RetrieveStatus(new StatusOptions
            {
                IncludeUntracked = true,
                RecurseUntrackedDirs = false,
                IncludeIgnored = false,
            });
            return status.Count(e => e.State != FileStatus.Ignored && e.State != FileStatus.Unaltered);
        }
        catch
        {
            // A status walk over an enormous or half-broken tree must not lose the whole listing —
            // but it must not read as "clean" either: that is the answer that lets removal skip
            // its data-loss warning.
            return GitWorktreeInfo.UnknownDirtyCount;
        }
    }

    /// <summary>Working directory of the checkout (main or linked) that has <paramref name="branch"/>
    /// checked out, or null. <paramref name="repo"/> is the main repository.</summary>
    private static string? CheckedOutAt(Repository repo, string branch)
    {
        if (string.Equals(repo.Head.FriendlyName, branch, StringComparison.Ordinal))
            return NormalizeDirectory(repo.Info.WorkingDirectory);

        foreach (var worktree in EnumerateLive(repo, FindStale(repo.Info.Path).Select(w => w.Name)))
        {
            try
            {
                using var wtRepo = worktree.WorktreeRepository;
                if (string.Equals(wtRepo.Head.FriendlyName, branch, StringComparison.Ordinal))
                    return NormalizeDirectory(wtRepo.Info.WorkingDirectory);
            }
            catch
            {
                // A worktree we cannot open cannot be holding the branch in any way that matters.
            }
        }

        return null;
    }

    /// <summary>Uncommitted entries of a linked worktree, read now; unknown when it cannot be opened.</summary>
    private static int CountDirtyAt(Worktree worktree)
    {
        try
        {
            using var wtRepo = worktree.WorktreeRepository;
            return CountDirty(wtRepo);
        }
        catch
        {
            return GitWorktreeInfo.UnknownDirtyCount;
        }
    }

    private static Commit? ResolveCommit(Repository repo, string committish)
    {
        try
        {
            return repo.Lookup<Commit>(committish)
                   ?? repo.Branches[committish]?.Tip
                   ?? repo.Lookup<Commit>($"refs/heads/{committish}");
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetWorktreePath(Worktree worktree)
    {
        try
        {
            using var wtRepo = worktree.WorktreeRepository;
            return NormalizeDirectory(wtRepo.Info.WorkingDirectory);
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeDirectory(string? path) =>
        string.IsNullOrWhiteSpace(path)
            ? string.Empty
            : Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool PathsEqual(string a, string b) =>
        a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
