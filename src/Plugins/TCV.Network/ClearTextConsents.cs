namespace TCV.Network;

/// <summary>
/// The user's yes to sending a password unencrypted, for this run of the application and this address
/// only. Reconnecting a server the user has just agreed to is not a new decision, and asking again each
/// time only teaches clicking yes. The first connect to an address is always asked; so is another server,
/// port, login or protocol, and a new run: nothing here is written anywhere.
/// </summary>
public sealed class ClearTextConsents
{
    private readonly HashSet<string> _given = new(StringComparer.Ordinal);

    /// <summary>True when the user has agreed for this address already, or does when asked now.</summary>
    /// <param name="info">The connection about to send its password.</param>
    /// <param name="ask">The question; only a yes is remembered.</param>
    public async Task<bool> AskOnceAsync(ConnectionInfo info, Func<Task<bool>> ask)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(ask);

        var address = AddressOf(info);
        lock (_given)
        {
            if (_given.Contains(address))
            {
                return true;
            }
        }

        if (!await ask())
        {
            return false;
        }

        // The answer may come back on any thread (TRAP-66).
        lock (_given)
        {
            _given.Add(address);
        }

        return true;
    }

    /// <summary>The name of a host is not case-sensitive, a login may be. The saved connection's name plays no part.</summary>
    private static string AddressOf(ConnectionInfo info) => string.Join(
        '\u001f', info.Protocol, info.Host.Trim().ToLowerInvariant(), info.Port, info.Username);
}
