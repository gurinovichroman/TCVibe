using System.IO;
using LibGit2Sharp;
using Microsoft.Extensions.Logging;

namespace TCV.Git;

/// <summary>Diff of one file against HEAD, with the branch it was taken on.</summary>
/// <param name="Text">Unified diff, empty when nothing changed, null when the file is not in a repository.</param>
/// <param name="Branch">Current branch or short commit id.</param>
public sealed record GitFileDiff(string? Text, string Branch);

/// <summary>
/// Read-only questions the file panels ask git: "what changed in this folder?" (the git column) and
/// "what does this file look like against HEAD?" (Ctrl+G). Stateless: every call opens the
/// repository on a thread-pool thread and disposes it before returning (INV-06 / TRAP-06), so no
/// handle outlives a call and a worktree can be removed while its panel is still open.
/// </summary>
public sealed class GitStatusProvider
{
    private readonly ILogger<GitStatusProvider> _logger;

    public GitStatusProvider(ILogger<GitStatusProvider> logger) => _logger = logger;

    /// <summary>
    /// Status of the direct children of <paramref name="directory"/>, from one pass that is limited
    /// to that folder's subtree and does not descend into untracked directories — the cost follows
    /// what the panel shows, not the size of the repository.
    /// </summary>
    public Task<GitDirectoryStatus> GetDirectoryStatusAsync(string directory, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var gitDir = GitHeadReader.FindGitDirectory(directory);
            if (gitDir is null) return GitDirectoryStatus.None;

            try
            {
                using var repo = new Repository(gitDir);
                ct.ThrowIfCancellationRequested();

                var root = repo.Info.WorkingDirectory;
                if (string.IsNullOrEmpty(root)) return GitDirectoryStatus.None;

                var relative = Path.GetRelativePath(root, Path.GetFullPath(directory)).Replace('\\', '/');
                if (relative.StartsWith("..", StringComparison.Ordinal)) return GitDirectoryStatus.None;
                var prefix = relative == "." ? string.Empty : relative.TrimEnd('/') + "/";

                var files = ReadChanges(repo, prefix, recurseUntracked: false, ct);

                // An untracked directory is reported as one "dir/" entry. That is exactly what we want
                // for a child of the listed folder, but if the listed folder is itself inside such an
                // entry there are no children to see: look inside, once, only in that case.
                if (prefix.Length > 0 && files.Any(f =>
                        f.RelativePath.EndsWith('/') && prefix.StartsWith(f.RelativePath, StringComparison.OrdinalIgnoreCase)))
                    files = ReadChanges(repo, prefix, recurseUntracked: true, ct);

                return new GitDirectoryStatus(true, BranchLabel(repo.Head), GitDirectoryStatus.BuildCells(files, prefix));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to retrieve git statuses for {Dir}", directory);
                return GitDirectoryStatus.None;
            }
        }, ct);

    /// <summary>
    /// Unified diff of <paramref name="absolutePath"/> against HEAD (index and working tree
    /// combined). A directory yields the diff of everything below it — the repository root, of the
    /// whole working tree. A null <see cref="GitFileDiff.Text"/> means the path is not inside a repository.
    /// </summary>
    public Task<GitFileDiff> GetDiffVsHeadAsync(string absolutePath, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            // A directory is looked up from itself: from its parent, a repository root would be
            // searched for above the repository and not found.
            var isDirectory = Directory.Exists(absolutePath);
            var gitDir = GitHeadReader.FindGitDirectory(
                isDirectory ? absolutePath : Path.GetDirectoryName(absolutePath) ?? absolutePath);
            if (gitDir is null) return new GitFileDiff(null, string.Empty);

            try
            {
                using var repo = new Repository(gitDir);
                ct.ThrowIfCancellationRequested();

                var root = repo.Info.WorkingDirectory;
                var relative = Path.GetRelativePath(root, absolutePath).Replace('\\', '/').TrimEnd('/');
                var headTree = repo.Head.Tip?.Tree;

                // Literal paths match a directory's whole subtree; the root itself needs no filter.
                using var patch = repo.Diff.Compare<Patch>(
                    headTree,
                    DiffTargets.WorkingDirectory,
                    relative is "." or "" ? null : new[] { relative },
                    new ExplicitPathsOptions { ShouldFailOnUnmatchedPath = false });

                return new GitFileDiff(patch.Content ?? string.Empty, BranchLabel(repo.Head));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Diff failed for {Path}", absolutePath);
                return new GitFileDiff(null, string.Empty);
            }
        }, ct);

    private static List<GitChangedFile> ReadChanges(
        Repository repo, string prefix, bool recurseUntracked, CancellationToken ct)
    {
        var options = new StatusOptions
        {
            IncludeUntracked = true,
            RecurseUntrackedDirs = recurseUntracked,
            IncludeIgnored = false,
            DisablePathSpecMatch = true,
        };
        if (prefix.Length > 0) options.PathSpec = new[] { prefix };

        var files = new List<GitChangedFile>();
        foreach (var entry in repo.RetrieveStatus(options))
        {
            ct.ThrowIfCancellationRequested();
            files.AddRange(GitStatusMapper.ToChangedFiles(entry));
        }

        return files;
    }

    private static string BranchLabel(Branch head)
    {
        if (head.FriendlyName is { Length: > 0 } friendly && !friendly.StartsWith('('))
            return friendly;

        var sha = head.Tip?.Sha;
        return sha is { Length: >= 7 } ? sha[..7] : string.Empty;
    }
}
