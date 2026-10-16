using LibGit2Sharp;

namespace TCV.Git;

/// <summary>Turns libgit2's flag soup into the two-sided (index / working tree) rows the UI shows.</summary>
internal static class GitStatusMapper
{
    /// <summary>One row per side that has a change: a file staged and then edited yields two.</summary>
    public static IEnumerable<GitChangedFile> ToChangedFiles(StatusEntry entry)
    {
        if (entry.State is FileStatus.Unaltered or FileStatus.Ignored) yield break;

        var path = entry.FilePath.Replace('\\', '/');
        if (StagedStatus(entry.State) is { } staged)
            yield return new GitChangedFile(path, staged, IsStaged: true,
                OldPath(staged, entry.HeadToIndexRenameDetails?.OldFilePath));
        if (UnstagedStatus(entry.State) is { } unstaged)
            yield return new GitChangedFile(path, unstaged, IsStaged: false,
                OldPath(unstaged, entry.IndexToWorkDirRenameDetails?.OldFilePath));
    }

    /// <summary>The pre-rename path of a renamed row, forward slashes; empty for any other status.</summary>
    private static string OldPath(GitFileStatus status, string? oldPath) =>
        status == GitFileStatus.Renamed && !string.IsNullOrEmpty(oldPath)
            ? oldPath.Replace('\\', '/')
            : string.Empty;

    /// <summary>Index-side change, or null when the entry has none.</summary>
    public static GitFileStatus? StagedStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.NewInIndex)) return GitFileStatus.Added;
        if (state.HasFlag(FileStatus.ModifiedInIndex)) return GitFileStatus.Modified;
        if (state.HasFlag(FileStatus.DeletedFromIndex)) return GitFileStatus.Deleted;
        if (state.HasFlag(FileStatus.RenamedInIndex)) return GitFileStatus.Renamed;
        if (state.HasFlag(FileStatus.TypeChangeInIndex)) return GitFileStatus.Modified;
        return null;
    }

    /// <summary>Working-tree change, or null when the entry has none.</summary>
    public static GitFileStatus? UnstagedStatus(FileStatus state)
    {
        if (state.HasFlag(FileStatus.Conflicted)) return GitFileStatus.Conflict;
        if (state.HasFlag(FileStatus.NewInWorkdir)) return GitFileStatus.Untracked;
        if (state.HasFlag(FileStatus.ModifiedInWorkdir)) return GitFileStatus.Modified;
        if (state.HasFlag(FileStatus.DeletedFromWorkdir)) return GitFileStatus.Deleted;
        if (state.HasFlag(FileStatus.RenamedInWorkdir)) return GitFileStatus.Renamed;
        if (state.HasFlag(FileStatus.TypeChangeInWorkdir)) return GitFileStatus.Modified;
        return null;
    }
}
