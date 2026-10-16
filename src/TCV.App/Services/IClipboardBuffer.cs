using TCV.Core.Interfaces;

namespace TCV.App.Services;

/// <summary>Clipboard operation mode.</summary>
public enum ClipboardOperation
{
    None,
    Copy,
    Cut
}

/// <summary>Represents a file entry in the clipboard buffer.</summary>
public record ClipboardEntry(
    string SourcePath,      // Full source path (Windows path or archive path like "zip://C:\file.zip/folder/file.txt")
    string DisplayName,     // Display name for UI
    bool IsDirectory,
    long Size,
    DateTime LastModified,
    string? ArchivePath = null  // Parent archive path if inside archive (e.g. "C:\file.zip")
);

/// <summary>Service for managing file clipboard operations (copy/cut/paste).</summary>
public interface IClipboardBuffer
{
    /// <summary>Gets the current operation mode.</summary>
    ClipboardOperation Operation { get; }

    /// <summary>Gets the list of entries in the buffer.</summary>
    IReadOnlyList<ClipboardEntry> Entries { get; }

    /// <summary>
    /// The Windows clipboard sequence number at the moment the buffer was filled (0 = not recorded).
    /// The buffer mirrors what was put on the system clipboard; if that number has moved on, another
    /// application has copied something since and the buffer is stale.
    /// </summary>
    long Stamp { get; set; }

    /// <summary>True when the buffer holds entries but the system clipboard changed after they were set.</summary>
    bool IsStale(long currentSystemStamp);

    /// <summary>Gets whether the buffer has entries.</summary>
    bool HasEntries { get; }

    /// <summary>Sets entries for copy operation.</summary>
    void SetCopy(IReadOnlyList<ClipboardEntry> entries);

    /// <summary>Sets entries for cut operation.</summary>
    void SetCut(IReadOnlyList<ClipboardEntry> entries);

    /// <summary>Clears the clipboard buffer.</summary>
    void Clear();

    /// <summary>Gets entries to delete (for cut operation after paste).</summary>
    IReadOnlyList<ClipboardEntry> GetEntriesToDelete();
}
