using System.IO;

namespace TCV.Core.Domain;

public sealed class FileEntry : Interfaces.IFileEntry
{
    public FileEntry(
        VfsPath path,
        string name,
        bool isDirectory,
        long size,
        DateTime lastModified,
        FileAttributes attributes)
    {
        Path = path;
        Name = name;
        IsDirectory = isDirectory;
        Size = size;
        LastModified = lastModified;
        Attributes = attributes;
    }

    public VfsPath Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public long Size { get; }
    public DateTime LastModified { get; }
    public FileAttributes Attributes { get; }
}
