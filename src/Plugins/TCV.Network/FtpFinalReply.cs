using FluentFTP;

namespace TCV.Network;

/// <summary>
/// The end of an FTP upload: the server's reply on the control connection, once the data connection has
/// closed. Closing the data stream does not read it (<c>DisposeAsync</c> of FluentFTP's stream, in 50 and 55
/// alike), and until it comes the server may still be writing: a listing shows a short file, and a time
/// stamp or a rename arrives ahead of the end. It is also the only place a full disk is reported, so a
/// stream closed without reading it reported success (TRAP-100).
/// </summary>
internal static class FtpFinalReply
{
    /// <summary>
    /// How long the server may take: FluentFTP's 15 s (its read timeout) is too short for one that commits
    /// the file on close, a gateway to object storage or a busy disk. Nothing else can use the connection
    /// meanwhile, so it is a limit and not a pause: Cancel works at once, and a server that answers early
    /// costs nothing.
    /// </summary>
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a download waits. The server has closed the data connection by then, with nothing left
    /// to commit, so its word comes at once: a reader at the end of a file should not hang for minutes.
    /// </summary>
    public static readonly TimeSpan DefaultDownloadWait = TimeSpan.FromSeconds(30);

    /// <summary>A server may report progress with 1xx; one that does nothing else is not an answer.</summary>
    private const int MaxPreliminaryReplies = 16;

    /// <summary>
    /// Waits for the reply to the transfer that has just closed. The caller owns the connection for as
    /// long as this runs: the rented one, or the shared one with its gate held (so no NOOP or listing
    /// rides in between, TRAP-82). Returns on a completion reply; anything else throws, and a missing
    /// reply is not taken for a good one. After a timeout or a cancel FluentFTP has closed the connection.
    /// </summary>
    /// <exception cref="IOException">The server refused the file, or never said it has it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task ConfirmUploadAsync(AsyncFtpClient client, string remotePath, TimeSpan wait, CancellationToken ct) =>
        ReadReplyAsync(
            client, wait,
            reply => new FtpTransferRefusedException($"The server refused the upload of \"{remotePath}\": {reply.Code} {reply.Message}"),
            cause => new IOException($"The server did not confirm the upload of \"{remotePath}\": the file on it may be incomplete.", cause),
            ct);

    /// <summary>
    /// Waits for the reply to the download whose data has just run out. A server that gives up midway
    /// (<c>426</c>, <c>451</c>) closes the data connection as it does at the end of the file, and this
    /// reply is the only thing that tells the two apart: without it a short copy passed for the whole file.
    /// </summary>
    /// <exception cref="IOException">The server gave up on the file, or never said it sent all of it.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled.</exception>
    public static Task ConfirmDownloadAsync(AsyncFtpClient client, string remotePath, TimeSpan wait, CancellationToken ct) =>
        ReadReplyAsync(
            client, wait,
            reply => new FtpTransferRefusedException($"The server broke off the download of \"{remotePath}\": {reply.Code} {reply.Message}"),
            cause => new IOException($"The server did not confirm the download of \"{remotePath}\": the copy may be incomplete.", cause),
            ct);

    /// <summary>
    /// Runs a whole transfer of the library's (<c>UploadFile</c>, <c>DownloadFile</c>), which reads the final
    /// reply itself with the client's read timeout: 15 s, a false error on a server that commits on close
    /// (TRAP-100). The commands on the way wait as long, which on a dead server is a slower failure and
    /// nothing worse: its token still cancels at once.
    /// </summary>
    public static async Task<T> WithPatienceAsync<T>(AsyncFtpClient client, TimeSpan wait, Func<Task<T>> transfer)
    {
        var readTimeout = client.Config.ReadTimeout;
        client.Config.ReadTimeout = (int)wait.TotalMilliseconds;
        try
        {
            return await transfer();
        }
        finally
        {
            client.Config.ReadTimeout = readTimeout;
        }
    }

    private static async Task ReadReplyAsync(
        AsyncFtpClient client, TimeSpan wait, Func<FtpReply, IOException> refused, Func<Exception?, IOException> notConfirmed,
        CancellationToken ct)
    {
        try
        {
            await WithPatienceAsync(client, wait, async () =>
            {
                for (var replies = 0; replies <= MaxPreliminaryReplies; replies++)
                {
                    var reply = await client.GetReply(ct);
                    if (reply.Type == FtpResponseType.PositivePreliminary)
                    {
                        continue;
                    }

                    if (reply.Type == FtpResponseType.PositiveCompletion)
                    {
                        return true;
                    }

                    throw refused(reply);
                }

                throw notConfirmed(null);
            });
        }
        catch (Exception ex) when (ex is TimeoutException or InvalidOperationException)
        {
            // InvalidOperationException: "No connection to the server exists", the control connection is gone.
            throw notConfirmed(ex);
        }
    }
}

/// <summary>
/// The server's answer to a transfer was no («552 Disk full», «426 Connection closed»). The transfer has
/// failed, but the server has answered, so nothing is left on the connection and it can go on: a server
/// with a limit on connections, one that makes us share the panel's, might not give another right away.
/// </summary>
internal sealed class FtpTransferRefusedException(string message) : IOException(message);
