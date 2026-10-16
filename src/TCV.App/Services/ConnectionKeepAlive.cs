using Microsoft.Extensions.Logging;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>One open file system to keep alive, and how to keep it from being closed while the ping is out.</summary>
/// <param name="FileSystem">A file system a panel holds, on screen or parked in a background tab.</param>
/// <param name="Lease">Holds it open until disposed (<c>FileListViewModel.LeaseFileSystem</c>, INV-09).</param>
public readonly record struct KeepAliveTarget(IVirtualFileSystem FileSystem, Func<IDisposable> Lease);

/// <summary>
/// Keeps the connections of every tab alive (6.82): a background tab's FTP session (ADR-0032) would
/// otherwise be closed by the server after a few idle minutes, and going back to the tab met a dead
/// connection. The file system decides whether a ping is due (<see cref="IKeepAlive"/>); this only
/// sends the round out, off the UI thread, with each target leased so closing its tab meanwhile
/// waits for the ping instead of disposing the client under it.
/// </summary>
public static class ConnectionKeepAlive
{
    /// <summary>
    /// Pings every target that can be kept alive, all at once; the same file system listed twice is
    /// pinged once. Never throws: a failed ping is logged and the next listing reconnects. Call it on
    /// the UI thread — leases are taken and released there.
    /// </summary>
    public static async Task PingAsync(IEnumerable<KeepAliveTarget> targets, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(logger);

        var seen = new HashSet<IVirtualFileSystem>(ReferenceEqualityComparer.Instance);
        var pings = new List<Task>();
        foreach (var target in targets)
        {
            if (target.FileSystem is IKeepAlive keepAlive && seen.Add(target.FileSystem))
            {
                pings.Add(PingOneAsync(target, keepAlive, logger, ct));
            }
        }

        await Task.WhenAll(pings);
    }

    private static async Task PingOneAsync(KeepAliveTarget target, IKeepAlive keepAlive, ILogger logger, CancellationToken ct)
    {
        using var lease = target.Lease();
        try
        {
            // Off the UI thread: a dead server makes even a NOOP wait for its timeout (INV-06).
            await Task.Run(() => keepAlive.KeepAliveAsync(ct), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Keep-alive of {Name} failed", target.FileSystem.DisplayName);
        }
    }
}
