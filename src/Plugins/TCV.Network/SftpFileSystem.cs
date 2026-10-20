using Renci.SshNet;
using Renci.SshNet.Sftp;
using TCV.Core.Domain;
using TCV.Core.Exceptions;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>IVirtualFileSystem backed by SFTP via SSH.NET.</summary>
public sealed class SftpFileSystem : IVirtualFileSystem, IModifiedTimeWriter
{
    private readonly ConnectionInfo _conn;
    private readonly SftpClient _client;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SftpFileSystem(ConnectionInfo conn, HostTrustPolicy? trust = null)
    {
        _conn = conn;
        _client = CreateClient(conn, trust);
    }

    public string Scheme => "sftp";
    public string DisplayName => $"sftp://{_conn.Username}@{_conn.Host}";

    public event EventHandler<VfsChangedEventArgs>? Changed;

    // ── Connection ────────────────────────────────────────────────────────────

    /// <summary>Creates a configured SftpClient for the given connection. Used by TransferQueue and connection test.</summary>
    public static SftpClient CreateClientInternal(ConnectionInfo conn, HostTrustPolicy? trust = null) =>
        CreateClient(conn, trust);

    private static SftpClient CreateClient(ConnectionInfo conn, HostTrustPolicy? trust)
    {
        Renci.SshNet.ConnectionInfo sshConn;

        if (conn.AuthMethod == SshAuthMethod.KeyFile)
        {
            if (string.IsNullOrWhiteSpace(conn.KeyFilePath))
            {
                throw new InvalidOperationException("Key-file authentication is selected but no key file is set.");
            }

            var keyFile = string.IsNullOrEmpty(conn.KeyFilePassphrase)
                ? new PrivateKeyFile(conn.KeyFilePath)
                : new PrivateKeyFile(conn.KeyFilePath, conn.KeyFilePassphrase);

            sshConn = new PrivateKeyConnectionInfo(conn.Host, conn.Port, conn.Username, keyFile);
        }
        else
        {
            // Many servers (PAM setups, macOS) offer only keyboard-interactive for passwords; with
            // "password" alone they answer "Permission denied". Every hidden prompt gets the password.
            var password = conn.Password ?? string.Empty;
            var interactive = new KeyboardInteractiveAuthenticationMethod(conn.Username);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (var prompt in e.Prompts.Where(p => !p.IsEchoed))
                {
                    prompt.Response = password;
                }
            };

            sshConn = new Renci.SshNet.ConnectionInfo(conn.Host, conn.Port, conn.Username,
                new PasswordAuthenticationMethod(conn.Username, password), interactive);
        }

        var client = new SftpClient(sshConn) { KeepAliveInterval = TimeSpan.FromSeconds(30) };

