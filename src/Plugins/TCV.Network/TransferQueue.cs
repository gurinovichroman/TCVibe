using System.Collections.ObjectModel;
using FluentFTP;
using Renci.SshNet;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// Manages an ordered queue of FTP/SFTP/WebDAV file transfers with pause, resume, cancel and retry.
/// <para>
/// Resume strategy:
///   FTP    — <c>DownloadFile</c>/<c>UploadFile</c> with <see cref="FtpLocalExists.Resume"/> /
///            <see cref="FtpRemoteExists.Resume"/>; FluentFTP issues a REST command internally.
///   SFTP   — seeks the read stream to <see cref="TransferItem.BytesTransferred"/> and opens the
///            write stream in append mode so already-transferred bytes are skipped.
///   WebDAV — downloads continue with a <c>Range</c> request when the server honours it; uploads
///            restart (a PUT cannot be resumed).
/// A download only resumes onto a local file whose length is exactly what was recorded; anything
/// else starts over, so a file the queue did not write is never extended.
/// </para>
/// </summary>
/// <remarks>
/// Workers run on the thread pool while the window reads <see cref="Items"/> on the UI thread.
/// <see cref="Items"/> is therefore a UI-only mirror, changed through the synchronisation context
/// given at construction; the worker reads its own list under a lock. Every change of an item's
/// status that decides who owns it — picked by the worker, paused, resumed, cancelled, retried,
/// ended — happens under that lock too, and the worker owns an item exactly while its token is set.
/// </remarks>
public sealed class TransferQueue : IDisposable
{
    private readonly object _sync = new();
    private readonly List<TransferItem> _all = [];
    private readonly SynchronizationContext? _ui;
    private readonly HostTrustPolicy? _trust;
    private readonly CancellationTokenSource _disposeCts = new();
    private int _pumping;

    /// <summary>Cleanups of transfers cancelled while nothing was running them, by item; under <see cref="_sync"/>.</summary>
    private readonly Dictionary<Guid, Task> _cleanups = [];

    /// <summary>Completes when the run the worker took last has ended and been reported; under <see cref="_sync"/>.</summary>
    private Task? _runEnded;

    private readonly Func<Exception, string>? _describeError;

    /// <param name="uiContext">Context that owns <see cref="Items"/> (the UI thread's); null runs synchronously.</param>
    /// <param name="trust">Trust policy for SFTP host keys and FTPS certificates; defaults to <see cref="NetworkSecurity.Default"/>.</param>
    /// <param name="describeError">Turns a failure into the text the window shows (the application localises it);
    /// null shows the exception's own message.</param>
    public TransferQueue(
        SynchronizationContext? uiContext = null, HostTrustPolicy? trust = null, Func<Exception, string>? describeError = null)
    {
        _ui = uiContext;
        _trust = trust;
        _describeError = describeError;
    }

    /// <summary>What the transfer window shows. Bind to it; do not mutate it from outside the queue.</summary>
    public ObservableCollection<TransferItem> Items { get; } = [];

    /// <summary>
    /// Raised once per run when a transfer stops for good: completed, failed or cancelled. A pause is
    /// not an end, and a retry is a new run. This is how the application records transfers in its
    /// operation log (INV-10): the queue runs beside the application's operation queue, not through it.
    /// Raised on a worker thread; a listener that throws is ignored.
    /// </summary>
    public event EventHandler<TransferFinishedEventArgs>? Finished;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Adds an item and starts processing if the queue is idle.</summary>
    public void Enqueue(TransferItem item)
    {
        lock (_sync)
        {
            _all.Add(item);
        }

        OnUi(() => Items.Add(item));
        Kick();
    }

    /// <summary>Test seam: lists an item without starting the worker, so a test can observe a queued item.</summary>
    internal void AddWithoutStarting(TransferItem item)
    {
        lock (_sync)
        {
            _all.Add(item);
        }

        OnUi(() => Items.Add(item));
    }

