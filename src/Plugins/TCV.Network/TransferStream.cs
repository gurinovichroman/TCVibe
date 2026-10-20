using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// A transfer stream that tells its owner, once, when it is closed and whether the transfer ran to its
/// end: a download read to the last byte, an upload neither aborted nor failed, and the server's reply
/// read on close. A connection of its own is then kept for the next transfer
/// (<see cref="TransferConnectionPool{T}"/>); one shared with the panel hands its gate back either way.
/// </summary>
internal sealed class TransferStream(Stream inner, bool download, Action<bool> closed) : Stream, IAbortableWriteStream
{
    private int _closed;
    private bool _reachedEnd;
    private bool _faulted;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public void Abort() => _faulted = true;

    public override void Flush() => Guard(inner.Flush);
    public override Task FlushAsync(CancellationToken ct) => GuardAsync(() => inner.FlushAsync(ct));

    public override int Read(byte[] buffer, int offset, int count) =>
        Counted(Guard(() => inner.Read(buffer, offset, count)), count);

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            return Counted(await inner.ReadAsync(buffer, ct), buffer.Length);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => Guard(() => inner.Write(buffer, offset, count));

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        try
        {
            await inner.WriteAsync(buffer, ct);
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>A read that asked for something and got nothing is the end of the file.</summary>
    private int Counted(int read, int asked)
    {
        if (read == 0 && asked > 0)
        {
            _reachedEnd = true;
        }

        return read;
    }

    private T Guard<T>(Func<T> work)
    {
        try
        {
            return work();
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    private void Guard(Action work) => Guard(() =>
    {
        work();
        return true;
    });

    private async Task GuardAsync(Func<Task> work)
    {
        try
        {
            await work();
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Whether the connection may carry the next transfer: a download was read to the end, an
    /// upload was not cut short. (A data socket reads and writes both ways, so CanRead cannot tell.)
    /// </summary>
    private bool Finished => !_faulted && (!download || _reachedEnd);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            var finished = Finished;
            try
            {
                inner.Dispose();
            }
            catch
            {
                finished = false;
                throw;
            }
            finally
            {
                Report(finished);
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        var finished = Finished;
        try
        {
            await inner.DisposeAsync();
        }
        catch
        {
            finished = false;
            throw;
        }
        finally
        {
            Report(finished);
        }

        await base.DisposeAsync();
    }

    private void Report(bool finished)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            closed(finished);
        }
    }
}