        // Nothing is sent to a server whose key nobody vouched for: the policy compares against the
        // known-hosts list, asks on first contact, and refuses a changed key outright.
        var policy = trust ?? NetworkSecurity.Default;
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = policy is not null
                         && policy.VerifySshHostKey(conn.Host, conn.Port, e.HostKeyName, e.HostKey);
        };

        return client;
    }

    private void EnsureConnected() => ConnectClient(_client);

    /// <summary>Connects, surfacing a changed host key as itself rather than as a generic SSH failure.</summary>
    public static void ConnectClient(SftpClient client)
    {
        if (client.IsConnected)
        {
            return;
        }

        try
        {
            client.Connect();
        }
        catch (Exception ex) when (NetworkErrors.Find<HostKeyChangedException>(ex) is { } changed)
        {
            throw changed;
        }
    }

    /// <summary>
    /// Runs one blocking SSH.NET call off the caller's thread. Every method goes through here:
    /// the client is synchronous — connecting alone can block for the full SSH timeout — and
    /// the callers are WPF commands, which must not stall the dispatcher. The gate serialises
    /// access because one SftpClient cannot service two operations at once.
    /// </summary>
    private async Task<T> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                EnsureConnected();
                return work();
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Listing ───────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<IFileEntry>>(() => _client
            .ListDirectory(ToRemotePath(path))
            .Where(f => f.Name is not ("." or ".."))
            .Select(f => (IFileEntry)ToFileEntry(path, f))
            .ToList(), ct);

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) =>
        RunAsync<IFileEntry?>(() =>
        {
            var remotePath = ToRemotePath(path);
            SftpFileAttributes attr;
            try
            {
                attr = _client.GetAttributes(remotePath);
            }
            catch (Renci.SshNet.Common.SftpPathNotFoundException)
            {
                // "Does not exist" is an answer, not a failure — callers probe with this
                // before copying, and a throw here would fail every copy of a new file.
                return null;
            }

            return new NetworkFileEntry(
                path,
                Path.GetFileName(remotePath),
                attr.IsDirectory,
                attr.IsDirectory ? 0 : attr.Size,
                attr.LastWriteTime);
        }, ct);

    // ── Streams ───────────────────────────────────────────────────────────────

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
        RunAsync<Stream>(() => _client.OpenRead(ToRemotePath(path)), ct);

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
        RunAsync<Stream>(() => overwrite
            ? _client.Create(ToRemotePath(path))
            : _client.OpenWrite(ToRemotePath(path)), ct);

    // ── Operations ────────────────────────────────────────────────────────────

    public async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // The streams are opened under the gate, then copied outside it: each SFTP stream
        // gets its own channel, and holding the gate for a multi-gigabyte copy would block
        // every listing behind it.
        using var readStream = await RunAsync(() => _client.OpenRead(ToRemotePath(source)), ct);
        var size = readStream.Length;
        using var writeStream = await RunAsync(() => _client.Create(ToRemotePath(destination)), ct);

        var buffer = new byte[81920];
        long copied = 0;
        int read;

        while ((read = await readStream.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await writeStream.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            progress?.Report(new FileProgress(copied, size, source.FileName, sw.Elapsed));
        }
    }

    /// <summary>
    /// Renames over an existing file, as every other file system's move does once the operation has
    /// decided to replace it. Plain SFTP rename refuses an existing target, so the OpenSSH
    /// posix-rename extension (an atomic replace) is used; a server without it gets delete-then-rename.
    /// </summary>
    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        RunAsync(() =>
        {
            var from = ToRemotePath(source);
            var to = ToRemotePath(destination);
            try
            {
                _client.RenameFile(from, to, isPosix: true);
                return true;
            }
            catch (NotSupportedException)
            {
                // No posix-rename on this server: fall through.
            }

            if (_client.Exists(to) && !_client.GetAttributes(to).IsDirectory)
            {
                _client.DeleteFile(to);
            }

            _client.RenameFile(from, to);
            return true;
        }, ct);

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        var remotePath = ToRemotePath(path);
        if (await RunAsync(() => _client.GetAttributes(remotePath).IsDirectory, ct))
        {
            await DeleteDirectoryAsync(remotePath, recursive, ct);
        }
        else
        {
            await RunAsync(() =>
            {
                _client.DeleteFile(remotePath);
                return true;
            }, ct);
        }
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) =>
        RunAsync(() =>
        {
            _client.CreateDirectory(ToRemotePath(path));
            return true;
        }, ct);

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) =>
        RunAsync(() =>
        {
            var oldPath = ToRemotePath(path);
            var dir = oldPath[..oldPath.LastIndexOf('/')];
            _client.RenameFile(oldPath, $"{dir}/{newName}");
            return true;
        }, ct);

    public bool SupportsOperation(VfsOperation operation) => true;

    public async Task<bool> TrySetLastModifiedAsync(VfsPath path, DateTime lastModified, CancellationToken ct = default)
    {
        try
        {
            return await RunAsync(() =>
            {
                _client.SetLastWriteTimeUtc(ToRemotePath(path), lastModified.ToUniversalTime());
                return true;
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;   // the upload succeeded; a server that refuses the time is not a failed transfer
        }
    }

    /// <summary>
    /// Disconnects in the background: SSH.NET's Dispose waits for the session to wind down, which
    /// after a dropped connection blocks — and a panel disconnects on the UI thread (INV-06). The gate
    /// is left to the garbage collector so a call still queued on it fails cleanly instead of throwing
    /// ObjectDisposedException from the semaphore.
    /// </summary>
    public void Dispose()
    {
        var client = _client;
        _ = Task.Run(() =>
        {
            try { client.Dispose(); }
            catch { /* closed either way */ }
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ToRemotePath(VfsPath path) =>
        string.IsNullOrEmpty(path.Value) || path.Value == "/" ? "/" : path.Value.Replace('\\', '/');

    private static NetworkFileEntry ToFileEntry(VfsPath parentPath, ISftpFile f) =>
        new(parentPath.Combine(f.Name), f.Name, f.IsDirectory, f.IsDirectory ? 0 : f.Length, f.LastWriteTime);

    /// <summary>
    /// A tree goes one SSH call at a time, each under the gate on its own: holding the gate for the
    /// whole tree kept the panel from listing until the last file was gone, and a cancel was only seen
    /// once it was all over.
    /// </summary>
    private async Task DeleteDirectoryAsync(string remotePath, bool recursive, CancellationToken ct)
    {
        if (recursive)
        {
            var children = await RunAsync(() => _client.ListDirectory(remotePath)
                .Where(f => f.Name is not ("." or ".."))
                .Select(f => (f.FullName, f.IsDirectory))
                .ToList(), ct);

            foreach (var (child, isDirectory) in children)
            {
                ct.ThrowIfCancellationRequested();
                if (isDirectory)
                {
                    await DeleteDirectoryAsync(child, recursive: true, ct);
                }
                else
                {
                    await RunAsync(() =>
                    {
                        _client.DeleteFile(child);
                        return true;
                    }, ct);
                }
            }
        }

        await RunAsync(() =>
        {
            _client.DeleteDirectory(remotePath);
            return true;
        }, ct);
    }
}
