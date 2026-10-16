using TCV.Core.Domain;

namespace TCV.Core.Interfaces;

public interface IVirtualFileSystem : IDisposable
{
    string Scheme { get; }
    string DisplayName { get; }

    Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default);
    Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default);
    Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default);
    Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default);
    Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct);
    Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct);
    Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct);
    Task CreateDirectoryAsync(VfsPath path, CancellationToken ct);
    Task RenameAsync(VfsPath path, string newName, CancellationToken ct);
    bool SupportsOperation(VfsOperation operation);

    event EventHandler<VfsChangedEventArgs>? Changed;
}
