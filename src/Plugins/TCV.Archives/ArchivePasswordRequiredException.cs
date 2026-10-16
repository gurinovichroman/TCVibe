namespace TCV.Archives;

/// <summary>Thrown when an archive requires a password to open or extract.</summary>
public sealed class ArchivePasswordRequiredException : Exception
{
    public string ArchivePath { get; }

    public ArchivePasswordRequiredException(string archivePath, Exception? inner = null)
        : base($"Archive '{Path.GetFileName(archivePath)}' is password-protected.", inner)
    {
        ArchivePath = archivePath;
    }
}
