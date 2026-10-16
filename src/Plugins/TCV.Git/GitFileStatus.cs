namespace TCV.Git;

/// <summary>Git status of a file in a working tree.</summary>
public enum GitFileStatus
{
    Untracked,
    Added,      // staged new file
    Modified,   // working-tree changes (staged or unstaged)
    Deleted,
    Renamed,
    Ignored,
    Conflict,
    Unmodified,
}