    /// <summary>Pauses a running transfer. It can be resumed with <see cref="ResumeAsync"/>.</summary>
    public void Pause(Guid id)
    {
        CancellationTokenSource? running;
        lock (_sync)
        {
            var item = FindLocked(id);
            if (item is null || item.Status != TransferStatus.Transferring)
            {
                return;
            }

            item.Status = TransferStatus.Paused;
            running = item.Cts;
        }

        TryCancel(running);
    }

    /// <summary>Resumes a paused transfer from where it stopped.</summary>
    public Task ResumeAsync(Guid id)
    {
        lock (_sync)
        {
            var item = FindLocked(id);
            if (item is null || item.Status != TransferStatus.Paused)
            {
                return Task.CompletedTask;
            }

            // A worker still unwinding from the pause sees Pending and leaves the run open; the item
            // is picked up again once it lets go (TakeNext skips an item a worker still holds).
            item.Status = TransferStatus.Pending;
        }

        Kick();
        return Task.CompletedTask;
    }

    /// <summary>Cancels a pending, running or paused transfer. A partial download is deleted.</summary>
    public void Cancel(Guid id)
    {
        CancellationTokenSource? running = null;
        lock (_sync)
        {
            if (FindLocked(id) is { } item)
            {
                running = CancelLocked(item);
            }
        }

        TryCancel(running);
    }

    /// <summary>
    /// Cancels every transfer that has not ended — waiting, running or paused — and completes once each
    /// has cleaned up after itself and been reported to <see cref="Finished"/>. For shutdown: the queue
    /// lives in memory only, so a paused transfer cannot be resumed after the process ends, and its
    /// partial file must not stay behind without a line in the log.
    /// </summary>
    public Task CancelAllAsync()
    {
        var wait = new List<Task>();
        var tokens = new List<CancellationTokenSource>();
        lock (_sync)
        {
            var running = false;
            foreach (var item in _all)
            {
                running |= item.Cts is not null;
                if (CancelLocked(item) is { } token)
                {
                    tokens.Add(token);
                }
            }

            // What a worker or a cleanup is still doing ends the runs cancelled above: wait for it.
            if (running && _runEnded is { } run)
            {
                wait.Add(run);
            }

            wait.AddRange(_cleanups.Values);
        }

        tokens.ForEach(TryCancel);
        return Task.WhenAll(wait);
    }

    /// <summary>
    /// The one place a transfer is cancelled; the caller holds <see cref="_sync"/>. The status is read
    /// and written under the same lock as the worker's pick (<see cref="TakeNext"/>), so a cancel can
    /// no longer slip in between the item being chosen and the transfer starting and get lost.
    /// </summary>
    /// <returns>The token of the worker that holds the item, for the caller to cancel once it has
    /// released the lock (a cancellation callback may run the worker's own code inline); null when
    /// nothing is running.</returns>
    private CancellationTokenSource? CancelLocked(TransferItem item)
    {
        if (item.IsFinished)
        {
            return null;
        }

        item.Status = TransferStatus.Cancelled;

        // A token means a worker holds the item: it stops, cleans up and reports the run itself.
        if (item.Cts is { } running)
        {
            return running;
        }

        // Waiting or paused: nothing is running, so the partial file is removed (and the run
        // reported) here. The worker does not pick the item up again until this is done.
        var attempt = item.Attempt;
        Task? cleanup = null;
        cleanup = Task.Run(async () =>
        {
            try
            {
                await DiscardPartialAsync(item);
            }
            finally
            {
                lock (_sync)
                {
                    // Only this cleanup's own entry: retried and cancelled again meanwhile, the item has
                    // a newer cleanup there, which must still be waited for.
                    if (_cleanups.TryGetValue(item.Id, out var current) && ReferenceEquals(current, cleanup))
                    {
                        _cleanups.Remove(item.Id);
                    }
                }

                ReportFinished(item, attempt, TransferStatus.Cancelled, null);
                Kick();
            }
        });
        _cleanups[item.Id] = cleanup;
        return null;
    }

