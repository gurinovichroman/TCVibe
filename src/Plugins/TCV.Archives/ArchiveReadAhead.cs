using Microsoft.Win32.SafeHandles;

namespace TCV.Archives;

/// <summary>
/// One pass over announced entries of an archive (<see cref="TCV.Core.Interfaces.IReadAheadFileSystem"/>):
/// the pass writes each entry to a single scratch file as it comes by, and a reader waits for its entry
/// and reads that piece of the file. Going entry by entry, a solid 7z or RAR is decoded from its start for
/// every file, which makes a synchronisation quadratic in the size of the archive (6.42).
/// </summary>
/// <remarks>
/// The readers ask in their order and the archive answers in its own, so entries wait on the disk until
/// asked for. The scratch file goes away with its handle, also when the process is killed: decoded content
/// of an encrypted archive does not linger. A pass that fails or stops only ends the service: what it did
/// not bring is read from the archive by the caller, and that read reports what is wrong with that entry.
/// </remarks>
internal sealed class ArchiveReadAhead : IDisposable
{
    /// <summary>What announcing gives back when there is nothing to gain.</summary>
    public static readonly IDisposable Nothing = new NoReadAhead();

    private const int BufferSize = 1024 * 1024;

    private readonly Dictionary<string, TaskCompletionSource<Piece?>> _entries;
    private readonly Action<Action<string, Stream>, CancellationToken> _readPass;
    private readonly Action<ArchiveReadAhead> _onDisposed;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private SafeFileHandle? _scratch;
    private bool _started;
    private bool _disposed;

    // This object, the pass while it runs, and each stream still open: the scratch file outlives them all.
    private int _holders = 1;

    /// <param name="paths">The entries, under the names <paramref name="readPass"/> hands them over by.</param>
    /// <param name="readPass">Reads the entries once, handing each to the given consumer.</param>
    /// <param name="onDisposed">Called once when the owner lets go.</param>
    public ArchiveReadAhead(
        IReadOnlyList<string> paths,
        Action<Action<string, Stream>, CancellationToken> readPass,
        Action<ArchiveReadAhead> onDisposed)
    {
        _entries = paths.ToDictionary(
            p => p, _ => new TaskCompletionSource<Piece?>(TaskCreationOptions.RunContinuationsAsynchronously), StringComparer.Ordinal);
        _readPass = readPass;
        _onDisposed = onDisposed;
    }

    public bool Covers(string path) => _entries.ContainsKey(path);

    /// <summary>
    /// The entry's content, once the pass has reached it; null when the pass ended without it. Starts the
    /// pass if this is the first read.
    /// </summary>
    public async Task<Stream?> TryOpenAsync(string path, CancellationToken ct)
    {
        Start();
        var piece = await _entries[path].Task.WaitAsync(ct).ConfigureAwait(false);
        if (piece is not { } found || !TryHold())
        {
            return null;
        }

        return new PieceStream(this, _scratch!, found);
    }

    private void Start()
    {
        lock (_gate)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
            _holders++;
        }

        _ = Task.Run(RunPass);
    }

    private void RunPass()
    {
        try
        {
            _scratch = File.OpenHandle(
                Path.Combine(Path.GetTempPath(), $"TCV_readahead_{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);

            var written = 0L;
            var buffer = new byte[BufferSize];
            _readPass((path, content) =>
            {
                var start = written;
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    // A big entry decodes for long between two calls of the pass: Cancel must not wait for it.
                    _stop.Token.ThrowIfCancellationRequested();
                    RandomAccess.Write(_scratch, buffer.AsSpan(0, read), written);
                    written += read;
                }

                if (_entries.TryGetValue(path, out var entry))
                {
                    entry.TrySetResult(new Piece(start, written - start));
                }
            }, _stop.Token);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Cancelled, a wrong password, a full disk, a damaged archive: see the remarks above.
        }
        finally
        {
            EndWaits();
            Release();
        }
    }

    private void EndWaits()
    {
        foreach (var entry in _entries.Values)
        {
            entry.TrySetResult(null);
        }
    }

    private bool TryHold()
    {
        lock (_gate)
        {
            if (_holders == 0 || _disposed)
            {
                return false;
            }

            _holders++;
            return true;
        }
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holders > 0)
            {
                return;
            }
        }

        _scratch?.Dispose();
        _stop.Dispose();
    }

    public void Dispose()
    {
        bool started;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            started = _started;
        }

        _onDisposed(this);
        _stop.Cancel();
        if (!started)
        {
            EndWaits();
        }

        Release();
    }

    private readonly record struct Piece(long Offset, long Length);

    private sealed class NoReadAhead : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>One entry of the scratch file. Reads do not move a shared position, so readers may overlap.</summary>
    private sealed class PieceStream(ArchiveReadAhead owner, SafeFileHandle scratch, Piece piece) : Stream
    {
        private long _position;
        private bool _closed;

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => piece.Length;

        public override long Position
        {
            get => _position;
            set => _position = value;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var wanted = Clamp(buffer.Length);
            if (wanted == 0)
            {
                return 0;
            }

            var read = RandomAccess.Read(scratch, buffer[..wanted], piece.Offset + _position);
            _position += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var wanted = Clamp(buffer.Length);
            if (wanted == 0)
            {
                return 0;
            }

            var read = await RandomAccess.ReadAsync(scratch, buffer[..wanted], piece.Offset + _position, cancellationToken)
                .ConfigureAwait(false);
            _position += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private int Clamp(int count) => (int)Math.Min(count, Math.Max(0, piece.Length - _position));

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => piece.Length + offset,
            };
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_closed)
            {
                _closed = true;
                owner.Release();
            }

            base.Dispose(disposing);
        }
    }
}
