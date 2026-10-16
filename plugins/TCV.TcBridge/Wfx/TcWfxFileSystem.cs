using System.IO;
using TCV.Core.Domain;
using TCV.Core.Exceptions;
using TCV.Core.Interfaces;

namespace TCV.TcBridge.Wfx;

/// <summary>
/// <see cref="IVirtualFileSystem"/> implementation backed by a Total Commander WFX plugin DLL.
/// Paths inside the VFS use forward slashes (VfsPath convention); they are converted to
/// backslash before being passed to the native WFX API.
/// </summary>
internal sealed class TcWfxFileSystem : IVirtualFileSystem
{
    private readonly TcWfxAdapter _adapter;
    private readonly string _tempDir;
    private bool _disposed;

    public string Scheme      { get; }
    public string DisplayName { get; }

    /// <inheritdoc/>
    public event EventHandler<VfsChangedEventArgs>? Changed;

    public TcWfxFileSystem(TcWfxAdapter adapter, string scheme, string displayName)
    {
        _adapter     = adapter;
        Scheme       = scheme;
        DisplayName  = displayName;
        _tempDir     = Path.Combine(Path.GetTempPath(), "TCV.TcBridge", scheme);
        Directory.CreateDirectory(_tempDir);
    }

    // -------------------------------------------------------------------------
    // IVirtualFileSystem
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string wfxPath = ToWfxPath(path);
        List<WfxEntry> native = _adapter.EnumeratePath(wfxPath);

        IReadOnlyList<IFileEntry> entries = native
            .Select(e => (IFileEntry)new FileEntry(
                path:         path.Combine(e.Name),
                name:         e.Name,
                isDirectory:  e.IsDirectory,
                size:         e.Size,
                lastModified: e.LastModified,
                attributes:   e.Attributes))
            .ToList();

