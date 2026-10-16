using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// A connection opened read-only (<see cref="ConnectionInfo.ReadOnly"/>): browsing, viewing and
/// copying from it work, anything that would change the server is refused. Wrapping the file system
/// instead of checking in each command covers every writer at once — F4 upload, a sync, an agent.
/// </summary>
public sealed class ReadOnlyFileSystem : IVirtualFileSystem, IRangeReader, IKeepAlive
{
    private readonly IVirtualFileSystem _inner;

    public ReadOnlyFileSystem(IVirtualFileSystem inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The file system this one reads through; the owner disposes it with this one.</summary>
    public IVirtualFileSystem Inner => _inner;

    public string Scheme => _inner.Scheme;

    public string DisplayName => _inner.DisplayName;

    public event EventHandler<VfsChangedEventArgs>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) =>
        _inner.ListAsync(path, ct);

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) =>
        _inner.GetEntryAsync(path, ct);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
        _inner.OpenReadAsync(path, ct);

    public Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct) =>
        _inner is IRangeReader ranged
            ? ranged.OpenReadFromAsync(path, offset, validator, ct)
            : WholeFileAsync(path, ct);

    private async Task<(Stream, long, long, string?)> WholeFileAsync(VfsPath path, CancellationToken ct) =>
        (await _inner.OpenReadAsync(path, ct), 0, 0, null);

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
        throw Refused();

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
        throw Refused();

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw Refused();

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw Refused();

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw Refused();

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => throw Refused();

    public bool SupportsOperation(VfsOperation operation) =>
        operation == VfsOperation.Read && _inner.SupportsOperation(operation);

    /// <summary>A read-only connection is still a connection a server can drop when it idles.</summary>
    public Task KeepAliveAsync(CancellationToken ct) =>
        _inner is IKeepAlive keepAlive ? keepAlive.KeepAliveAsync(ct) : Task.CompletedTask;

    public void Dispose() => _inner.Dispose();

    private static ReadOnlyConnectionException Refused() => new();
}

/// <summary>A write was attempted on a connection opened read-only.</summary>
public sealed class ReadOnlyConnectionException()
    : UnauthorizedAccessException("The connection is read-only: nothing on it can be changed.");
