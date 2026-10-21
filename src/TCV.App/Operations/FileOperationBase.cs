using System.Diagnostics;
using System.IO;
using TCV.App.FileSystem;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

public abstract class FileOperationBase : IFileOperation
{
    public Guid Id { get; } = Guid.NewGuid();

    private OperationState _state = OperationState.Pending;

    // protected internal: OperationQueue records the final outcome so callers of
    // EnqueueAsync can tell a completed operation from a failed or cancelled one.
    public OperationState State
    {
        get => _state;
        protected internal set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// <see cref="State"/> moved on: the operation left the queue and started, or ended. Raised on the
    /// thread that runs it (the queue's worker, TRAP-47), so the UI marshals.
    /// </summary>
    public event EventHandler? StateChanged;

    public Exception? FailureException { get; protected internal set; }
    public CancellationTokenSource Cts { get; } = new();
    public PauseHandle? PauseHandle { get; set; }

    /// <summary>
    /// Makes <paramref name="token"/> cancel this operation. The queue watches <see cref="Cts"/>,
    /// so a progress dialog's own token has to be forwarded here or its Cancel button does nothing.
    /// </summary>
    public void LinkCancellation(CancellationToken token) => token.Register(Cts.Cancel);

    public abstract Task ExecuteAsync(CancellationToken ct);

    /// <summary>
    /// A user-facing summary of what this operation did, written to the operation log once it
    /// finishes. Null suppresses logging (the default — operations opt in by overriding this).
    /// </summary>
    public virtual OperationLogDescriptor? LogDescriptor => null;

    /// <summary>
    /// Who requested this operation, when it was not the user directly (an agent's client id, 6.13).
    /// A plain property rather than part of <see cref="LogDescriptor"/> so that tagging an operation
    /// as agent-initiated does not require touching every operation's descriptor — the origin is a
    /// fact about who enqueued it, not about what it does.
    /// </summary>
    public string? Origin { get; set; }

    /// <summary>
    /// The lane of the queue that runs it: an operation with a server on one side goes beside the local
    /// ones (ADR-0038). Operations that do not say run in the local lane, one at a time as always.
    /// </summary>
    public virtual OperationLane Lane => OperationLane.Local;

    /// <summary>The network lane when one of <paramref name="fileSystems"/> is a server or a cloud store.</summary>
    protected static OperationLane LaneOf(params IVirtualFileSystem[] fileSystems) =>
        fileSystems.Any(fs => IsRemoteServer(fs) || IsCloudStorage(fs)) ? OperationLane.Network : OperationLane.Local;

    // ── Skips, warnings and per-item errors ───────────────────────────────────

    private int _skippedCount;

    /// <summary>
    /// Items left untouched because the user chose Skip, "only if newer" said no, an item failed and
    /// was skipped, or it was a junction. A skipped operation still ends
    /// <see cref="OperationState.Completed"/>, so callers that delete the source afterwards (cut and
    /// paste) must look at this before treating the operation as whole.
    /// </summary>
    public int SkippedCount => Volatile.Read(ref _skippedCount);

    protected void CountSkipped() => Interlocked.Increment(ref _skippedCount);

    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _warnings = new();

    /// <summary>
    /// Things the user should hear about although the operation completed: junctions left out,
    /// items skipped after an error.
    /// </summary>
    public IReadOnlyCollection<string> Warnings => _warnings.ToArray();

    protected void AddWarning(string message) => _warnings.Enqueue(message);

    /// <summary>
    /// Asked when one item fails with an IO or access error. Without a handler the first error
    /// aborts the operation, as it always did (tests, agents and other headless callers).
    /// </summary>
    public OperationErrorHandler? ErrorHandler { get; set; }

    /// <summary>
    /// The failure was already put to the user in the retry / skip / abort prompt, and they chose
    /// Abort. An error box repeating the same message afterwards only makes them read it twice.
    /// </summary>
    public bool FailureAlreadyShown { get; private set; }

    /// <summary>True when the operation failed with an error the user has not seen yet.</summary>
    public bool HasUnreportedFailure =>
        State == OperationState.Failed && FailureException is not null && !FailureAlreadyShown;

    private volatile bool _skipAllErrors;

    /// <summary>
    /// Runs <paramref name="action"/> for one item. An IO or access error is put to the user:
    /// retry, skip or abort. Returns true when the item was processed, false when it was skipped.
    /// </summary>
    protected async Task<bool> RunGuardedAsync(VfsPath item, Func<Task> action, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await action();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           && ex is not DestinationInsideSourceException)
            {
                ct.ThrowIfCancellationRequested();
                if (ErrorHandler is null) throw;

                var resolution = await AskAboutErrorAsync(item, ex, ct);

                switch (resolution)
                {
                    case OperationErrorResolution.Retry:
                        continue;
                    case OperationErrorResolution.SkipAll:
                        _skipAllErrors = true;
                        CountSkipped();
                        AddWarning($"{item.FileName}: {ex.Message}");
                        return false;
                    case OperationErrorResolution.Skip:
                        CountSkipped();
                        AddWarning($"{item.FileName}: {ex.Message}");
                        return false;
                    default:
                        // The operation fails with this error, but the user has just read it in the
                        // prompt and chose Abort: callers must not show it a second time.
                        FailureAlreadyShown = true;
                        throw;
                }
            }
        }
    }

    private readonly SemaphoreSlim _promptGate = new(1, 1);

    /// <summary>
    /// One prompt at a time: a parallel copy hits errors on several threads, and without the gate the
    /// dialogs stacked on top of each other and "Skip all" did not reach the ones already queued.
    /// </summary>
    private async Task<OperationErrorResolution> AskAboutErrorAsync(VfsPath item, Exception ex, CancellationToken ct)
    {
        await _promptGate.WaitAsync(ct);
        try
        {
            if (_skipAllErrors) return OperationErrorResolution.Skip;
            return await ErrorHandler!(
                new OperationErrorInfo(OperationPaths.ToDisplay(item.Value), ex.Message, ex), ct);
        }
        finally
        {
            _promptGate.Release();
        }
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Streams one file between two — possibly different — file systems, awaiting
    /// <see cref="PauseHandle"/> and reporting progress per chunk. A copy inside one file
    /// system delegates to the file system itself, unless pausing is asked for: only the
    /// chunk loop can honour it.
    /// </summary>
    /// <param name="overwriting">
    /// True when <paramref name="dst"/> already holds a file that is about to be replaced. On the
    /// local disk the new content is then written beside it and swapped in only once complete, so a
    /// cancelled or failed copy cannot destroy the old file. A brand-new destination is removed
    /// again when the copy does not finish, instead of being left truncated.
    /// </param>
    protected async Task StreamFileAsync(
        IVirtualFileSystem sourceFs, IVirtualFileSystem destFs,
        VfsPath src, VfsPath dst, long totalBytes,
        IProgress<FileProgress> progress, CancellationToken ct,
        bool overwriting = false)
    {
        // On a server too: writing straight over the old file left a truncated copy (and no original)
        // whenever the connection dropped mid-transfer — and a server has no Recycle Bin to fall back on.
        var swap = overwriting && (IsLocalDisk(destFs) || IsRemoteServer(destFs));
        var target = swap ? TempSibling(dst) : dst;
        var finished = false;

        try
        {
            if (PauseHandle is null && FileSystemIdentity.Same(sourceFs, destFs))
            {
                await sourceFs.CopyAsync(src, target, progress, ct);
            }
            else
            {
                await using var input = await sourceFs.OpenReadAsync(src, ct);
                await using var output = await destFs.OpenWriteAsync(target, true, ct);
                await PumpAsync(input, output, src, totalBytes, progress, ct);
            }
            finished = true;
        }
        finally
        {
            // A half-written file is worse than none: it looks like the real thing.
            if (!finished && (swap || !overwriting))
                await TryDeleteAsync(destFs, target);
        }

        if (swap)
        {
            try
            {
                if (IsLocalDisk(destFs)) ClearReadOnly(dst);
                await destFs.MoveAsync(target, dst, CancellationToken.None);
            }
            catch
            {
                // A server's rename-over may delete the old file first (FTP) and then fail: the
                // finished copy is then the only one left, so it is dropped only while the original
                // is still there (or when the disk's atomic replace guarantees that it is).
                if (IsLocalDisk(destFs) || await StillExistsAsync(destFs, dst))
                    await TryDeleteAsync(destFs, target);
                throw;
            }
        }
    }

    private async Task PumpAsync(
        Stream input, Stream output, VfsPath src, long totalBytes,
        IProgress<FileProgress> progress, CancellationToken ct)
    {
        // Declared first so it is disposed first (before the caller's 'output'): a destination whose close
        // publishes the data (a WebDAV PUT) is told to discard it if this copy did not run to the end.
        using var abortUnlessCompleted = new AbortUnlessCompleted(output);

        // A destination that only collects the body and uploads it afterwards (a WebDAV PUT) moves every
        // byte twice, and each pass counts for half. Counting only the writes put the bar at 100 % before
        // the upload, the slow part, had even started.
        var deferred = output as IDeferredUploadStream;

        const int BufferSize = 1_048_576; // 1 MB
        var buffer = new byte[BufferSize];
        long bytesCopied = 0;
        var sw = Stopwatch.StartNew();
        var lastReportTicks = Stopwatch.GetTimestamp();
        var reportIntervalTicks = Stopwatch.Frequency / 10; // 100 ms

        int bytesRead;
        while ((bytesRead = await input.ReadAsync(buffer, ct)) > 0)
        {
            if (PauseHandle is not null)
                await PauseHandle.WaitAsync(ct);

            await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            bytesCopied += bytesRead;

            var now = Stopwatch.GetTimestamp();
            if (bytesCopied == totalBytes || now - lastReportTicks >= reportIntervalTicks)
            {
                lastReportTicks = now;
                var shown = deferred is null ? bytesCopied : bytesCopied / 2;
                progress.Report(new FileProgress(shown, totalBytes, src.FileName, sw.Elapsed));
            }
        }

        if (deferred is not null)
        {
            await deferred.CommitAsync(
                new UploadPassProgress(progress, bytesCopied, totalBytes, src.FileName, sw), ct);
        }

        abortUnlessCompleted.Complete();
    }

    /// <summary>
    /// The upload pass of a deferred upload: the collected half plus half of what was sent. Reports on
    /// the sending thread — <see cref="Progress{T}"/> would post, and its reports could arrive out of order.
    /// </summary>
    private sealed class UploadPassProgress(
        IProgress<FileProgress> progress, long collected, long totalBytes, string fileName, Stopwatch sw)
        : IProgress<long>
    {
        public void Report(long sent) =>
            progress.Report(new FileProgress((collected + sent) / 2, totalBytes, fileName, sw.Elapsed));
    }

    /// <summary>Calls <see cref="IAbortableWriteStream.Abort"/> on dispose unless <see cref="Complete"/> ran.</summary>
    private sealed class AbortUnlessCompleted(System.IO.Stream output) : IDisposable
    {
        private bool _completed;

        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (!_completed)
            {
                (output as IAbortableWriteStream)?.Abort();
            }
        }
    }

    private static VfsPath TempSibling(VfsPath dst) =>
        (dst.Parent ?? VfsPath.Root).Combine($".{dst.FileName}.{Guid.NewGuid().ToString("N")[..8]}.tcvtmp");

    /// <summary>False when the entry is gone or cannot be checked: not knowing is treated as "lost".</summary>
    private static async Task<bool> StillExistsAsync(IVirtualFileSystem fs, VfsPath path)
    {
        try { return await fs.GetEntryAsync(path, CancellationToken.None) is not null; }
        catch { return false; }
    }

    private static async Task TryDeleteAsync(IVirtualFileSystem fs, VfsPath path)
    {
        try { await fs.DeleteAsync(path, false, CancellationToken.None); }
        catch { /* best effort: the original error is the one worth reporting */ }
    }

    /// <summary>Clears the read-only flag of a local file so it can be replaced or deleted.</summary>
    protected static void ClearReadOnly(VfsPath path)
    {
        try
        {
            var win = WindowsFileSystem.ToWindowsPath(path);
            var attrs = File.GetAttributes(win);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(win, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Not there, or not ours to change — the operation that follows reports the real problem.
        }
    }

    // ── Listing and sizing ────────────────────────────────────────────────────

    /// <summary>Total payload of a transfer, walking into directories so progress cannot exceed 100%.</summary>
    protected async Task<long> SumFileBytesAsync(
        IVirtualFileSystem fs, IReadOnlyList<IFileEntry> entries, CancellationToken ct)
    {
        long total = 0;
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (OperationPaths.IsReparseDirectory(e)) continue;   // never followed, so never counted
            total += e.IsDirectory
                ? await SumFileBytesAsync(fs, await ListSourceAsync(fs, e.Path, ct), ct)
                : e.Size;
        }
        return total;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, IReadOnlyList<IFileEntry>>
        _listingCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Lists a source directory, reusing what the sizing pass already fetched. Without this a
    /// remote copy pays two LIST round trips per directory: one to total the bytes up front,
    /// one to walk the same tree again while transferring.
    /// </summary>
    protected async Task<IReadOnlyList<IFileEntry>> ListSourceAsync(
        IVirtualFileSystem fs, VfsPath path, CancellationToken ct)
    {
        if (_listingCache.TryGetValue(path.Value, out var cached)) return cached;

        var entries = await fs.ListAsync(path, ct);
        _listingCache[path.Value] = entries;
        return entries;
    }

    /// <summary>Records a junction that was deliberately not followed.</summary>
    protected void SkipReparse(IFileEntry entry)
    {
        CountSkipped();
        AddWarning($"{entry.Name}: junction or symbolic link not followed");
    }

    /// <summary>
    /// True when the file system is the real Windows disk, so entry paths survive
    /// <c>ToWindowsPath</c> and BCL calls (timestamps, shell verbs) mean something.
    /// </summary>
    protected static bool IsLocalDisk(IVirtualFileSystem fs) => FileSystemIdentity.IsLocalDisk(fs);

    /// <summary>
    /// FTP, SFTP or WebDAV: a move there renames over an existing file. Asked of the file system under
    /// a search-results list too (ADR-0028): its own scheme is "search".
    /// </summary>
    protected static bool IsRemoteServer(IVirtualFileSystem fs) =>
        FileSystemIdentity.Underlying(fs).Scheme.ToLowerInvariant() is "ftp" or "ftps" or "sftp" or "dav" or "davs";

    /// <summary>
    /// Object storage or a cloud drive. Its own recursive delete takes a whole folder (walking it here
    /// would double the requests), but a write replaces the file in one step at commit, so it needs no
    /// temporary-name swap — which there would cost a server-side copy and a delete.
    /// </summary>
    protected static bool IsCloudStorage(IVirtualFileSystem fs) =>
        FileSystemIdentity.Underlying(fs).Scheme.ToLowerInvariant() is "s3" or "azblob" or "gdrive" or "dropbox" or "onedrive";
}
