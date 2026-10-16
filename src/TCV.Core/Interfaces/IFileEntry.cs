using System.IO;
using TCV.Core.Domain;

namespace TCV.Core.Interfaces;

public interface IFileEntry
{
    VfsPath Path { get; }
    string Name { get; }
    bool IsDirectory { get; }
    long Size { get; }
    DateTime LastModified { get; }
    FileAttributes Attributes { get; }
}
