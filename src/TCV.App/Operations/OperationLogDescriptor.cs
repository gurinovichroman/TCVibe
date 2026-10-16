using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>The kind of file operation recorded in the user-facing operation log.</summary>
public enum OperationKind
{
    Copy,
    Move,
    Delete,
    CreateFolder,
    Rename,
    /// <summary>Extracting entries out of an archive onto the disk.</summary>
    Extract,
    /// <summary>Adding files to an archive.</summary>
    Pack,
    /// <summary>A new empty file (Shift+F4).</summary>
    CreateFile,
    /// <summary>A file fetched from a server by the background transfer queue (Shift+F5; <see cref="RemoteTransferLog"/>).</summary>
    Download,
    /// <summary>A file sent to a server: by the transfer queue, or an F4 edit written back (<see cref="RemoteTransferLog"/>).</summary>
    Upload,
}

/// <summary>
/// The user-facing summary an operation exposes so <see cref="OperationQueue"/> can write a log
/// entry once the operation finishes — without the queue needing to know each operation's fields.
/// The outcome and timestamp are added by the queue; this is only the "what was attempted" part.
/// </summary>
public sealed record OperationLogDescriptor(
    OperationKind Kind,
    string Source,
    string? Destination,
    int ItemCount)
{
    /// <summary>Destination value that stands for "the Recycle Bin"; the log dialog localises it.</summary>
    public const string RecycleBinToken = "recycle-bin";

    /// <summary>
    /// The renames a batch performed, for a log line that can be taken back from the log
    /// (<see cref="RenameOperation"/>); null for every other operation.
    /// </summary>
    public RenameBatchRecord? Renames { get; init; }

    /// <summary>
    /// The folder the operation put new items into, said outright; null when nothing arrived anywhere
    /// new (a delete, a rename, a write into an existing archive). <see cref="Destination"/> cannot say
    /// it: it is the folder for a batch but the item itself for one that arrived under another name
    /// (F5 to «D:\x\new.txt», a transfer renamed on arrival), and an archive for a pack. The name index
    /// rescans this folder after the operation (TRAP-62).
    /// </summary>
    public string? ArrivedIn { get; init; }

    /// <summary>
    /// A concise source description: the single item's full path, or the containing directory
    /// with the object count when several were selected.
    /// </summary>
    public static string DescribeEntries(IReadOnlyList<IFileEntry> entries)
    {
        if (entries.Count == 0) return string.Empty;
        if (entries.Count == 1) return entries[0].Path.Value;
        var parent = entries[0].Path.Parent?.Value ?? string.Empty;
        return $"{parent} ({entries.Count})";
    }
}
