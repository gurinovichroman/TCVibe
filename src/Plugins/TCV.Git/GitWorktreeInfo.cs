namespace TCV.Git;

/// <summary>
/// One row of the worktree dashboard: where it is, what it is on, and whether it is safe to leave
/// or remove. Everything is captured at list time — the dialog must not hold a live repository
/// handle while the user reads it (a worktree cannot be removed while something has it open).
/// </summary>
/// <param name="Name">Worktree name as git knows it; the main checkout has none and uses its folder.</param>
/// <param name="Path">Absolute working directory.</param>
/// <param name="Branch">Checked-out branch, or a short commit id when HEAD is detached.</param>
/// <param name="IsMain">True for the repository's own checkout, which cannot be removed.</param>
/// <param name="IsCurrent">True when the panel that opened the dashboard is inside this worktree.</param>
/// <param name="IsLocked">Git-level lock: removal needs force.</param>
/// <param name="Missing">Registered with git, but its directory is gone — removing prunes the record.</param>
/// <param name="DirtyCount">Changed/untracked entries; 0 means removing loses nothing;
/// <see cref="UnknownDirtyCount"/> means the status could not be read.</param>
/// <param name="AheadBy">Commits ahead of upstream.</param>
/// <param name="BehindBy">Commits behind upstream.</param>
/// <param name="HasUpstream">False when the branch tracks nothing — then ahead/behind mean nothing either.</param>
public sealed record GitWorktreeInfo(
    string Name,
    string Path,
    string Branch,
    bool IsMain,
    bool IsCurrent,
    bool IsLocked,
    bool Missing,
    int DirtyCount,
    int AheadBy,
    int BehindBy,
    bool HasUpstream)
{
    /// <summary><see cref="DirtyCount"/> when the status walk failed. Treated as dirty: not knowing
    /// is no reason to skip the data-loss warning.</summary>
    public const int UnknownDirtyCount = -1;

    /// <summary>True when the worktree has (or may have) uncommitted work — the one thing removal
    /// would destroy.</summary>
    public bool IsDirty => DirtyCount != 0;

    /// <summary>The change count as shown to people: a number, or "?" when it is unknown.</summary>
    public string DirtyCountLabel =>
        DirtyCount == UnknownDirtyCount ? "?" : DirtyCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>
    /// Compact "↑2 ↓1" for the list. Empty when the branch tracks nothing, because "0/0" there reads
    /// as "in sync with the remote" when the truth is that there is no remote to be in sync with.
    /// </summary>
    public string AheadBehindLabel
    {
        get
        {
            if (!HasUpstream) return string.Empty;
            if (AheadBy == 0 && BehindBy == 0) return "=";

            var ahead = AheadBy > 0 ? $"↑{AheadBy}" : string.Empty;
            var behind = BehindBy > 0 ? $"↓{BehindBy}" : string.Empty;
            return string.Join(" ", new[] { ahead, behind }.Where(s => s.Length > 0));
        }
    }

    /// <summary>The first thing that is true about the row, worst first. The UI localises it.</summary>
    public GitWorktreeState State => Missing
        ? GitWorktreeState.Missing
        : IsLocked ? GitWorktreeState.Locked
        : IsDirty ? GitWorktreeState.Dirty
        : GitWorktreeState.Clean;

    /// <summary>English text for logs and tests; the dashboard shows a localised label for <see cref="State"/>.</summary>
    public string StateLabel => State switch
    {
        GitWorktreeState.Missing => "missing",
        GitWorktreeState.Locked => "locked",
        GitWorktreeState.Dirty => $"{DirtyCountLabel}±",
        _ => "clean",
    };
}

/// <summary>Coarse condition of a worktree row.</summary>
public enum GitWorktreeState
{
    Clean,
    Dirty,
    Locked,
    Missing,
}
