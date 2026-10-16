using System.IO;
using Microsoft.Extensions.Logging;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Services;

/// <summary>How an attempt to write an edited file back ended.</summary>
public enum RemoteEditOutcome
{
    /// <summary>The server now holds the edited file.</summary>
    Uploaded,
    /// <summary>The upload failed; the edits are still in the local copy, which is kept.</summary>
    Failed,
    /// <summary>The server copy changed after the file was opened and the user chose not to overwrite it.</summary>
    Declined,
}

/// <summary>Outcome of one write-back, for the UI to tell the user about.</summary>
/// <param name="FileName">The file being edited.</param>
/// <param name="Outcome">What happened.</param>
/// <param name="Error">Reason for a failure, otherwise null.</param>
/// <param name="LocalPath">The temporary copy holding the edits.</param>
public sealed record RemoteEditEventArgs(string FileName, RemoteEditOutcome Outcome, string? Error, string LocalPath)
{
    public bool Success => Outcome == RemoteEditOutcome.Uploaded;
}

/// <summary>What the user is asked when the server copy changed underneath an edit.</summary>
/// <param name="FileName">The file being edited.</param>
/// <param name="OpenedSize">Size when it was fetched.</param>
/// <param name="OpenedModified">Modification time when it was fetched.</param>
/// <param name="CurrentSize">Size on the server now.</param>
/// <param name="CurrentModified">Modification time on the server now.</param>
public sealed record RemoteEditConflict(
    string FileName, long OpenedSize, DateTime OpenedModified, long CurrentSize, DateTime CurrentModified);

/// <summary>
/// Edits a file that lives somewhere the editor cannot reach — inside an archive, on an FTP or
/// SFTP host — by round-tripping it through a temporary copy and uploading again on every save
/// (6.23).
/// </summary>
/// <remarks>
/// The alternative would be to teach every editor about the VFS, which is not possible. What is
/// possible is the trick every FTP client uses: fetch, hand the local path to the editor, watch
/// the file, put it back. The session lives until it is disposed, so the panel can keep editing
/// after navigating away — and disposing it is what stops the watcher and deletes the copy.
/// <para>
/// What makes it safe: the session holds a <b>lease</b> on the mounted file system (TRAP-05/29) so
/// leaving the mount cannot dispose the connection under a later save; every outcome is raised
/// (<see cref="Uploaded"/>) for the UI to show; a server copy that changed since it was fetched is
/// not overwritten without asking; an upload is verified and retried, and if it still fails the local
/// copy is kept rather than deleted; and closing the application flushes a save made moments earlier.
/// </para>
/// <para>
/// Every write-back — uploaded, failed or declined — is a line in the operation log (INV-10). It does
/// not run through <see cref="Operations.OperationQueue"/>: the editor starts it, and on exit it must
/// still reach the server while the queue is busy or paused (see <see cref="Operations.RemoteTransferLog"/>).
/// </para>
/// </remarks>
public sealed class RemoteEditService : IDisposable
{
    private readonly ILogger<RemoteEditService> _logger;
    private readonly Func<string, bool> _launcher;
    private readonly OperationLog? _operationLog;
    private readonly List<RemoteEditSession> _sessions = new();
    private readonly Lock _gate = new();

    public RemoteEditService(ILogger<RemoteEditService> logger, OperationLog operationLog, FileEditorLauncher editor)
        : this(logger, editor.Open, operationLog) { }

    /// <summary>Test seam: how the temporary copy is handed to an editor. Without a log nothing is journaled.</summary>
    public RemoteEditService(ILogger<RemoteEditService> logger, Func<string, bool> launcher, OperationLog? operationLog = null)
    {
        _logger = logger;
        _launcher = launcher;
        _operationLog = operationLog;
    }

    /// <summary>Raised after a save was uploaded, failed, or was declined. Carries what the UI should tell the user.</summary>
    public event EventHandler<RemoteEditEventArgs>? Uploaded;

    /// <summary>
    /// Asked (possibly on a worker thread) when the server copy changed since it was fetched; return
    /// true to overwrite it. With no handler the server copy is never overwritten.
    /// </summary>
    public Func<RemoteEditConflict, Task<bool>>? ConfirmOverwrite { get; set; }

