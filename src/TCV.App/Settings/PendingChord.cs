namespace TCV.App.Settings;

/// <summary>
/// The first stroke of a chord (<c>Ctrl+K</c> …) waiting for its second one. Pure state with an
/// injected clock, so the rules — it expires, it is consumed once, focus loss and clicks drop it — are
/// testable without a window.
/// </summary>
/// <remarks>
/// A pending stroke swallows the next key. Without an expiry, pressing <c>Ctrl+K</c> and then switching
/// to another window left the manager waiting; coming back, the first key typed vanished. It expires
/// after <see cref="DefaultTimeout"/>, and the window drops it when it loses focus or is clicked.
/// </remarks>
public sealed class PendingChord
{
    /// <summary>How long the second stroke may take, as in editors that show a "waiting" hint.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private KeyStroke? _first;
    private DateTime _expiresUtc;

    /// <summary>The waiting stroke, or null when none is pending (or it has expired).</summary>
    public KeyStroke? Peek(DateTime nowUtc) => _first is not null && nowUtc < _expiresUtc ? _first : null;

    /// <summary>Starts waiting for the second stroke of a chord that began with <paramref name="first"/>.</summary>
    public void Begin(KeyStroke first, DateTime nowUtc, TimeSpan? timeout = null)
    {
        _first = first;
        _expiresUtc = nowUtc + (timeout ?? DefaultTimeout);
    }

    /// <summary>
    /// Returns the waiting first stroke and stops waiting, or null when nothing (live) was pending.
    /// An expired stroke is dropped without being returned: its moment has passed.
    /// </summary>
    public KeyStroke? Take(DateTime nowUtc)
    {
        var live = Peek(nowUtc);
        _first = null;
        return live;
    }

    /// <summary>Forgets a pending stroke (focus lost, a click, Escape).</summary>
    public void Clear() => _first = null;
}
