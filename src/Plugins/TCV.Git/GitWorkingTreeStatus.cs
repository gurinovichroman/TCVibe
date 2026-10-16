namespace TCV.Git;

/// <summary>
/// One line of the commit dialog. A file modified after being staged legitimately produces two
/// rows — one staged, one not — because that is exactly what git will and will not commit, and
/// collapsing them into one row is how "I staged it, why isn't it in the commit?" happens.
/// </summary>
/// <param name="RelativePath">Path relative to the repository root, forward slashes.</param>
/// <param name="Status">What changed.</param>
/// <param name="IsStaged">True when this change is in the index and would go into the next commit.</param>
/// <param name="OldRelativePath">For <see cref="GitFileStatus.Renamed"/>: the path the file had before,
/// which a revert has to bring back; empty otherwise.</param>
public sealed record GitChangedFile(
    string RelativePath, GitFileStatus Status, bool IsStaged, string OldRelativePath = "")
{
    /// <summary>Single-letter status, matching the panel's Git column and <c>git status --short</c> (U = unmerged).</summary>
    public string StatusLabel => Status switch
    {
        GitFileStatus.Untracked => "?",
        GitFileStatus.Added => "A",
        GitFileStatus.Modified => "M",
        GitFileStatus.Deleted => "D",
        GitFileStatus.Renamed => "R",
        GitFileStatus.Conflict => "U",
        _ => " ",
    };
}

/// <summary>
/// Snapshot of what a repository is about to commit. Captured in one pass on a background thread so
/// the dialog binds to plain values and never opens a repository of its own.
/// </summary>
/// <param name="RepositoryRoot">Working directory of the repository, or empty when there is none.</param>
/// <param name="Branch">Current branch, or a short commit id when HEAD is detached.</param>
/// <param name="Files">Changed entries, staged ones first.</param>
public sealed record GitWorkingTreeStatus(
    string RepositoryRoot,
    string Branch,
    IReadOnlyList<GitChangedFile> Files)
{
    public static GitWorkingTreeStatus Empty { get; } =
        new(string.Empty, string.Empty, Array.Empty<GitChangedFile>());

    public bool IsRepository => RepositoryRoot.Length > 0;

    public IEnumerable<GitChangedFile> Staged => Files.Where(f => f.IsStaged);
    public IEnumerable<GitChangedFile> Unstaged => Files.Where(f => !f.IsStaged);

    /// <summary>True when there is something in the index — the precondition for a commit.</summary>
    public bool HasStagedChanges => Files.Any(f => f.IsStaged);

    /// <summary>Changed paths, a file staged and modified again counted once — «8 changes» in the status bar.</summary>
    public int ChangedPathCount => Files.Select(f => f.RelativePath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

/// <summary>Lines added and removed by a set of changes («+304 −65»).</summary>
public sealed record GitLineStats(int Added, int Removed)
{
    public static GitLineStats None { get; } = new(0, 0);
}
