using TCV.Core.Interfaces;

namespace TCV.Network.Cloud;

/// <summary>
/// A write stream that collects its body in a temporary file and uploads it when committed or
/// disposed — the same contract as WebDAV's upload stream. Every provider wants the size up front
/// (or uploads in sessions that need it), and a multi-gigabyte upload must not become a
/// multi-gigabyte array. A body that was abandoned, or whose copy was cancelled, is never sent.
/// </summary>
/// <remarks>Internal (not private) so tests can see that the temporary file goes away.</remarks>
internal sealed class CloudUploadStream : Stream, IDeferredUploadStream
{
    private readonly Func<FileStream, IProgress<long>?, CancellationToken, Task> _send;
    private readonly CancellationToken _ct;
    private readonly FileStream _buffer;
    private bool _finished;
    private bool _aborted;

    /// <param name="send">Uploads the collected body; the stream is positioned anywhere, the sender seeks.</param>
    public CloudUploadStream(Func<FileStream, IProgress<long>?, CancellationToken, Task> send, CancellationToken ct)
    {
        _send = send;
        _ct = ct;
        _buffer = new FileStream(
            Path.Combine(Path.GetTempPath(), $"tcv-cloud-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);
    }

    /// <summary>Where the body is collected; the file is gone once the stream is committed or disposed.</summary>
    internal string BufferPath => _buffer.Name;

    public void Abort() => _aborted = true;

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
            await _buffer.FlushAsync(linked.Token);
            await _send(_buffer, progress, linked.Token);
        }
        finally
        {
            await _buffer.DisposeAsync();
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
    public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
        _buffer.WriteAsync(buffer, cancellationToken);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _buffer.WriteAsync(buffer, offset, count, cancellationToken);

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

            await _buffer.FlushAsync(_ct);
            await _send(_buffer, null, _ct);
        }
        finally
        {
            await _buffer.DisposeAsync();
        }
    }
}