        return Task.FromResult(entries);
    }

    public async Task<IFileEntry> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var parent = path.Parent ?? VfsPath.Root;
        var entries = await ListAsync(parent, ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.Name, path.FileName, StringComparison.OrdinalIgnoreCase));

        if (entry is null)
            throw new VfsException($"Entry not found: {path}", path);
        return entry;
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_adapter.CanRead)
            throw new VfsException($"WFX plugin does not support reading files.", path);

        string localFile = GetTempFilePath(path);
        string wfxPath   = ToWfxPath(path);

        int rc = _adapter.GetFile(wfxPath, localFile, WfxNative.FS_COPYFLAGS_OVERWRITE);
        if (rc != WfxNative.FS_FILE_OK)
            throw new VfsException($"FsGetFile returned error {rc} for '{wfxPath}'.", path);

        // Return a DeleteOnClose stream so temp files are cleaned up automatically.
        return new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!_adapter.CanWrite)
            throw new VfsException($"WFX plugin does not support writing files.", path);

        string localFile = GetTempFilePath(path);

        // We return a wrapper stream that uploads to the plugin on Close/Dispose.
        Stream inner = new FileStream(localFile, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);

        return Task.FromResult<Stream>(new WfxUploadStream(inner, localFile, path, this));
    }

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string src  = ToWfxPath(source);
        string dest = ToWfxPath(destination);
        int rc = _adapter.RenMovFile(src, dest, move: false, overwrite: true);
        if (rc != WfxNative.FS_FILE_OK)
            throw new VfsException($"FsRenMovFile (copy) returned error {rc}.", source);
        RaiseChanged(destination, System.IO.WatcherChangeTypes.Created);
        return Task.CompletedTask;
    }

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string src  = ToWfxPath(source);
        string dest = ToWfxPath(destination);
        int rc = _adapter.RenMovFile(src, dest, move: true, overwrite: false);
        if (rc != WfxNative.FS_FILE_OK)
            throw new VfsException($"FsRenMovFile (move) returned error {rc}.", source);
        RaiseChanged(source, System.IO.WatcherChangeTypes.Deleted);
        RaiseChanged(destination, System.IO.WatcherChangeTypes.Created);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string wfxPath = ToWfxPath(path);

        bool ok = _adapter.DeleteFile(wfxPath);
        if (!ok && _adapter.CanRemoveDir)
            ok = _adapter.RemoveDir(wfxPath);

        if (!ok)
            throw new VfsException($"Failed to delete '{wfxPath}'.", path);

        RaiseChanged(path, System.IO.WatcherChangeTypes.Deleted);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string wfxPath = ToWfxPath(path);
        if (!_adapter.MkDir(wfxPath))
            throw new VfsException($"FsMkDir failed for '{wfxPath}'.", path);
        RaiseChanged(path, System.IO.WatcherChangeTypes.Created);
        return Task.CompletedTask;
    }

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var parent    = path.Parent ?? VfsPath.Root;
        var newPath   = parent.Combine(newName);
        string src    = ToWfxPath(path);
        string dest   = ToWfxPath(newPath);
        int rc = _adapter.RenMovFile(src, dest, move: true, overwrite: false);
        if (rc != WfxNative.FS_FILE_OK)
            throw new VfsException($"FsRenMovFile (rename) returned error {rc}.", path);
        RaiseChanged(path, System.IO.WatcherChangeTypes.Renamed);
        return Task.CompletedTask;
    }

    public bool SupportsOperation(VfsOperation operation) => operation switch
    {
        VfsOperation.Read            => _adapter.CanRead,
        VfsOperation.Write           => _adapter.CanWrite,
        VfsOperation.Delete          => _adapter.CanDelete || _adapter.CanRemoveDir,
        VfsOperation.Rename          => _adapter.CanRename,
        VfsOperation.CreateDirectory => _adapter.CanMkDir,
        VfsOperation.Copy            => _adapter.CanRename,  // reused RenMovFile with move=false
        VfsOperation.Move            => _adapter.CanRename,
        _                            => false,
    };

    // -------------------------------------------------------------------------
    // Internal: used by WfxUploadStream to push buffered data to the plugin
    // -------------------------------------------------------------------------

    internal void UploadTempFile(string localFile, VfsPath remotePath)
    {
        string wfxPath = ToWfxPath(remotePath);
        int rc = _adapter.PutFile(localFile, wfxPath, WfxNative.FS_COPYFLAGS_OVERWRITE);
        if (rc != WfxNative.FS_FILE_OK)
            throw new VfsException($"FsPutFile returned error {rc} for '{wfxPath}'.", remotePath);
        RaiseChanged(remotePath, System.IO.WatcherChangeTypes.Changed);
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _adapter.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Converts a forward-slash VfsPath to a WFX backslash path.</summary>
    private static string ToWfxPath(VfsPath path) =>
        path.Value.Replace('/', '\\');

    private string GetTempFilePath(VfsPath path)
    {
        string safeName = string.Concat(
            System.IO.Path.GetInvalidFileNameChars()
                          .Aggregate(path.FileName,
                                     (s, c) => s.Replace(c, '_')));
        return Path.Combine(_tempDir, safeName);
    }

    private void RaiseChanged(VfsPath path, System.IO.WatcherChangeTypes changeType)
        => Changed?.Invoke(this, new VfsChangedEventArgs(path, changeType));
}

// ---------------------------------------------------------------------------
// Helper stream that uploads local temp data to the WFX plugin on close.
// ---------------------------------------------------------------------------

internal sealed class WfxUploadStream : Stream
{
    private readonly Stream            _inner;
    private readonly string            _localFile;
    private readonly VfsPath           _remotePath;
    private readonly TcWfxFileSystem   _fs;
    private bool _uploaded;

    public WfxUploadStream(Stream inner, string localFile, VfsPath remotePath, TcWfxFileSystem fs)
    {
        _inner      = inner;
        _localFile  = localFile;
        _remotePath = remotePath;
        _fs         = fs;
    }

    public override bool CanRead  => false;
    public override bool CanSeek  => false;
    public override bool CanWrite => true;
    public override long Length   => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => _inner.WriteAsync(buffer, offset, count, ct);
    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin)      => throw new NotSupportedException();
    public override void SetLength(long value)                      => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_uploaded)
        {
            _uploaded = true;
            _inner.Flush();
            _inner.Dispose();
            _fs.UploadTempFile(_localFile, _remotePath);
            try { File.Delete(_localFile); } catch { /* best effort */ }
        }
        base.Dispose(disposing);
    }
}
