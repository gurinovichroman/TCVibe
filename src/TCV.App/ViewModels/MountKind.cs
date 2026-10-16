namespace TCV.App.ViewModels;

/// <summary>
/// What a panel has mounted in front of the local disk. Commands branch on this instead of
/// probing the file system's concrete type, so a new plugin file system is not silently
/// treated as an FTP server (or missed as an archive) by code it predates.
/// </summary>
public enum MountKind
{
    /// <summary>Nothing mounted, or a mount that is none of the kinds below.</summary>
    Other,

    /// <summary>An archive browsed as a directory tree.</summary>
    Archive,

    /// <summary>A remote host reached over FTP/FTPS/SFTP.</summary>
    Remote,

    /// <summary>
    /// Search hits listed as a folder (TRAP-28). A mount, yet every hit is a file on the local disk
    /// under its own Windows path; only the list itself ("/") is no folder. Commands that need a
    /// real path for the selection treat it as the disk (<c>FileListViewModel.EntriesOnDisk</c>).
    /// </summary>
    SearchResults,
}
