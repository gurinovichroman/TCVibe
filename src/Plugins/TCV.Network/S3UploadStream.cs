using System.IO;
using System.Security.Cryptography;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// Collects a body in a temporary file and uploads it when committed, or on dispose — unless it was
/// abandoned. S3 signs the SHA-256 of a body and wants its length up front, so nothing can go out
/// before the last byte is in; the hash is taken while writing, so a single PUT costs no second pass.
/// Not in memory: a multi-gigabyte upload must not become a multi-gigabyte array.
/// </summary>
/// <remarks>Internal (not private) only so tests can see that the temporary file goes away.</remarks>
internal sealed class S3UploadStream : Stream, IDeferredUploadStream
{
    private readonly S3FileSystem _owner;
    private readonly string _bucket;
    private readonly string _key;
    private readonly bool _overwrite;
    private readonly VfsPath _path;
    private readonly CancellationToken _ct;
    private readonly FileStream _buffer;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finished;
    private bool _aborted;

    public S3UploadStream(S3FileSystem owner, string bucket, string key, bool overwrite, VfsPath path, CancellationToken ct)
    {
        _owner = owner;
        _bucket = bucket;
        _key = key;
        _overwrite = overwrite;
        _path = path;
        _ct = ct;
        _buffer = new FileStream(
            Path.Combine(Path.GetTempPath(), $"tcv-s3-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    public void Abort() => _aborted = true;

    /// <summary>Where the body is collected; the file is gone once the stream is committed or disposed.</summary>
    internal string BufferPath => _buffer.Name;

    public async Task CommitAsync(IProgress<long>? progress, CancellationToken ct)
    {
        if (_finished)
        {
            throw new InvalidOperationException("The upload was already sent or discarded.");
        }

        _finished = true;
        try
        {
            // Asked for explicitly, so unlike dispose this never reports success without sending.
            if (_aborted)
            {
                throw new InvalidOperationException("An aborted upload cannot be committed.");
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
            linked.Token.ThrowIfCancellationRequested();
            await SendAsync(progress, linked.Token);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _buffer.Length;

    public override long Position
    {
        get => _buffer.Position;
        set => throw new NotSupportedException();
    }

    public override void Flush() => _buffer.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        _buffer.Write(buffer, offset, count);
        _hash.AppendData(buffer, offset, count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _buffer.WriteAsync(buffer, cancellationToken);
        _hash.AppendData(buffer.Span);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Off the caller's synchronisation context: blocking on it from a UI thread would deadlock.
            Task.Run(FinishAsync).GetAwaiter().GetResult();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await FinishAsync();
        await base.DisposeAsync();
    }

    private async Task FinishAsync()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            if (_aborted || _ct.IsCancellationRequested)
            {
                return;
            }

            await SendAsync(progress: null, _ct);
        }
        finally
        {
            await ReleaseAsync();
        }
    }

    private async Task SendAsync(IProgress<long>? progress, CancellationToken ct)
    {
        await _buffer.FlushAsync(ct);
        await _owner.UploadAsync(
            _buffer, Convert.ToHexStringLower(_hash.GetHashAndReset()), _bucket, _key, _overwrite, _path, progress, ct);
    }

    private async Task ReleaseAsync()
    {
        _hash.Dispose();
        await _buffer.DisposeAsync();
    }
}

/// <summary>
/// A read-only window onto part of a file — one part of a multipart upload — that a request body can
/// read and rewind (a region redirect sends it again). Does not own the file.
/// </summary>
internal sealed class FileSegmentStream : Stream
{
    private readonly FileStream _file;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public FileSegmentStream(FileStream file, long start, long length)
    {
        _file = file;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var wanted = (int)Math.Min(buffer.Length, _length - _position);
        if (wanted <= 0)
        {
            return 0;
        }

        _file.Position = _start + _position;
        var read = _file.Read(buffer[..wanted]);
        _position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var wanted = (int)Math.Min(buffer.Length, _length - _position);
        if (wanted <= 0)
        {
            return 0;
        }

        _file.Position = _start + _position;
        var read = await _file.ReadAsync(buffer[..wanted], cancellationToken);
        _position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
