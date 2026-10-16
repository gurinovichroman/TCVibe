namespace TCV.Git;

/// <summary>
/// Why a git operation failed, when the caller can do something better than print the message:
/// offer to stash on a dirty tree, ask for credentials after an authentication failure, warn harder
/// before deleting an unmerged branch. <see cref="None"/> means "just show the message".
/// </summary>
public enum GitFailureKind
{
    None,
    /// <summary>Uncommitted changes block the operation (checkout, worktree removal). For a worktree
    /// removal the detail is the fresh change count, or "?" when it could not be read.</summary>
    DirtyWorkingTree,
    /// <summary>The remote rejected or required credentials.</summary>
    Authentication,
    /// <summary>The branch holds commits no other branch has.</summary>
    Unmerged,
    /// <summary>Another worktree has the branch checked out.</summary>
    CheckedOutElsewhere,
    /// <summary>user.name / user.email are not configured.</summary>
    NoIdentity,
    /// <summary>The commit would have no changes.</summary>
    NothingToCommit,
    /// <summary>The operation stopped on conflicts.</summary>
    Conflicts,
    /// <summary>A stash was asked for on a clean tree.</summary>
    NothingToStash,
    /// <summary>A branch to be created already exists; <see cref="GitOperationResult.Detail"/> is its name.</summary>
    BranchExists,
    /// <summary>The named branch does not exist; the detail is its name.</summary>
    NoSuchBranch,
    /// <summary>The text is not a valid branch name; the detail is the text.</summary>
    InvalidBranchName,
    /// <summary>The repository has no commit yet to branch or restore from.</summary>
    NoCommits,
    /// <summary>The worktree is locked and force was not given; the detail is its name.</summary>
    WorktreeLocked,
    /// <summary>The target directory already holds files; the detail is its path.</summary>
    DirectoryNotEmpty,
    /// <summary>Untracked files in the working tree would be overwritten by a checkout.</summary>
    CheckoutConflict,
    /// <summary>The path is not inside a git repository; the detail is the path.</summary>
    NotARepository,
    /// <summary>A branch or commit to start from does not resolve; the detail is what was typed.</summary>
    NoSuchRevision,
    /// <summary>The path cannot hold a worktree (a drive root, say); the detail is the path.</summary>
    InvalidPath,
    /// <summary>A <c>.lock</c> file is in the way: another git process is working on the repository.</summary>
    RepositoryLocked,
    /// <summary>The branch name collides with an existing one (<c>a</c> and <c>a/b</c> cannot both
    /// exist); the detail is the existing branch in the way.</summary>
    BranchNameConflict,
    /// <summary>An SSH remote: this build has no SSH transport. The detail is the host.</summary>
    SshUnsupported,
    /// <summary>The server's TLS certificate was rejected; the detail is libgit2's reason.</summary>
    Certificate,
    /// <summary>None of the paths to stage, unstage or restore lies inside the repository (or none was given).</summary>
    NoPathsInRepository,
    /// <summary>A commit was asked for with an empty message.</summary>
    EmptyCommitMessage,
    /// <summary>The branch to delete is the one checked out here; the detail is its name.</summary>
    CurrentBranch,
    /// <summary>A stash pop found no stash entry to apply.</summary>
    NoStash,
    /// <summary>No worktree, live or stale, goes by that name; the detail is the name.</summary>
    NoSuchWorktree,
    /// <summary>libgit2 declined to prune the worktree's registration; the detail is its name.</summary>
    WorktreeNotPruned,
    /// <summary>The caller cancelled before anything changed.</summary>
    Cancelled,
    /// <summary>The text is not a repository address; the detail is the text.</summary>
    InvalidUrl,
    /// <summary>No folder to clone into was given.</summary>
    NoTargetDirectory,
    /// <summary>Not a failure of the operation: the worktree is gone from git, but its folder could not
    /// be deleted (something holds a file in it). Comes with <see cref="GitOperationResult.Success"/>;
    /// the detail is the folder, the message the reason.</summary>
    DirectoryNotDeleted,
    /// <summary>Nothing above: libgit2 (or the file system) failed in a way we do not classify. The
    /// detail is its own text, which the UI wraps in a sentence of its own rather than showing bare.</summary>
    Unexpected,
}

/// <summary>
/// Outcome of a git write. Git failures are ordinary (a dirty tree blocks a checkout, a branch name
/// is taken, the remote refuses), so they travel as a value the caller shows in the dialog rather
/// than as an exception the UI thread has to catch — the same reason
/// <see cref="GitStatusProvider"/> swallows and reports instead of throwing.
/// </summary>
/// <param name="Success">True when the repository actually changed as asked.</param>
/// <param name="Message">Human-readable detail: the git error, or a summary on success.</param>
/// <param name="Kind">Machine-readable reason for a failure, or for the part a success left undone
/// (<see cref="GitFailureKind.DirectoryNotDeleted"/>); <see cref="GitFailureKind.None"/> otherwise.</param>
/// <param name="Detail">The one variable part of a <paramref name="Kind"/> (a worktree path, a branch
/// name), so the UI can put it into a localised sentence instead of showing the English message.</param>
public sealed record GitOperationResult(
    bool Success, string Message = "", GitFailureKind Kind = GitFailureKind.None, string Detail = "")
{
    public static GitOperationResult Ok(string message = "") => new(true, message);
    public static GitOperationResult Fail(string message, GitFailureKind kind = GitFailureKind.None, string detail = "") =>
        new(false, message, kind, detail);
}