    /// <summary>
    /// Editors write in bursts — truncate, write, flush, rename — so a save raises several
    /// change events. Uploading only after this much quiet turns a burst into one upload.
    /// </summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Pauses between upload attempts; the list length is the number of retries.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>Sessions currently open.</summary>
    public int ActiveSessions
    {
        get { lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    /// <summary>
    /// Fetches <paramref name="path"/> from <paramref name="vfs"/> into a temporary file, opens it
    /// with the system editor and starts watching it. Returns the session, or null when the file
    /// could not be fetched or the file system cannot be written back to.
    /// </summary>
    /// <param name="lease">Keeps the mounted file system alive for as long as the session lives;
    /// released when the session ends, or right away when it cannot start.</param>
    public async Task<RemoteEditSession?> BeginAsync(
        IVirtualFileSystem vfs, VfsPath path, string fileName, IDisposable? lease = null, CancellationToken ct = default)
    {
        if (!vfs.SupportsOperation(VfsOperation.Write))
        {
            _logger.LogInformation("Remote edit refused: {Scheme} is read-only", vfs.Scheme);
            lease?.Dispose();
            return null;
        }

        string temporary;
        IFileEntry? baseline = null;
        try
        {
            // What the server holds now: a later upload compares against it to notice that somebody
            // else changed the file while it was open here.
            try { baseline = await vfs.GetEntryAsync(path, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* no baseline: the conflict check is skipped, the edit still works */ }

            // A directory per session keeps the original file name, which is what the editor puts
            // in its title bar and what its syntax highlighting keys off.
            var directory = Path.Combine(Path.GetTempPath(), "tcv-edit-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, fileName);

            await using var source = await vfs.OpenReadAsync(path, ct);
            await using var target = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, ct);
        }
        catch (OperationCanceledException)
        {
            lease?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote edit: could not fetch {Path}", path.Value);
            lease?.Dispose();
            return null;
        }

        var session = new RemoteEditSession(vfs, path, temporary, baseline, lease, this, _logger);
        lock (_gate)
        {
            _sessions.Add(session);
        }

        if (!_launcher(temporary))
        {
            _logger.LogWarning("Remote edit: could not open {Path}", temporary);
            session.Dispose();
            return null;
        }

        return session;
    }

    /// <summary>
    /// Uploads every session whose local copy holds a save that has not reached the server — a save
    /// made a moment before the application closes is still waiting out its settle delay. Blocks up to
    /// <paramref name="timeout"/>; meant for shutdown, called off the UI thread's synchronisation context.
    /// </summary>
    public void FlushAll(TimeSpan timeout)
    {
        // Nobody can answer a question now: the thread that would show it is the one waiting here,
        // so asking hung the shutdown for the whole timeout. A conflict keeps the local copy instead.
        _closing = true;

        RemoteEditSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.ToArray();
        }

        if (sessions.Length == 0)
        {
            return;
        }

        try
        {
            Task.Run(() => Task.WhenAll(sessions.Select(s => s.FlushAsync()))).Wait(timeout);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote edit: flushing on exit failed");
        }
    }

    internal void RaiseUploaded(RemoteEditSession session, RemoteEditOutcome outcome, string? error)
    {
        // Journaled here rather than by whoever listens: the window wires its listener on the first F4
        // only, and the record must not depend on a UI being attached (INV-10).
        _operationLog?.Append(Operations.RemoteTransferLog.RemoteSave(
            session.LocalPath, session.RemotePath.Value, outcome, error, DateTime.Now));
        Uploaded?.Invoke(this, new RemoteEditEventArgs(session.RemoteName, outcome, error, session.LocalPath));
    }

    /// <summary>
    /// Asks <see cref="ConfirmOverwrite"/>, and hands the answer back on the thread pool (INV-06). The
    /// question is answered on the UI thread, and an <c>await</c> may continue right on the thread that
    /// completed the task — then the upload that follows the answer would open the connection and send
    /// the file from the UI thread. The continuation below is queued to the pool, and the task it
    /// returns runs its own continuations asynchronously, so the caller resumes there too (TRAP-66).
    /// </summary>
    internal Task<bool> AskOverwriteAsync(RemoteEditConflict conflict) =>
        !_closing && ConfirmOverwrite is { } ask
            ? ask(conflict).ContinueWith(
                static answer => answer.GetAwaiter().GetResult(),
                CancellationToken.None,
                TaskContinuationOptions.RunContinuationsAsynchronously,
                TaskScheduler.Default)
            : Task.FromResult(false);

    private volatile bool _closing;

    /// <summary>
    /// True once <see cref="FlushAll"/> has started: the application is closing, questions are no
    /// longer asked, and per-save dialogs would only pile up behind the closing window.
    /// </summary>
    public bool IsClosing => _closing;

    internal void Forget(RemoteEditSession session)
    {
        lock (_gate)
        {
            _sessions.Remove(session);
        }
    }

    /// <summary>Ends every session — called when the application closes.</summary>
    public void Dispose()
    {
        RemoteEditSession[] sessions;
        lock (_gate)
        {
            sessions = _sessions.ToArray();
        }

        foreach (var session in sessions)
        {
            session.Dispose();
        }
    }
}

/// <summary>
/// One file being edited through a temporary copy. Watches the copy and uploads it whenever it
/// changes, until disposed.
/// </summary>
public sealed class RemoteEditSession : IDisposable
{
    private readonly IVirtualFileSystem _vfs;
    private readonly VfsPath _remotePath;
    private readonly string _localPath;
    private readonly RemoteEditService _owner;
    private readonly ILogger _logger;
    private readonly IDisposable? _lease;
    private readonly FileSystemWatcher _watcher;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _uploadGate = new(1, 1);

    private CancellationTokenSource? _pending;
    private bool _disposed;
    private long _baselineSize = -1;
    private DateTime _baselineModified;
    private (long Length, long WriteTicks) _uploadedStamp;
    private bool _keepLocalCopy;

    internal RemoteEditSession(
        IVirtualFileSystem vfs, VfsPath remotePath, string localPath, IFileEntry? baseline,
        IDisposable? lease, RemoteEditService owner, ILogger logger)
    {
        _vfs = vfs;
        _remotePath = remotePath;
        _localPath = localPath;
        _lease = lease;
        _owner = owner;
        _logger = logger;
        if (baseline is not null)
        {
            Remember(baseline);
        }

        _uploadedStamp = StampOf(localPath);

        _watcher = new FileSystemWatcher(Path.GetDirectoryName(localPath)!, Path.GetFileName(localPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        // Editors that save atomically (JetBrains "safe write", vim) write a temporary file and
        // rename it over the original — which raises Renamed, not Changed.
        _watcher.Renamed += OnChanged;
    }

    /// <summary>Name shown in messages about this session.</summary>
    public string RemoteName => Path.GetFileName(_localPath);

    /// <summary>The temporary file the editor is working on.</summary>
    public string LocalPath => _localPath;

    /// <summary>Where the file lives on the mounted file system.</summary>
    public VfsPath RemotePath => _remotePath;

    /// <summary>How many times the file has been written back.</summary>
    public int UploadCount { get; private set; }

    /// <summary>Set by <see cref="Dispose"/> when the temporary copy was kept because it holds edits the server lacks.</summary>
    public bool KeptLocalCopy { get; private set; }

    /// <summary>True while the local copy holds edits the server does not have.</summary>
    public bool HasUnuploadedChanges => !_disposed && StampOf(_localPath) != _uploadedStamp;

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        CancellationToken token;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending?.Cancel();
            _pending = new CancellationTokenSource();
            token = _pending.Token;
        }

        _ = UploadAfterSettleAsync(token);
    }

    private async Task UploadAfterSettleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_owner.SettleDelay, ct);
        }
        catch (OperationCanceledException)
        {
            return;   // another write arrived; that one owns the upload
        }