    /// <summary>Cancels a token a worker may be disposing at this very moment. Never under <see cref="_sync"/>.</summary>
    private static void TryCancel(CancellationTokenSource? cts)
    {
        try
        {
            cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker finished first: there was nothing left to stop.
        }
    }

    /// <summary>Puts a failed or cancelled transfer back in the queue.</summary>
    public void Retry(Guid id)
    {
        lock (_sync)
        {
            // Not while a worker still holds it, unwinding from a cancel: it would read the new Pending as
            // a pause, and the cancelled run would never be reported nor its partial file removed.
            var item = FindLocked(id);
            if (item is null || item.Cts is not null
                || item.Status is not (TransferStatus.Failed or TransferStatus.Cancelled))
            {
                return;
            }

            // A cancelled item had its partial file removed; a failed one keeps it and can continue.
            if (item.Status == TransferStatus.Cancelled)
            {
                item.BytesTransferred = 0;
            }

            item.BeginNewAttempt();
            item.ErrorMessage = null;
            item.Status = TransferStatus.Pending;
        }

        Kick();
    }

    /// <summary>Removes finished (completed, failed, cancelled) items from the list.</summary>
    public void ClearFinished()
    {
        List<TransferItem> finished;
        lock (_sync)
        {
            finished = _all.Where(i => i.IsFinished && i.Cts is null && !_cleanups.ContainsKey(i.Id)).ToList();
            foreach (var item in finished)
            {
                _all.Remove(item);
            }
        }

        OnUi(() =>
        {
            foreach (var item in finished)
            {
                Items.Remove(item);
            }
        });
    }

    /// <summary>
    /// True while something is waiting, running or paused — the window warns before closing over it.
    /// A paused transfer counts: closing ends it (<see cref="CancelAllAsync"/>), it cannot be resumed later.
    /// </summary>
    public bool HasActiveWork
    {
        get
        {
            lock (_sync)
            {
                return _all.Any(i => i.Status is TransferStatus.Pending or TransferStatus.Transferring or TransferStatus.Paused);
            }
        }
    }

    // ── Processing loop ───────────────────────────────────────────────────────

    private void Kick() => _ = Task.Run(PumpAsync);

    private async Task PumpAsync()
    {
        // One pump at a time. Whoever loses the race just leaves; the winner re-checks for work
        // after releasing, so an item that became Pending in that gap is never stranded.
        if (Interlocked.Exchange(ref _pumping, 1) == 1)
        {
            return;
        }

        try
        {
            while (!_disposeCts.IsCancellationRequested && TakeNext() is { } run)
            {
                await ExecuteTransferAsync(run);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _pumping, 0);
            if (!_disposeCts.IsCancellationRequested && HasPending())
            {
                Kick();
            }
        }
    }

    /// <summary>One run a worker has taken: the item, its token, the attempt and the run's end signal.</summary>
    private sealed record Run(TransferItem Item, CancellationTokenSource Cts, int Attempt, TaskCompletionSource Ended);

    /// <summary>
    /// Takes the next waiting item and marks it running, in one step under <see cref="_sync"/>. A cancel
    /// either comes first (the item is no longer Pending and is skipped) or finds the token published
    /// and stops the transfer through it — there is no moment in between (TRAP-65).
    /// </summary>
    private Run? TakeNext()
    {
        lock (_sync)
        {
            if (_disposeCts.IsCancellationRequested)
            {
                return null;
            }

            var item = _all.FirstOrDefault(IsReadyLocked);
            if (item is null)
            {
                return null;
            }

            var run = new Run(
                item,
                CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token),
                item.Attempt,
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            item.Cts = run.Cts;
            item.Status = TransferStatus.Transferring;
            _runEnded = run.Ended.Task;
            return run;
        }
    }

