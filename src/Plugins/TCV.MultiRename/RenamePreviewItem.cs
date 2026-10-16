namespace TCV.MultiRename;

/// <summary>Why a proposed name cannot be used. Only the first problem found is reported.</summary>
public enum ConflictKind
{
    None,

    /// <summary>Another file in the batch gets the same new name.</summary>
    Duplicate,

    /// <summary>The new name is empty.</summary>
    Empty,

    /// <summary>The name has characters Windows forbids, ends in a dot or space, or is a reserved device name.</summary>
    InvalidName,

    /// <summary>The name is longer than a file name may be.</summary>
    TooLong,

    /// <summary>A file with that name is already in the folder and is not part of this batch.</summary>
    ExistsInFolder,
}

/// <summary>One row in the rename preview — old name, proposed new name, and whether it conflicts.</summary>
public sealed record RenamePreviewItem(string OldName, string NewName, bool HasConflict, ConflictKind Kind = ConflictKind.None);
