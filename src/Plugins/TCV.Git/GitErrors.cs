using LibGit2Sharp;

namespace TCV.Git;

/// <summary>
/// Turns what LibGit2Sharp throws into a <see cref="GitOperationResult"/> with a
/// <see cref="GitFailureKind"/>, so the UI can say it in the user's language instead of printing
/// libgit2's English. <see cref="GitCloneErrors"/> does the same for the text a failed clone leaves.
/// </summary>
public static class GitErrors
{
    /// <summary>
    /// Classifies <paramref name="exception"/>. What has no kind of its own becomes
    /// <see cref="GitFailureKind.Unexpected"/> with the raw text as the detail: shown inside a
    /// localised sentence, never bare, and never dropped — it is the only clue there is.
    /// </summary>
    /// <param name="exception">What the git call threw.</param>
    /// <param name="path">The path the operation was about, for the kinds whose detail is a path.</param>
    public static GitOperationResult FromException(Exception exception, string path = "")
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        return exception switch
        {
            // GIT_ELOCKED: index.lock or a ref's .lock — an IDE or a terminal git is mid-operation.
            LockedFileException => GitOperationResult.Fail(message, GitFailureKind.RepositoryLocked),
            CheckoutConflictException => GitOperationResult.Fail(message, GitFailureKind.CheckoutConflict),
            UnbornBranchException => GitOperationResult.Fail(message, GitFailureKind.NoCommits),
            RepositoryNotFoundException => GitOperationResult.Fail(message, GitFailureKind.NotARepository, path),
            _ => GitOperationResult.Fail(message, GitFailureKind.Unexpected, message),
        };
    }
}
