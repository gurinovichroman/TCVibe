using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>IVirtualFileSystem backed by FTP/FTPS via FluentFTP.</summary>
public sealed class FtpFileSystem : IVirtualFileSystem, IModifiedTimeWriter, IKeepAlive
{
    private readonly ConnectionInfo _conn;
    private readonly HostTrustPolicy? _trust;
    private readonly AsyncFtpClient _client;

    // One control connection can run one command at a time; a listing racing a transfer corrupts
    // both. A stream on it (only when the server gives no transfer connection) keeps the gate until
    // it is closed.
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Connections of their own for the file transfers, so a download does not hold the control
    /// connection the panel lists through for as long as the file takes. A server that refuses
    /// another connection gets its transfers on the control one, under the gate, as before.
    /// </summary>
    private readonly TransferConnectionPool<AsyncFtpClient> _transfers;

    /// <summary>When the control connection was last used (UTC ticks): the keep-alive pings only an idle one.</summary>
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;

    public FtpFileSystem(ConnectionInfo conn, HostTrustPolicy? trust = null)
    {
        _conn = conn;
        _trust = trust;
        _client = CreateClient(conn, trust);
        _transfers = new TransferConnectionPool<AsyncFtpClient>(OpenTransferClientAsync, c => c.IsConnected, CloseInBackground);
    }

    /// <summary>
    /// Builds a configured FTP client: explicit or implicit TLS as the connection says, and
    /// certificate validation that asks the user before accepting anything the operating system
    /// does not already trust. Shared by the file system, the transfer queue and the connection test.
    /// </summary>
    public static AsyncFtpClient CreateClient(ConnectionInfo conn, HostTrustPolicy? trust = null)
    {
        var client = new AsyncFtpClient(conn.Host, conn.Username, conn.Password ?? string.Empty, conn.Port);

        if (!conn.IsFtps)
        {
            return client;
        }

        client.Config.EncryptionMode = conn.IsImplicitFtps ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        client.Config.DataConnectionEncryption = true;

        var policy = trust ?? NetworkSecurity.Default;
        client.ValidateCertificate += (_, e) => e.Accept = AcceptCertificate(policy, conn, e);
        return client;
    }

    private static bool AcceptCertificate(HostTrustPolicy? policy, ConnectionInfo conn, FtpSslValidationEventArgs e)
    {
        var chainIsValid = e.PolicyErrors == SslPolicyErrors.None;
        if (policy is null)
        {
            // No policy configured: only what the operating system already trusts.
            return chainIsValid;
        }

        var hash = e.Certificate is null ? [] : SHA256.HashData(new X509Certificate2(e.Certificate).RawData);
        var subject = e.Certificate?.Subject ?? string.Empty;
        return policy.VerifyCertificate(conn.Host, conn.Port, subject, hash, chainIsValid, e.PolicyErrors.ToString());
    }

    public string Scheme => _conn.GetScheme();
    public string DisplayName => $"{_conn.Username}@{_conn.Host}";

    public event EventHandler<VfsChangedEventArgs>? Changed;

