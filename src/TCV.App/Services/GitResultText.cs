using TCV.App.Localization;
using TCV.Git;

namespace TCV.App.Services;

/// <summary>
/// Turns a <see cref="GitOperationResult"/> into a sentence in the user's language. The Git layer
/// speaks English (it is logged and tested that way); anything it can classify by
/// <see cref="GitFailureKind"/> is re-worded here, and the rest falls back to libgit2's own text.
/// </summary>
public static class GitResultText
{
    public static string Describe(GitOperationResult result)
    {
        if (result.Success && result.Kind == GitFailureKind.None) return result.Message;

        return result.Kind switch
        {
            GitFailureKind.NoIdentity => LocalizationManager.Get("Git_ErrNoIdentity"),
            GitFailureKind.NothingToCommit => LocalizationManager.Get("Git_ErrNothingToCommit"),
            GitFailureKind.DirtyWorkingTree => LocalizationManager.Get("Git_ErrDirty"),
            GitFailureKind.CheckedOutElsewhere =>
                string.Format(LocalizationManager.Get("Git_ErrElsewhere"), result.Detail),
            GitFailureKind.Conflicts => LocalizationManager.Get("Git_ErrConflicts"),
            GitFailureKind.Unmerged =>
                string.Format(LocalizationManager.Get("Git_ErrUnmerged"), result.Detail),
            GitFailureKind.Authentication => LocalizationManager.Get("Git_ErrAuth"),
            GitFailureKind.NothingToStash => LocalizationManager.Get("Git_ErrNothingToStash"),
            GitFailureKind.BranchExists =>
                string.Format(LocalizationManager.Get("Git_ErrBranchExists"), result.Detail),
            GitFailureKind.NoSuchBranch =>
                string.Format(LocalizationManager.Get("Git_ErrNoSuchBranch"), result.Detail),
            GitFailureKind.InvalidBranchName =>
                string.Format(LocalizationManager.Get("Git_ErrInvalidBranch"), result.Detail),
            GitFailureKind.NoCommits => LocalizationManager.Get("Git_ErrNoCommits"),
            GitFailureKind.WorktreeLocked =>
                string.Format(LocalizationManager.Get("Git_ErrWorktreeLocked"), result.Detail),
            GitFailureKind.DirectoryNotEmpty =>
                string.Format(LocalizationManager.Get("Git_ErrDirNotEmpty"), result.Detail),
            GitFailureKind.CheckoutConflict => LocalizationManager.Get("Git_ErrCheckoutConflict"),
            GitFailureKind.NotARepository => LocalizationManager.Format("Git_ErrNotRepository", result.Detail),
            GitFailureKind.NoSuchRevision => LocalizationManager.Format("Git_ErrNoSuchRevision", result.Detail),
            GitFailureKind.InvalidPath => LocalizationManager.Format("Git_ErrInvalidPath", result.Detail),
            GitFailureKind.RepositoryLocked => LocalizationManager.Get("Git_ErrLocked"),
            GitFailureKind.BranchNameConflict => LocalizationManager.Format("Git_ErrBranchNameConflict", result.Detail),
            GitFailureKind.SshUnsupported => SshUnsupported(result.Detail),
            GitFailureKind.Certificate => LocalizationManager.Format("Git_ErrCertificate", result.Detail),
            GitFailureKind.NoPathsInRepository => LocalizationManager.Get("Git_ErrNoPaths"),
            GitFailureKind.EmptyCommitMessage => LocalizationManager.Get("Git_ErrEmptyMessage"),
            GitFailureKind.CurrentBranch => LocalizationManager.Format("Git_ErrCurrentBranch", result.Detail),
            GitFailureKind.NoStash => LocalizationManager.Get("Git_NoStash"),
            GitFailureKind.NoSuchWorktree => LocalizationManager.Format("Git_ErrNoSuchWorktree", result.Detail),
            GitFailureKind.WorktreeNotPruned => LocalizationManager.Format("Git_ErrWorktreeNotPruned", result.Detail),
            // Only a clone is cancelled midway; the dialog says the same words.
            GitFailureKind.Cancelled => LocalizationManager.Get("Git_CloneCancelled"),
            GitFailureKind.InvalidUrl => LocalizationManager.Format("Git_ErrInvalidUrl", result.Detail),
            GitFailureKind.NoTargetDirectory => LocalizationManager.Get("Git_ErrNoTargetDirectory"),
            // A success with a leftover: the reason is the file system's own words, inside our sentence.
            GitFailureKind.DirectoryNotDeleted =>
                LocalizationManager.Format("Git_ErrDirectoryNotDeleted", result.Detail, result.Message),
            // libgit2's own words, but inside a sentence in the user's language rather than bare.
            GitFailureKind.Unexpected => LocalizationManager.Format(
                "Git_ErrUnexpected", result.Detail.Length > 0 ? result.Detail : result.Message),
            _ => result.Message,
        };
    }

    /// <summary>Why an SSH link cannot be cloned, naming its host when it is known. Shared by the
    /// clone dialog, which says it before the user presses Clone.</summary>
    public static string SshUnsupported(string host) =>
        LocalizationManager.Format("Git_ErrSsh", host.Length > 0 ? host : "?");
}
