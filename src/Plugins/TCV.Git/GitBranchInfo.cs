namespace TCV.Git;

/// <summary>One local branch as the branch dialog shows it.</summary>
/// <param name="Name">Short branch name.</param>
/// <param name="IsCurrent">Checked out in the checkout the dialog was opened from.</param>
/// <param name="CheckedOutAt">Working directory of another worktree that has it checked out, or null.
/// Such a branch cannot be switched to here — git allows one checkout per branch.</param>
public sealed record GitBranchInfo(string Name, bool IsCurrent, string? CheckedOutAt)
{
    public bool IsBlocked => CheckedOutAt is not null;
}

/// <summary>The most recent stash entry, so "pop" can say what it is about to apply.</summary>
/// <param name="Count">Number of entries in the (repository-wide) stash.</param>
/// <param name="TopMessage">Message of entry 0, e.g. <c>On feature: wip</c>; empty when there is none.</param>
public sealed record GitStashInfo(int Count, string TopMessage);
