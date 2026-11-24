namespace TCV.Network;

/// <summary>
/// The one connection the transfer queue keeps between items, so a folder of thousands of small files
/// logs in once and not once per file: the login alone costs more than such a file, and a server with a
/// limit on logins per minute ends the transfer. The queue runs one item at a time, so one is enough.
/// </summary>
/// <remarks>
/// Only a connection that carried its item to the end is kept: one left mid-transfer may still owe an
/// answer. A connection that is kept may die between two items without a word, and only the next command
/// finds out, so <see cref="RunAsync"/> gives the work one more try on a new connection. The first
/// connection of a run is not retried: its failure is the server's real answer. Unlike the
/// <see cref="TransferConnectionPool{T}"/>, a refused login is thrown: the queue has no other connection
/// to fall back on, and the item has to say why it failed.
/// </remarks>
/// <param name="close">Closes a connection; finished means closed. One that failed is waited for: the next
/// thing the queue may do is delete the partial file, which the server may still hold open for it.</param>
internal sealed class KeptConnection<T>(
    Func<ConnectionInfo, CancellationToken, Task<T>> open, Func<T, bool> isAlive, Func<T, Task> close)
    where T : class
{
    private readonly object _sync = new();
    private T? _kept;
    private LoginIdentity _keptFor;

    /// <summary>
    /// Runs <paramref name="work"/> on the kept connection if it is to the same login and alive, or on a
    /// new one. <paramref name="mayRetry"/> says whether a failure could be the kept connection's fault;
    /// what the server said about the file itself (a target that exists) is not.
    /// </summary>
    public async Task RunAsync(
        ConnectionInfo connection, Func<T, Task> work, Func<Exception, bool> mayRetry, CancellationToken ct)
    {
        while (true)
        {
            var client = TakeKept(connection);
            var wasKept = client is not null;
            client ??= await open(connection, ct);

            var succeeded = false;
            try
            {
                await work(client);
                succeeded = true;
                return;
            }
            catch (Exception ex) when (wasKept && !ct.IsCancellationRequested && mayRetry(ex))
            {
                // Round again: nothing is kept any more, so this ends after one new connection.
            }
            finally
            {
                if (succeeded)
                {
                    Keep(connection, client);
                }
                else
                {
                    await close(client);
                }
            }
        }
    }

    private T? TakeKept(ConnectionInfo connection)
    {
        T? stale = null;
        try
        {
            lock (_sync)
            {
                var kept = _kept;
                _kept = null;
                if (kept is null)
                {
                    return null;
                }

                if (_keptFor == LoginIdentity.Of(connection) && isAlive(kept))
                {
                    return kept;
                }

                stale = kept;
                return null;
            }
        }
        finally
        {
            if (stale is not null)
            {
                _ = close(stale);
            }
        }
    }

    private void Keep(ConnectionInfo connection, T client)
    {
        T? previous;
        lock (_sync)
        {
            previous = _kept;
            _kept = client;
            _keptFor = LoginIdentity.Of(connection);
        }

        if (previous is not null)
        {
            _ = close(previous);
        }
    }

    /// <summary>Closes what is kept: the queue has run dry, and a silent connection costs the server a slot.</summary>
    public void Close()
    {
        T? kept;
        lock (_sync)
        {
            kept = _kept;
            _kept = null;
        }

        if (kept is not null)
        {
            _ = close(kept);
        }
    }

    /// <summary>
    /// Everything a login depends on. By value, not by object: items of one folder may hold copies, and
    /// the same object may have had its password corrected between a failure and the Retry.
    /// </summary>
    private readonly record struct LoginIdentity(
        NetworkProtocol Protocol, string Host, int Port, string Username, string? Password,
        SshAuthMethod AuthMethod, string? KeyFilePath, string? KeyFilePassphrase, bool ReadOnly)
    {
        public static LoginIdentity Of(ConnectionInfo c) => new(
            c.Protocol, c.Host, c.Port, c.Username, c.Password, c.AuthMethod, c.KeyFilePath, c.KeyFilePassphrase, c.ReadOnly);
    }
}