    // ── Connection ────────────────────────────────────────────────────────────

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_client.IsConnected)
        {
            return;
        }

        try
        {
            await _client.Connect(ct);
        }
        catch (Exception ex) when (NetworkErrors.Find<HostKeyChangedException>(ex) is { } changed)
        {
            // The one failure the user must read verbatim, not as "TLS handshake failed".
            throw changed;
        }
    }

    private async Task<T> GuardedAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            return await work();
        }
        finally
        {
            Touch();
            _gate.Release();
        }
    }

    private void Touch() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    // ── Keep-alive (6.82) ─────────────────────────────────────────────────────

    /// <summary>
    /// A NOOP on a control connection idle for a minute: servers close a silent one after a few
    /// minutes (IIS after two), and a background tab's connection would be dead when the tab is shown
    /// again. A busy connection is left alone — the gate is taken only if free, so the ping never
    /// queues ahead of a listing or rides inside a transfer. A connection that does not answer is
    /// closed, so the next listing connects afresh instead of failing on the dead socket.
    /// </summary>
    public async Task KeepAliveAsync(CancellationToken ct)
    {
        // A spare transfer connection is not pinged but closed: the next transfer opens a new one.
        _transfers.CloseIdle(KeepAlivePolicy.IdleBeforePing);

        if (!KeepAlivePolicy.IsDue(new DateTime(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc), DateTime.UtcNow))
        {
            return;
        }

        if (!await _gate.WaitAsync(0, ct))
        {
            return;   // in use: a command or an open stream keeps the session alive by itself
        }

        try
        {
            if (!_client.IsConnected)
            {
                return;   // closed already: the next use connects
            }

            var reply = await _client.Execute("NOOP", ct);
            if (reply.Success)
            {
                Touch();
            }
            else if (reply.Type == FtpResponseType.TransientNegativeCompletion)
            {
                await DropDeadConnectionAsync();   // "421 Timeout": the server is closing it
            }
            else
            {
                Touch();   // a server that does not know NOOP still answered: the session is alive
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await DropDeadConnectionAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes a control connection that stopped answering, without a QUIT that would wait for its own timeout.</summary>
    private async Task DropDeadConnectionAsync()
    {
        var withQuit = _client.Config.DisconnectWithQuit;
        try
        {
            _client.Config.DisconnectWithQuit = false;
            await _client.Disconnect(CancellationToken.None);
        }
        catch (Exception)
        {
            // Closed either way: IsConnected is false and the next use connects.
        }
        finally
        {
            _client.Config.DisconnectWithQuit = withQuit;
        }
    }

    private Task GuardedAsync(Func<Task> work, CancellationToken ct) =>
        GuardedAsync(async () =>
        {
            await work();
            return true;
        }, ct);

    // ── Listing ───────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) =>
        GuardedAsync<IReadOnlyList<IFileEntry>>(async () =>
        {
            var items = await _client.GetListing(ToRemotePath(path), ct);
            return items
                .Where(item => item.Name is not ("." or ".."))
                .Select(item => (IFileEntry)new NetworkFileEntry(
                    path.Combine(item.Name),
                    item.Name,
                    item.Type == FtpObjectType.Directory,
                    item.Size,
                    ToLocal(item.Modified)))
                .ToList();
        }, ct);

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) =>
        GuardedAsync<IFileEntry?>(async () =>
        {
            var item = await _client.GetObjectInfo(ToRemotePath(path), token: ct);
            if (item is null)
            {
                // Without MLST, FluentFTP looks the path up in its parent's listing — and the root has
                // no parent entry, so a server like vsftpd reported "/" as missing and could not be
                // connected to at all. Logging in (above) proves the root is there.
                return IsRoot(path) ? new NetworkFileEntry(path, "/", true, 0, DateTime.MinValue) : null;
            }

            return new NetworkFileEntry(
                path,
                item.Name,
                item.Type == FtpObjectType.Directory,
                item.Size,
                ToLocal(item.Modified));
        }, ct);

    private static bool IsRoot(VfsPath path) => ToRemotePath(path) is "" or "/";

    /// <summary>
    /// Machine listings (MLSD/MLST) carry UTC, and FluentFTP hands it over unconverted. Every other
    /// file system speaks local time; comparing the two raw made a server file hours "older" or
    /// "newer" than it is, and a synchronisation then overwrote the genuinely newer copy.
    /// </summary>
    internal static DateTime ToLocal(DateTime modified) =>
        modified.Kind == DateTimeKind.Utc ? modified.ToLocalTime() : modified;

    // ── Streams ───────────────────────────────────────────────────────────────

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
        OpenTransferAsync(path, download: true, (client, remotePath) => client.OpenRead(remotePath, token: ct), ct);

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
        OpenTransferAsync(path, download: false, (client, remotePath) => client.OpenWrite(remotePath, token: ct), ct);

    /// <summary>
    /// Opens a transfer stream on a connection of its own, so the panel keeps listing the server while
    /// the file streams. A kept connection the server has dropped meanwhile is replaced once. Without a
    /// spare connection the stream goes over the control one and holds the gate until it is closed.
    /// </summary>
    private async Task<Stream> OpenTransferAsync(
        VfsPath path, bool download, Func<AsyncFtpClient, string, Task<Stream>> open, CancellationToken ct)
    {
        var remotePath = ToRemotePath(path);
        for (var attempt = 0; attempt < 2 && await _transfers.RentAsync(ct) is { } transfer; attempt++)
        {
            try
            {
                var stream = await open(transfer, remotePath);
                return new TransferStream(stream, download, reusable => _transfers.Return(transfer, reusable));
            }
            catch (Exception) when (!transfer.IsConnected && !ct.IsCancellationRequested && attempt == 0)
            {
                _transfers.Return(transfer, reusable: false);
            }
            catch
            {
                // Refused (no such file, no permission): the connection itself is still good.
                _transfers.Return(transfer, reusable: transfer.IsConnected && !ct.IsCancellationRequested);
                throw;
            }
        }

        await _gate.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);
            var stream = await open(_client, remotePath);

            // The gate is handed back when the stream is closed, however the transfer went; a transfer counts as use.
            return new TransferStream(stream, download, _ =>
            {
                Touch();
                _gate.Release();
            });
        }
        catch
        {
            _gate.Release();
            throw;
        }
    }

    /// <summary>A connected client for one transfer; it is closed again when the server refuses the login.</summary>
    private async Task<AsyncFtpClient> OpenTransferClientAsync(CancellationToken ct)
    {
        var client = CreateClient(_conn, _trust);
        try
        {
            await client.Connect(ct);
            return client;
        }
        catch
        {
            CloseInBackground(client);
            throw;
        }
    }

    /// <summary>
    /// Runs a whole transfer on a connection of its own, or on the control connection under the gate
    /// when the server gives no other. The connection is kept for the next transfer only after a success.
    /// </summary>
    private async Task WithTransferClientAsync(Func<AsyncFtpClient, Task> work, CancellationToken ct)
    {
        if (await _transfers.RentAsync(ct) is not { } transfer)
        {
            await GuardedAsync(() => work(_client), ct);
            return;
        }

        var succeeded = false;
        try
        {
            await work(transfer);
            succeeded = true;
        }
        finally
        {
            _transfers.Return(transfer, succeeded);
        }
    }

    // ── Operations ────────────────────────────────────────────────────────────

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
        WithTransferClientAsync(async client =>
        {
            // FTP has no server-side copy: down to a temp file and back up again.
            var tempFile = Path.GetTempFileName();
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var sourcePath = ToRemotePath(source);
                var adapter = progress is null
                    ? null
                    : new Progress<FtpProgress>(p => progress.Report(new FileProgress(
                        p.TransferredBytes, 0, source.FileName, started.Elapsed)));

                await client.DownloadFile(tempFile, sourcePath, FtpLocalExists.Overwrite, token: ct, progress: adapter);
                await client.UploadFile(tempFile, ToRemotePath(destination), FtpRemoteExists.Overwrite, token: ct);
            }
            finally
            {
                File.Delete(tempFile);
            }
        }, ct);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        GuardedAsync(() => _client.MoveFile(ToRemotePath(source), ToRemotePath(destination), token: ct), ct);

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        var remotePath = ToRemotePath(path);
        var info = await GuardedAsync(() => _client.GetObjectInfo(remotePath, token: ct), ct);
        if (info?.Type == FtpObjectType.Directory && recursive)
        {
            // A tree is a command per file: on a connection of its own, so the panel lists meanwhile.
            await WithTransferClientAsync(client => client.DeleteDirectory(remotePath, ct), ct);
            return;
        }

        await GuardedAsync(async () =>
        {
            if (info?.Type != FtpObjectType.Directory)
            {
                await _client.DeleteFile(remotePath, ct);
            }
            else
            {
                // FluentFTP's DeleteDirectory always empties the folder first; a plain RMD is what
                // "not recursive" means, and the server refuses when the folder is not empty.
                var reply = await _client.Execute("RMD " + remotePath, ct);
                if (!reply.Success)
                {
                    throw new IOException($"The server refused to remove \"{remotePath}\": {reply.Message}");
                }
            }
        }, ct);
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) =>
        GuardedAsync(() => _client.CreateDirectory(ToRemotePath(path), ct), ct);

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) =>
        GuardedAsync(() =>
        {
            var oldPath = ToRemotePath(path);
            var newPath = $"{oldPath[..oldPath.LastIndexOf('/')]}/{newName}";
            return _client.MoveFile(oldPath, newPath, token: ct);
        }, ct);

    public bool SupportsOperation(VfsOperation operation) => true;

    public async Task<bool> TrySetLastModifiedAsync(VfsPath path, DateTime lastModified, CancellationToken ct = default)
    {
        try
        {
            // Under the gate like every other command: a stamp racing a listing on the one control
            // connection corrupts both replies. MFMT is sent directly because FluentFTP's
            // SetModifiedTime ignores a refusal, and the caller needs to know the time did not stick.
            var stamp = lastModified.ToUniversalTime().ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            return await GuardedAsync(async () =>
            {
                if (!_client.HasFeature(FtpCapability.MFMT))
                {
                    return false;
                }

                var reply = await _client.Execute($"MFMT {stamp} {ToRemotePath(path)}", ct);
                return reply.Success;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;   // MFMT is optional on FTP servers
        }
    }

    /// <summary>
    /// Closes the connection in the background. FluentFTP's Dispose sends QUIT and waits for the
    /// reply synchronously; after a dropped connection that wait lasts the whole read timeout, and
    /// Dispose is called on the UI thread when a panel disconnects (INV-06).
    /// </summary>
    public void Dispose()
    {
        _transfers.Dispose();
        CloseInBackground(_client);
    }

    private static void CloseInBackground(AsyncFtpClient client) =>
        _ = Task.Run(() =>
        {
            try { client.Dispose(); }
            catch { /* a connection that will not close cleanly is closed anyway */ }
        });

    private static string ToRemotePath(VfsPath path) =>
        path.Value.Replace('\\', '/');
}

/// <summary>Helpers for picking the meaningful failure out of nested library exceptions.</summary>
public static class NetworkErrors
{
    /// <summary>The first exception of type <typeparamref name="T"/> in the chain, or null.</summary>
    public static T? Find<T>(Exception? ex) where T : Exception
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    if (Find<T>(inner) is { } nested)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }
}
