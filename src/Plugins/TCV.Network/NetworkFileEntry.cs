using System.IO;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>IFileEntry implementation for remote (FTP/SFTP) file entries.</summary>
internal sealed record NetworkFileEntry(
    VfsPath Path,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime LastModified) : IFileEntry
{
    public FileAttributes Attributes =>
        IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
}
