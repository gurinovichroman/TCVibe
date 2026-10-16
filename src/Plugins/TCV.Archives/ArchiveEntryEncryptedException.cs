namespace TCV.Archives;

/// <summary>
/// Thrown when an edit would have to rewrite an encrypted entry. Renaming a ZIP entry in process stores
/// it again under the new name, and the BCL can neither decrypt the entry nor encrypt it back.
/// </summary>
public sealed class ArchiveEntryEncryptedException : NotSupportedException
{
    public string EntryName { get; }

    public ArchiveEntryEncryptedException(string entryName)
        : base($"Entry '{entryName}' is encrypted and cannot be rewritten inside the archive.")
    {
        EntryName = entryName;
    }
}
