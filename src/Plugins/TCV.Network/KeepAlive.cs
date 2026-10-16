namespace TCV.Network;

/// <summary>
/// A file system whose session the server closes when it sits idle — FTP's control connection, which
/// servers drop after a few minutes of silence (6.82). The application calls
/// <see cref="KeepAliveAsync"/> now and then for every connection a panel holds, the parked ones of
/// background tabs included (ADR-0032), so going back to a tab does not meet a dead session.
/// </summary>
/// <remarks>
/// SFTP needs no help here: SSH.NET sends its own keep-alive every 30 s
/// (<c>SftpClient.KeepAliveInterval</c>). HTTP-based storage (WebDAV, S3, Azure, the drives) has no
/// session to keep.
/// </remarks>
public interface IKeepAlive
{
    /// <summary>
    /// Sends a no-op if the connection has been idle for <see cref="KeepAlivePolicy.IdleBeforePing"/>;
    /// does nothing while it is busy or not connected. A connection that turns out to be dead is
    /// closed, so the next use connects afresh instead of failing on the old socket. Never throws
    /// except for cancellation.
    /// </summary>
    Task KeepAliveAsync(CancellationToken ct);
}

/// <summary>How often an idle connection is pinged.</summary>
public static class KeepAlivePolicy
{
    /// <summary>
    /// The idle time after which a no-op goes out. Under IIS FTP's two-minute control timeout, well
    /// under the five minutes of vsftpd and ProFTPD.
    /// </summary>
    public static TimeSpan IdleBeforePing { get; } = TimeSpan.FromSeconds(60);

    /// <summary>How often the application looks for connections to ping: twice per idle period.</summary>
    public static TimeSpan CheckInterval { get; } = TimeSpan.FromSeconds(30);

    /// <summary>True when a connection last used at <paramref name="lastActivityUtc"/> should be pinged now.</summary>
    public static bool IsDue(DateTime lastActivityUtc, DateTime nowUtc) =>
        nowUtc - lastActivityUtc >= IdleBeforePing;
}