    private bool HasPending()
    {
        lock (_sync)
        {
            return _all.Any(IsReadyLocked);
        }
    }

    /// <summary>Waiting, and neither a worker nor a cleanup still holds it.</summary>
    private bool IsReadyLocked(TransferItem item) =>
        item.Status == TransferStatus.Pending && item.Cts is null && !_cleanups.ContainsKey(item.Id);

    private async Task ExecuteTransferAsync(Run run)
    {
        var item = run.Item;
        var ct = run.Cts.Token;
        var completed = false;
        Exception? failure = null;

        try
        {
            try
            {
                // A read-only connection opens nothing for writing, here either: the queue makes its own
                // connection and would otherwise slip past the panel's guard.
                if (item.Connection.ReadOnly && item.Direction == TransferDirection.Upload)
                {
                    throw new ReadOnlyConnectionException();
                }

                switch (item.Connection.Protocol)
                {
                    case NetworkProtocol.Sftp:
                        await ExecuteSftpTransferAsync(item, ct);
                        break;
                    case NetworkProtocol.Ftp or NetworkProtocol.Ftps or NetworkProtocol.FtpsImplicit:
                        await ExecuteFtpTransferAsync(item, ct);
                        break;
                    default:
                        // WebDAV, object storage, the drives: HTTP underneath, the file system does the protocol.
                        await ExecuteHttpTransferAsync(item, ct);
                        break;
                }

                completed = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Paused, cancelled or shutting down: which one is decided below, under the lock.
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            string? error = null;
            if (failure is not null)
            {
                // Before the lock that hands the item back: a describer that threw here would leave the
                // item held (its token set) and Transferring for good.
                try
                {
                    error = _describeError?.Invoke(failure);
                }
                catch (Exception)
                {
                    // The exception's own words below.
                }

                error ??= (NetworkErrors.Find<HostKeyChangedException>(failure) ?? failure).Message;
            }

            TransferStatus? ended;
            lock (_sync)
            {
                item.Cts = null;
                ended = completed ? TransferStatus.Complete
                    // A pause (perhaps already resumed) is not an end, even when the connection it cut
                    // surfaced as an I/O error rather than as a cancellation.
                    : item.Status is TransferStatus.Paused or TransferStatus.Pending ? null
                    : item.Status == TransferStatus.Cancelled || ct.IsCancellationRequested ? TransferStatus.Cancelled
                    : TransferStatus.Failed;

                if (ended is { } status)
                {
                    item.Status = status;
                    if (status == TransferStatus.Failed)
                    {
                        item.ErrorMessage = error;
                    }
                }
            }

            run.Cts.Dispose();

            if (ended == TransferStatus.Cancelled)
            {
                await DiscardPartialAsync(item);
            }

            if (ended is { } end)
            {
                ReportFinished(item, run.Attempt, end, end == TransferStatus.Failed ? error : null);
            }
        }
        finally
        {
            run.Ended.TrySetResult();
        }
    }

    /// <summary>Tells the listeners that a run has ended — once per run, whichever thread gets there first.</summary>
    private void ReportFinished(TransferItem item, int attempt, TransferStatus status, string? error)
    {
        if (!item.TryClaimEnd(attempt))
        {
            return;
        }

        try
        {
            Finished?.Invoke(this, new TransferFinishedEventArgs(item, status, error));
        }
        catch (Exception)
        {
            // The log is a record of the transfer, not part of it: a listener's failure must not fail
            // the item or stop the worker before the next one.
        }
    }

    // ── FTP transfer (FluentFTP, supports REST-based resume) ──────────────────

    private async Task ExecuteFtpTransferAsync(TransferItem item, CancellationToken ct)
    {
        // A name FluentFTP would send as another one is refused before anything is opened (TRAP-94).
        FtpFileSystem.ToSendablePath(item.Direction == TransferDirection.Download ? item.SourcePath : item.DestinationPath);

        using var client = FtpFileSystem.CreateClient(item.Connection, _trust);
        try
        {
            await client.Connect(ct);
        }
        catch (Exception ex) when (NetworkErrors.Find<HostKeyChangedException>(ex) is { } changed)
        {
            throw changed;
        }

        var progress = new FtpProgressWrapper(item);

        if (item.Direction == TransferDirection.Download)
        {
            ReconcileSource(item,
                await client.GetFileSize(item.SourcePath, -1, ct),
                await client.GetModifiedTime(item.SourcePath, ct));
            var resuming = CanResumeDownload(item);

            // Resume only continues a transfer this queue started, onto a file of exactly the length
            // it recorded. Any other existing file is unrelated: appending the remote tail to it would
            // silently produce a corrupt hybrid, and replacing it needs the user's say-so.
            if (!resuming && File.Exists(item.DestinationPath) && !item.OverwriteExisting && !item.OwnsLocalFile)
            {
                throw new TransferTargetExistsException(item.DestinationPath, onServer: false);
            }

            item.OwnsLocalFile = true;
            var status = await client.DownloadFile(
                item.DestinationPath,
                item.SourcePath,
                resuming ? FtpLocalExists.Resume : FtpLocalExists.Overwrite,
                token: ct,
                progress: progress);
            EnsureFtpSucceeded(status, item.SourcePath);
        }
        else
        {
            ReconcileLocalSource(item);
            item.LearnTotal(new FileInfo(item.SourcePath).Length);
            var resuming = item.BytesTransferred > 0;

            // The existence check comes first so that "the queue owns the remote file" only ever
            // becomes true once it is really about to write there.
            if (!resuming && !item.OverwriteExisting && !item.OwnsRemoteFile
                && await client.FileExists(item.DestinationPath, ct))
            {
                throw new TransferTargetExistsException(item.DestinationPath, onServer: true);
            }

            item.OwnsRemoteFile = true;
            var status = await client.UploadFile(
                item.SourcePath,
                item.DestinationPath,
                resuming ? FtpRemoteExists.Resume : FtpRemoteExists.Overwrite,
                token: ct,
                progress: progress);

            EnsureFtpSucceeded(status, item.SourcePath);
        }
    }

    private static void EnsureFtpSucceeded(FtpStatus status, string path)
    {
        if (status == FtpStatus.Failed)
        {
            throw new TransferFailedException(path);
        }
    }

    // ── SFTP transfer (SSH.NET, manual seek for resume) ───────────────────────

    private async Task ExecuteSftpTransferAsync(TransferItem item, CancellationToken ct)
    {
        using var client = SftpFileSystem.CreateClientInternal(item.Connection, _trust);
        await Task.Run(() => SftpFileSystem.ConnectClient(client), ct);

        if (item.Direction == TransferDirection.Download)
        {
            var attributes = client.GetAttributes(item.SourcePath);
            ReconcileSource(item, attributes.Size, attributes.LastWriteTimeUtc);
            var resumeOffset = CanResumeDownload(item) ? item.BytesTransferred : 0;
            if (resumeOffset == 0 && File.Exists(item.DestinationPath) && !item.OverwriteExisting && !item.OwnsLocalFile)
            {
                throw new TransferTargetExistsException(item.DestinationPath, onServer: false);
            }

            item.LearnTotal(attributes.Size);

            using var remoteStream = client.OpenRead(item.SourcePath);
            if (resumeOffset > 0)
            {
                remoteStream.Seek(resumeOffset, SeekOrigin.Begin);
            }

            // Open local file in append mode so existing bytes are preserved
            item.OwnsLocalFile = true;
            using var localStream = new FileStream(
                item.DestinationPath,
                resumeOffset > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            await CopyWithProgressAsync(remoteStream, localStream, resumeOffset, item, ct);
        }
        else
        {
            // The bytes recorded are what was handed to the stream, not what the server has: after a
            // dropped connection the buffered tail never arrived, and seeking past the real end would
            // leave a hole of zeros in the middle of the file. Continue from what is really there.
            ReconcileLocalSource(item);
            var resuming = item.BytesTransferred > 0;
            var resumeOffset = item.BytesTransferred;
            if (resuming)
            {
                var onServer = client.Exists(item.DestinationPath) ? client.GetAttributes(item.DestinationPath).Size : 0;
                resumeOffset = Math.Min(resumeOffset, onServer);
                item.BytesTransferred = resumeOffset;
            }

            if (!resuming && !item.OverwriteExisting && !item.OwnsRemoteFile && client.Exists(item.DestinationPath))
            {
                throw new TransferTargetExistsException(item.DestinationPath, onServer: true);
            }

            item.OwnsRemoteFile = true;

            // Upload: read local file from offset, write to remote past the part already there
            item.LearnTotal(new FileInfo(item.SourcePath).Length);

            using var localStream = new FileStream(
                item.SourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                useAsync: true);

            if (resumeOffset > 0)
            {
                localStream.Seek(resumeOffset, SeekOrigin.Begin);
            }

            using var remoteStream = resumeOffset > 0
                ? client.OpenWrite(item.DestinationPath)
                : client.Create(item.DestinationPath);

            if (resumeOffset > 0)
            {
                remoteStream.Seek(resumeOffset, SeekOrigin.Begin);
            }

            await CopyWithProgressAsync(localStream, remoteStream, resumeOffset, item, ct);
        }
    }

    // ── HTTP-based transfer: WebDAV, S3, Azure Blob, the drives ───────────────

    private async Task ExecuteHttpTransferAsync(TransferItem item, CancellationToken ct)
    {
        using var fs = NetworkPlugin.CreateFileSystem(item.Connection);

        if (item.Direction == TransferDirection.Download)
        {
            if (fs is not IRangeReader ranged)
            {
                // No ranged reads: every attempt fetches the whole file.
                item.BytesTransferred = 0;
                if (File.Exists(item.DestinationPath) && !item.OverwriteExisting && !item.OwnsLocalFile)
                {
                    throw new TransferTargetExistsException(item.DestinationPath, onServer: false);
                }

                await using var whole = await fs.OpenReadAsync(new VfsPath(item.SourcePath), ct);
                item.OwnsLocalFile = true;
                await using var file = new FileStream(
                    item.DestinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyWithProgressAsync(whole, file, 0, item, ct);
                return;
            }

            var wanted = CanResumeDownload(item) ? item.BytesTransferred : 0;
            if (wanted > 0 && item.ResumeValidator is null)
            {
                // The server named no ETag or Last-Modified: nothing proves the file is the one the
                // partial copy came from, so it starts over rather than splice two versions.
                wanted = 0;
                item.BytesTransferred = 0;
            }

            if (wanted == 0 && File.Exists(item.DestinationPath) && !item.OverwriteExisting && !item.OwnsLocalFile)
            {
                throw new TransferTargetExistsException(item.DestinationPath, onServer: false);
            }

            // If-Range: a server whose file changed since the first request sends the whole new file
            // (200) instead of the rest of the old one, and the download restarts from byte zero.
            var (stream, offset, total, validator) = await ranged.OpenReadFromAsync(
                new VfsPath(item.SourcePath), wanted, wanted > 0 ? item.ResumeValidator : null, ct);
            item.ResumeValidator = validator;
            await using (stream)
            {
                // The server may have ignored the Range header and sent the whole file.
                item.LearnTotal(total);
                item.OwnsLocalFile = true;
                using var local = new FileStream(
                    item.DestinationPath,
                    offset > 0 ? FileMode.Append : FileMode.Create,
                    FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await CopyWithProgressAsync(stream, local, offset, item, ct);
            }

            return;
        }

        // A PUT cannot be resumed: an upload always starts from the first byte.
        item.BytesTransferred = 0;
        await using var source = new FileStream(
            item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var target = (Stream)await fs.OpenWriteAsync(
            new VfsPath(item.DestinationPath), item.OverwriteExisting || item.OwnsRemoteFile, ct);
        try
        {
            item.LearnTotal(source.Length);

            // The body is collected in a temporary file and then sent, so each byte travels twice and
            // each pass counts for half: counting only the first put the item at 100 % before the
            // upload had even started.
            var deferred = target as IDeferredUploadStream;
            await CopyWithProgressAsync(source, target, 0, item, ct, passes: deferred is null ? 1 : 2);
            if (deferred is not null)
            {
                var collected = target.Length;
                await deferred.CommitAsync(
                    new InlineProgress<long>(sent => item.BytesTransferred = (collected + sent) / 2), ct);
            }
        }
        catch
        {
            (target as IAbortableWriteStream)?.Abort();
            throw;
        }
        finally
        {
            await target.DisposeAsync();
        }
    }

    // ── Copy helper ───────────────────────────────────────────────────────────

    /// <param name="passes">
    /// How many times each byte travels in the whole transfer, this copy being the first: the item
    /// shows this pass's share of the work (2 for a body that is collected first and uploaded after).
    /// </param>
    private static async Task CopyWithProgressAsync(
        Stream source, Stream destination,
        long startOffset,
        TransferItem item,
        CancellationToken ct,
        int passes = 1)
    {
        var buffer = new byte[81920];
        long copied = startOffset;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            item.BytesTransferred = copied / passes;
        }

        await destination.FlushAsync(ct);
        item.BytesTransferred = copied / passes;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Records what the source looks like now, and forgets the progress made so far when it no longer
    /// matches what those bytes were read from: resuming would splice the head of one version of the
    /// file onto the tail of another, and report the hybrid as done.
    /// </summary>
    internal static void ReconcileSource(TransferItem item, long size, DateTime modified)
    {
        var stamp = (size, modified.ToUniversalTime());
        if (item.BytesTransferred > 0 && item.SourceStamp is { } recorded && recorded != stamp)
        {
            item.BytesTransferred = 0;
        }

        item.SourceStamp = stamp;
    }

    /// <summary><see cref="ReconcileSource"/> for an upload: the source is the local file.</summary>
    private static void ReconcileLocalSource(TransferItem item)
    {
        var info = new FileInfo(item.SourcePath);
        ReconcileSource(item, info.Length, info.LastWriteTimeUtc);
    }

    /// <summary>A download continues only onto a local file of exactly the recorded length.</summary>
    private static bool CanResumeDownload(TransferItem item)
    {
        if (item.BytesTransferred <= 0)
        {
            return false;
        }

        try
        {
            var info = new FileInfo(item.DestinationPath);
            if (info.Exists && info.Length == item.BytesTransferred)
            {
                return true;
            }
        }
        catch (IOException)
        {
            // Unreadable: start over.
        }

        item.BytesTransferred = 0;
        return false;
    }

    /// <summary>
    /// Removes what a cancelled transfer left behind: the partial local file of a download, or the
    /// partial remote file of an upload (best effort — the server may be gone by now).
    /// </summary>
    private async Task DiscardPartialAsync(TransferItem item)
    {
        if (item.BytesTransferred == 0 && item.Direction == TransferDirection.Upload)
        {
            return;
        }

        try
        {
            if (item.Direction == TransferDirection.Download)
            {
                // Only what this queue wrote: the length matches its own accounting, or it was
                // created for this very transfer. An unrelated pre-existing file is never touched.
                if (item.OwnsLocalFile && File.Exists(item.DestinationPath))
                {
                    File.Delete(item.DestinationPath);
                }
            }
            else
            {
                await DeleteRemotePartialAsync(item);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a partial file behind is bad; failing the cancel because of it would be worse.
        }

        item.OwnsLocalFile = false;
        item.BytesTransferred = 0;
    }

    private async Task DeleteRemotePartialAsync(TransferItem item)
    {
        try
        {
            switch (item.Connection.Protocol)
            {
                case NetworkProtocol.Sftp:
                    using (var sftp = SftpFileSystem.CreateClientInternal(item.Connection, _trust))
                    {
                        SftpFileSystem.ConnectClient(sftp);
                        if (sftp.Exists(item.DestinationPath))
                        {
                            sftp.DeleteFile(item.DestinationPath);
                        }
                    }

                    break;
                case NetworkProtocol.Ftp or NetworkProtocol.Ftps or NetworkProtocol.FtpsImplicit:
                    // Never a delete of another file than the one this queue wrote (TRAP-94).
                    FtpFileSystem.ToSendablePath(item.DestinationPath);
                    using (var ftp = FtpFileSystem.CreateClient(item.Connection, _trust))
                    {
                        await ftp.Connect();
                        if (await ftp.FileExists(item.DestinationPath))
                        {
                            await ftp.DeleteFile(item.DestinationPath);
                        }
                    }

                    break;
            }
        }
        catch (Exception)
        {
            // Best effort by design; see DiscardPartial.
        }
    }

    private TransferItem? FindLocked(Guid id) => _all.FirstOrDefault(i => i.Id == id);

    private void OnUi(Action action)
    {
        if (_ui is null)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }

    public void Dispose()
    {
        _disposeCts.Cancel();

        // Under the lock: TakeNext links to the token only after seeing it not cancelled, under the same lock.
        lock (_sync)
        {
            _disposeCts.Dispose();
        }
    }

    // ── FluentFTP progress adapter ────────────────────────────────────────────

    private sealed class FtpProgressWrapper : IProgress<FtpProgress>
    {
        private readonly TransferItem _item;

        public FtpProgressWrapper(TransferItem item) => _item = item;

        public void Report(FtpProgress value)
        {
            _item.BytesTransferred = value.TransferredBytes;
            if (value.Progress > 0)
            {
                _item.LearnTotal((long)(value.TransferredBytes / (value.Progress / 100.0)));
            }
        }
    }
}

/// <summary>One run of a transfer has ended: which transfer, and how its run ended.</summary>
public sealed class TransferFinishedEventArgs(TransferItem item, TransferStatus status, string? error) : EventArgs
{
    /// <summary>The transfer. Its paths and direction are fixed; its status may already have moved on (a retry).</summary>
    public TransferItem Item { get; } = item;

    /// <summary>How this run ended: <see cref="TransferStatus.Complete"/>, <see cref="TransferStatus.Failed"/> or <see cref="TransferStatus.Cancelled"/>.</summary>
    public TransferStatus Status { get; } = status;

    /// <summary>What the queue window showed for a failure; null otherwise.</summary>
    public string? Error { get; } = error;
}

/// <summary>The transfer would replace a file the user did not agree to replace.</summary>
public sealed class TransferTargetExistsException : IOException
{
    public TransferTargetExistsException(string path, bool onServer)
        : base(onServer ? $"\"{path}\" already exists on the server." : $"\"{path}\" already exists.")
    {
        Path = path;
        OnServer = onServer;
    }

    /// <summary>The destination that is already there.</summary>
    public string Path { get; }

    /// <summary>True for an upload (the file is on the server), false for a download.</summary>
    public bool OnServer { get; }
}

/// <summary>The FTP library reported a failed transfer without saying why.</summary>
public sealed class TransferFailedException : IOException
{
    public TransferFailedException(string path)
        : base($"The FTP transfer of \"{path}\" failed.")
    {
        Path = path;
    }

    /// <summary>The file that was being transferred.</summary>
    public string Path { get; }
}