        await UploadAsync();
    }

    /// <summary>Uploads now if the local copy differs from what was last written back.</summary>
    public async Task FlushAsync()
    {
        lock (_gate)
        {
            _pending?.Cancel();
        }

        if (HasUnuploadedChanges)
        {
            await UploadAsync();
        }
    }

    private async Task UploadAsync()
    {
        await _uploadGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            var stamp = StampOf(_localPath);
            if (stamp == _uploadedStamp)
            {
                return;   // a save burst already uploaded this content
            }

            if (!await ConfirmNoConflictAsync().ConfigureAwait(false))
            {
                _keepLocalCopy = true;
                _owner.RaiseUploaded(this, RemoteEditOutcome.Declined, null);
                return;
            }

            Exception? failure = null;
            var attempts = _owner.RetryDelays.Count + 1;
            for (var attempt = 0; attempt < attempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(_owner.RetryDelays[attempt - 1]);
                }

                try
                {
                    var length = await PutAsync();
                    await VerifyAsync(length);
                    _uploadedStamp = stamp;
                    _keepLocalCopy = false;
                    UploadCount++;
                    _logger.LogInformation("Remote edit: uploaded {Path}", _remotePath.Value);
                    _owner.RaiseUploaded(this, RemoteEditOutcome.Uploaded, null);
                    return;
                }
                catch (Exception ex)
                {
                    failure = ex;
                    _logger.LogWarning(ex, "Remote edit: upload of {Path} failed (attempt {Attempt})", _remotePath.Value, attempt + 1);
                }
            }

            // Whatever is on the server may now be a truncated copy of the file: keep the local one.
            _keepLocalCopy = true;
            _owner.RaiseUploaded(this, RemoteEditOutcome.Failed, failure?.Message);
        }
        finally
        {
            _uploadGate.Release();
        }
    }

    /// <summary>
    /// False when the server copy changed since it was fetched (or since our last upload) and the user
    /// declined to overwrite it. A server we cannot ask about never blocks the save.
    /// </summary>
    private async Task<bool> ConfirmNoConflictAsync()
    {
        if (_baselineSize < 0)
        {
            return true;
        }

        IFileEntry? current;
        try { current = await _vfs.GetEntryAsync(_remotePath); }
        catch (Exception) { return true; }

        if (current is null)
        {
            return true;   // deleted meanwhile: writing recreates it
        }

        var tolerance = string.Equals(_vfs.Scheme, "file", StringComparison.OrdinalIgnoreCase)
            ? TimeSpan.FromSeconds(2)
            : TimeSpan.FromSeconds(61);   // FTP rounds to the minute
        var changed = current.Size != _baselineSize || (current.LastModified - _baselineModified).Duration() > tolerance;
        if (!changed)
        {
            return true;
        }

        // Not back to whatever context the save started on: the upload follows (INV-06).
        return await _owner.AskOverwriteAsync(new RemoteEditConflict(
            RemoteName, _baselineSize, _baselineModified, current.Size, current.LastModified)).ConfigureAwait(false);
    }

    private async Task<long> PutAsync()
    {
        // Opened with FileShare.ReadWrite|Delete: an editor may still hold its own handle open.
        await using var source = new FileStream(
            _localPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = source.Length;
        await using var target = await _vfs.OpenWriteAsync(_remotePath, overwrite: true);
        await source.CopyToAsync(target);
        return length;
    }

    /// <summary>A transfer that ended early leaves a short file behind and no exception: compare sizes.</summary>
    private async Task VerifyAsync(long expectedLength)
    {
        IFileEntry? stored;
        try { stored = await _vfs.GetEntryAsync(_remotePath); }
        catch (Exception) { return; }   // cannot verify on this server: trust the transfer

        if (stored is null)
        {
            throw new IOException("The file is missing on the server after the upload.");
        }

        if (stored.Size != expectedLength)
        {
            throw new IOException($"The server holds {stored.Size:N0} bytes, expected {expectedLength:N0}.");
        }

        Remember(stored);
    }

    private void Remember(IFileEntry entry)
    {
        _baselineSize = entry.Size;
        _baselineModified = entry.LastModified;
    }

    private static (long Length, long WriteTicks) StampOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : (-1, 0);
        }
        catch (IOException)
        {
            return (-1, 0);
        }
    }

    /// <summary>
    /// Stops watching and releases the mount. The temporary copy is deleted — unless it holds edits
    /// that never reached the server, in which case it is the only copy of them and stays.
    /// </summary>
    public void Dispose()
    {
        bool keep;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            keep = _keepLocalCopy || HasUnuploadedChanges;
            _disposed = true;
            _pending?.Cancel();
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnChanged;
        _watcher.Created -= OnChanged;
        _watcher.Renamed -= OnChanged;
        _watcher.Dispose();
        _owner.Forget(this);
        _lease?.Dispose();

        if (keep)
        {
            KeptLocalCopy = true;
            _logger.LogWarning("Remote edit: keeping {Path}, it holds edits that were not uploaded", _localPath);
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(_localPath);
            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // The editor may still hold the file; a leftover temp directory is not worth a fuss.
        }
        catch (UnauthorizedAccessException) { }
    }
}
