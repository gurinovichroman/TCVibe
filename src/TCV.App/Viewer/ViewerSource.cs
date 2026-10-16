using System.IO;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Viewer;

/// <summary>
/// Random access to the bytes behind the viewer, so a file of any size can be looked at through a
/// window: the start, the end, an offset. The viewer never holds more than one window in memory.
/// </summary>
public interface IViewerSource : IDisposable
{
    /// <summary>Total size of the file in bytes.</summary>
    long Length { get; }

    /// <summary>
    /// Reads up to <paramref name="count"/> bytes starting at <paramref name="offset"/> (fewer near the
    /// end). <paramref name="progress"/> receives the number of bytes handled so far — reading or, on a
    /// stream that cannot seek, skipping.
    /// </summary>
    Task<byte[]> ReadAsync(long offset, int count, IProgress<long>? progress, CancellationToken ct);
}

/// <summary>
/// A <see cref="IViewerSource"/> over any <see cref="IVirtualFileSystem"/> (INV-02): the disk, an
/// archive entry, a server. Each read opens the file afresh, seeks when the stream can and skips when it
/// cannot (FTP), so nothing is kept open between windows. An archive entry, which would be unpacked
/// again on every open, is read through <see cref="ExtractedViewerSource"/> instead.
/// </summary>
/// <remarks>
/// It can own a <em>lease</em> on a mounted file system (TRAP-05): the viewer window outlives the command
/// that opened it, and navigating the panel away must not dispose the connection under a later jump. The
/// F3 viewer keeps one lease for the whole window instead (<see cref="ViewerSiblings"/>), as N/P replace
/// the source.
/// </remarks>
public sealed class VfsViewerSource : IViewerSource
{
    private const int SkipBuffer = 128 * 1024;

    private readonly IVirtualFileSystem _vfs;
    private readonly VfsPath _path;
    private readonly IDisposable? _lease;

    public VfsViewerSource(IVirtualFileSystem vfs, VfsPath path, long length, IDisposable? lease = null)
    {
        _vfs = vfs;
        _path = path;
        _lease = lease;
        Length = length;
    }

    public long Length { get; }

    public async Task<byte[]> ReadAsync(long offset, int count, IProgress<long>? progress, CancellationToken ct)
    {
        await using var stream = await _vfs.OpenReadAsync(_path, ct).ConfigureAwait(false);

        if (offset > 0)
        {
            if (stream.CanSeek)
            {
                stream.Seek(offset, SeekOrigin.Begin);
            }
            else
            {
                var skipped = 0L;
                var trash = new byte[SkipBuffer];
                while (skipped < offset)
                {
                    var read = await stream.ReadAsync(trash.AsMemory(0, (int)Math.Min(trash.Length, offset - skipped)), ct)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        return [];
                    }

                    skipped += read;
                    progress?.Report(skipped);
                }
            }
        }

        var buffer = new byte[count];
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            progress?.Report(offset + total);
        }

        return total == count ? buffer : buffer[..total];
    }

    public void Dispose() => _lease?.Dispose();
}

/// <summary>
/// A <see cref="IViewerSource"/> over an archive entry, which is unpacked once — on the first read — and
/// every window after that is read from the copy (6.37). <see cref="VfsViewerSource"/> reopens the file
/// for each window, and reopening an archive entry means unpacking it from the beginning again: for the
/// tail, for a block back, for every block a search reads.
/// </summary>
/// <remarks>
/// The archive engines already hand back a seekable copy (memory for a small entry, a temp file that
/// deletes itself for a big one), and that copy is kept. A stream that cannot seek is first copied to a
/// scratch file under <see cref="TemporaryFiles"/>. The copy goes when the source is disposed: when the
/// viewer closes or moves on to another file (N/P). Reads take turns, as two windows must not seek one
/// stream at once; the reading itself runs off the UI thread (INV-06), the unpacking too (the archive
/// file system decodes on a worker).
/// </remarks>
public sealed class ExtractedViewerSource : IViewerSource
{
    private const int CopyBuffer = 128 * 1024;

    private readonly IVirtualFileSystem _vfs;
    private readonly VfsPath _path;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private Stream? _copy;
    private string? _scratchFolder;
    private volatile bool _disposed;

    public ExtractedViewerSource(IVirtualFileSystem vfs, VfsPath path, long length)
    {
        _vfs = vfs;
        _path = path;
        Length = length;
    }

    public long Length { get; }

    public async Task<byte[]> ReadAsync(long offset, int count, IProgress<long>? progress, CancellationToken ct)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _copy ??= await ExtractAsync(progress, ct).ConfigureAwait(false);
            if (offset >= _copy.Length)
            {
                return [];
            }

            _copy.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[(int)Math.Min(count, _copy.Length - offset)];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await _copy.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                progress?.Report(offset + total);
            }

            return total == buffer.Length ? buffer : buffer[..total];
        }
        finally
        {
            _turn.Release();

            // Disposed while this read had its turn: the copy could not go then, so it goes now.
            if (_disposed && _turn.Wait(0))
            {
                try
                {
                    ReleaseCopy();
                }
                finally
                {
                    _turn.Release();
                }
            }
        }
    }

    private async Task<Stream> ExtractAsync(IProgress<long>? progress, CancellationToken ct)
    {
        var stream = await _vfs.OpenReadAsync(_path, ct).ConfigureAwait(false);
        if (stream.CanSeek)
        {
            return stream;
        }

        await using (stream)
        {
            var path = TemporaryFiles.PathFor(_path.FileName);
            _scratchFolder = Path.GetDirectoryName(path);
            var copy = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                CopyBuffer, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            try
            {
                var buffer = new byte[CopyBuffer];
                var copied = 0L;
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await copy.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    progress?.Report(copied);
                }

                return copy;
            }
            catch
            {
                await copy.DisposeAsync().ConfigureAwait(false);
                DeleteScratchFolder();
                throw;
            }
        }
    }

    private void ReleaseCopy()
    {
        _copy?.Dispose();
        _copy = null;
        DeleteScratchFolder();
    }

    private void DeleteScratchFolder()
    {
        if (_scratchFolder is null)
        {
            return;
        }

        try
        {
            Directory.Delete(_scratchFolder, recursive: true);
        }
        catch (IOException)
        {
            // Swept at the next start (TemporaryFiles).
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        _scratchFolder = null;
    }

    /// <summary>Drops the copy — at once, or when a read that has the stream right now is done with it.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (_turn.Wait(0))
        {
            try
            {
                ReleaseCopy();
            }
            finally
            {
                _turn.Release();
            }
        }
    }
}
