namespace TCV.Network;

/// <summary>
/// Extra connections to one server that file transfers ride on, so that the connection a panel lists
/// through is never held by a stream for the minutes a big file takes: the panel goes on browsing the
/// server while a copy streams in the background. A connection that carried a transfer to its end is
/// kept for the next one; one left mid-transfer is closed, since the server may still be answering it.
/// </summary>
/// <remarks>
/// A server that refuses one more connection (a per-user or per-address limit) is not asked again:
/// the number of connections it took becomes the ceiling, and a transfer that finds none free gets
/// null and goes over the shared connection, as every transfer did before. A server that has given one
/// keeps a ceiling of one at least: a connection of ours that is still closing counts against its limit
/// for a moment after it is gone from here. A changed host identity is not such a refusal: it is thrown
/// (TRAP-43). Neither is a failure of a connect that was being cancelled, whatever it surfaces as.
/// <para>
/// A refusal of the very first connection says nothing about a limit: the login may have been wrong and
/// corrected since, and a ceiling of 0 kept every transfer on the shared connection for as long as the file
/// system lived. It is asked again after <see cref="AskAgainAfter"/>, rarely enough that repeated refusals
/// do not read as an attack, and at once when the caller knows better (<see cref="AskAgain"/>).
/// </para>
/// </remarks>
internal sealed class TransferConnectionPool<T> : IDisposable where T : class
{
    private readonly Func<CancellationToken, Task<T>> _open;
    private readonly Func<T, bool> _isAlive;
    private readonly Action<T> _close;
    private readonly Func<DateTime> _utcNow;
    private readonly object _sync = new();
    private readonly List<(T Client, DateTime IdleSince)> _idle = [];
    private int _count;
    private int? _ceiling;
    private DateTime _askAgainAt;
    private bool _everOpened;
    private bool _disposed;

    /// <summary>How long a server that has given no connection at all is left alone.</summary>
    public static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(5);

    /// <param name="open">Makes a new connection, logged in and ready; throws when the server refuses it.</param>
    /// <param name="isAlive">Whether a connection kept from an earlier transfer is still open.</param>
    /// <param name="close">Closes a connection for good; must not block (a polite close can wait for a timeout).</param>
    /// <param name="utcNow">The clock; tests set it.</param>
    public TransferConnectionPool(
        Func<CancellationToken, Task<T>> open, Func<T, bool> isAlive, Action<T> close, Func<DateTime>? utcNow = null)
    {
        _open = open;
        _isAlive = isAlive;
        _close = close;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Connections open now: kept ones and those carrying a transfer.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _count;
            }
        }
    }

    /// <summary>The server refused a connection, and no more will be asked for than it took.</summary>
    public bool HasCeiling
    {
        get
        {
            lock (_sync)
            {
                return _ceiling is not null;
            }
        }
    }

    /// <summary>
    /// A connection for one transfer: a kept one, or a new one. Null when the server will not give
    /// another; the caller then transfers over the shared connection. Every connection returned must
    /// be handed back with <see cref="Return"/>.
    /// </summary>
    public async Task<T?> RentAsync(CancellationToken ct)
    {
        while (true)
        {
            T? kept = null;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_idle.Count > 0)
                {
                    // The most recently used: the one least likely to have been closed by the server.
                    kept = _idle[^1].Client;
                    _idle.RemoveAt(_idle.Count - 1);
                }
                else if (_ceiling is { } ceiling && _count >= ceiling && (ceiling > 0 || _utcNow() < _askAgainAt))
                {
                    return null;
                }
                else
                {
                    if (_ceiling == 0)
                    {
                        // Asking again after a refusal: one probe, not one per transfer started meanwhile.
                        _askAgainAt = _utcNow() + AskAgainAfter;
                    }

                    _count++;
                }
            }

            if (kept is null)
            {
                return await OpenAsync(ct);
            }

            if (_isAlive(kept))
            {
                return kept;
            }

            // The server closed it while it waited: drop it and look again.
            Discard(kept);
        }
    }

    private async Task<T?> OpenAsync(CancellationToken ct)
    {
        T client;
        try
        {
            client = await _open(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
                                   && NetworkErrors.Find<HostKeyChangedException>(ex) is null)
        {
            lock (_sync)
            {
                _count--;
                _ceiling = Math.Max(_count, _everOpened ? 1 : 0);
                _askAgainAt = _utcNow() + AskAgainAfter;
            }

            return null;
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _count--;
            }

            if (NetworkErrors.Find<HostKeyChangedException>(ex) is { } changed)
            {
                throw changed;
            }

            throw;
        }

        lock (_sync)
        {
            _everOpened = true;
            if (_ceiling == 0)
            {
                _ceiling = null;   // it gives connections after all
            }
        }

        return client;
    }

    /// <summary>The refusal of the first connection is out of date: the next transfer asks the server again.</summary>
    public void AskAgain()
    {
        lock (_sync)
        {
            if (_ceiling == 0)
            {
                _ceiling = null;
            }
        }
    }

    /// <summary>
    /// Hands back a connection from <see cref="RentAsync"/>. It is kept for the next transfer only when
    /// <paramref name="reusable"/>: the transfer ran to its end and the server has answered for it.
    /// </summary>
    public void Return(T client, bool reusable)
    {
        lock (_sync)
        {
            if (reusable && !_disposed)
            {
                _idle.Add((client, _utcNow()));
                return;
            }
        }

        Discard(client);
    }

    /// <summary>
    /// Closes the connections no transfer has used for <paramref name="idleFor"/>: a server closes a
    /// silent connection after a few minutes, and keeping spare ones open costs it a slot. The next
    /// transfer opens a new one.
    /// </summary>
    /// <returns>How many were closed.</returns>
    public int CloseIdle(TimeSpan idleFor)
    {
        List<T> stale;
        lock (_sync)
        {
            var now = _utcNow();
            stale = _idle.Where(i => now - i.IdleSince >= idleFor).Select(i => i.Client).ToList();
            _idle.RemoveAll(i => now - i.IdleSince >= idleFor);
        }

        stale.ForEach(Discard);
        return stale.Count;
    }

    private void Discard(T client)
    {
        lock (_sync)
        {
            _count--;
        }

        _close(client);
    }

    /// <summary>Closes the kept connections; one still carrying a transfer is closed when it comes back.</summary>
    public void Dispose()
    {
        List<T> idle;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            idle = _idle.Select(i => i.Client).ToList();
            _idle.Clear();
        }

        idle.ForEach(Discard);
    }
}
