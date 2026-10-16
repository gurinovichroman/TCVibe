using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentFTP;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using TCV.Network.Cloud;

namespace TCV.Network;

/// <summary>
/// When a panel's connection dropped, how long to wait before each new try and which failures a new
/// try can fix (6.82). A dropped session, a timeout, a server that is restarting are worth another
/// try; a refused login, a changed host key (TRAP-43: a refusal by design, never a question), a
/// folder that is gone or forbidden are not — retrying them would only repeat the same answer.
/// </summary>
public sealed class ReconnectPolicy
{
    /// <summary>
    /// 1, 2, 5, 10, 20 s, then every 30 s: about two minutes in all. A server that comes back after a
    /// restart is caught within seconds, and a network that is gone for good stops being knocked on.
    /// </summary>
    public static ReconnectPolicy Default { get; } = new(
    [
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30),
    ]);

    private readonly TimeSpan[] _delays;

    /// <param name="delays">The pause before each try, first try first; their number is the number of tries.</param>
    public ReconnectPolicy(IEnumerable<TimeSpan> delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        _delays = delays.ToArray();
        if (_delays.Length == 0)
        {
            throw new ArgumentException("At least one try is needed.", nameof(delays));
        }
    }

    /// <summary>How many tries are made before giving up.</summary>
    public int MaxAttempts => _delays.Length;

    /// <summary>The pause before try <paramref name="attempt"/> (1-based); null once the tries are used up.</summary>
    public TimeSpan? DelayBefore(int attempt) =>
        attempt >= 1 && attempt <= _delays.Length ? _delays[attempt - 1] : null;

    /// <summary>
    /// True when <paramref name="error"/> looks like a dropped or unreachable connection that a new
    /// connection may fix. A refusal anywhere in the chain wins over a network error around it: a TLS
    /// handshake rejected by a pinned certificate arrives wrapped in an <see cref="IOException"/>.
    /// Anything not recognised is not retried — a new try of an unknown error is noise.
    /// </summary>
    public static bool IsWorthRetrying(Exception? error)
    {
        if (error is null)
        {
            return false;
        }

        var chain = Flatten(error).ToList();
        if (chain.Any(IsRefusal))
        {
            return false;
        }

        return chain.Any(IsDrop);
    }

    /// <summary>A failure a new connection cannot fix: credentials, identity, permissions, a missing folder.</summary>
    private static bool IsRefusal(Exception ex) => ex switch
    {
        HostKeyChangedException => true,
        SignInRequiredException => true,
        SshAuthenticationException => true,
        SshPassPhraseNullOrEmptyException => true,
        SftpPermissionDeniedException => true,
        SftpPathNotFoundException => true,
        SshConnectionException ssh => IsRefusingDisconnect(ssh.DisconnectReason),
        FtpAuthenticationException => true,
        FtpSecurityNotAvailableException => true,
        FtpInvalidCertificateException => true,
        FtpCommandException ftp => ftp.ResponseType == FtpResponseType.PermanentNegativeCompletion,
        AuthenticationException => true,
        UnauthorizedAccessException => true,
        FileNotFoundException or DirectoryNotFoundException => true,
        S3Exception s3 => !IsTransientStatus(s3.StatusCode),
        AzureStorageException azure => !IsTransientStatus(azure.StatusCode),
        CloudDriveException drive => !IsTransientStatus(drive.StatusCode),
        HttpRequestException { StatusCode: { } status } => !IsTransientStatus(status),
        System.Security.Cryptography.CryptographicException => true,
        _ => false,
    };

    /// <summary>A failure of the line itself: the socket, a timeout, a session the server closed.</summary>
    private static bool IsDrop(Exception ex) => ex switch
    {
        SocketException => true,
        SshConnectionException => true,
        SshOperationTimeoutException => true,
        FtpMissingSocketException => true,
        FtpCommandException ftp => ftp.ResponseType == FtpResponseType.TransientNegativeCompletion,
        TimeoutException => true,
        S3Exception or AzureStorageException or CloudDriveException => true,   // transient: refusals were sorted out above
        HttpRequestException => true,
        TaskCanceledException { InnerException: TimeoutException } => true,
        IOException => true,
        _ => false,
    };

    /// <summary>The server said why it hung up, and a new connection would get the same answer.</summary>
    private static bool IsRefusingDisconnect(DisconnectReason reason) => reason is
        DisconnectReason.HostNotAllowedToConnect
        or DisconnectReason.KeyExchangeFailed
        or DisconnectReason.ProtocolVersionNotSupported
        or DisconnectReason.HostKeyNotVerifiable
        or DisconnectReason.AuthenticationCanceledByUser
        or DisconnectReason.NoMoreAuthenticationMethodsAvailable
        or DisconnectReason.IllegalUserName;

    /// <summary>An HTTP answer that says "later": a timeout, throttling, a server error.</summary>
    private static bool IsTransientStatus(HttpStatusCode status) =>
        status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;

    private static IEnumerable<Exception> Flatten(Exception error)
    {
        var pending = new Stack<Exception>();
        pending.Push(error);
        var seen = 0;
        while (pending.Count > 0 && seen++ < 64)
        {
            var current = pending.Pop();
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    pending.Push(inner);
                }
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }
    }
}
