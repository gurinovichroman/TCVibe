using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// Deletes entries: to the Recycle Bin where the drive has one, for good elsewhere. A folder deleted
/// for good is emptied item by item, so the progress window counts what is gone and Cancel or Pause
/// lands between two files instead of after the whole tree (6.36). What went before a Cancel stays
/// gone, or in the bin.
/// </summary>
public sealed class DeleteOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _fs;
    private readonly IReadOnlyList<IFileEntry> _entries;
    private readonly bool _moveToRecycleBin;
    private readonly ILogger<DeleteOperation> _logger;

    public DeleteOperation(
        IVirtualFileSystem fs,
        IReadOnlyList<IFileEntry> entries,
        bool moveToRecycleBin,
        ILogger<DeleteOperation> logger)
    {
        _fs = fs;
        _entries = entries;
        _moveToRecycleBin = moveToRecycleBin;
        _logger = logger;
    }

    /// <summary>
    /// Where the count of removed items goes (the progress window). Items, not bytes: every file and
    /// folder removed on its own counts one, and so does an entry the shell takes to the bin in one call.
    /// </summary>
    public IProgress<ItemProgress>? Progress { get; set; }

    public override OperationLane Lane => LaneOf(_fs);

    public override OperationLogDescriptor LogDescriptor => new(
        OperationKind.Delete,
        OperationLogDescriptor.DescribeEntries(_entries),
        _moveToRecycleBin && _entries.Any(e => RecycleBin.IsAvailableFor(_fs, e.Path))
            ? OperationLogDescriptor.RecycleBinToken
            : null,
        _entries.Count);

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            var counter = new ItemCounter(Progress, Stopwatch.StartNew());

            // The whole tree is listed before the first item goes, so the bar has a total to fill.
            var plan = new List<Step>(_entries.Count);
            foreach (var entry in _entries)
            {
                plan.Add(await PlanAsync(entry, counter, ct));
            }
            counter.Total = plan.Sum(s => s.Count);

            foreach (var step in plan)
            {
                if (await RemoveAsync(step, counter, ct))
                {
                    _logger.LogDebug("Deleted {Path}", step.Entry.Path);
                }
            }

            State = OperationState.Completed;
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            State = OperationState.Failed;
            FailureException = ex;
            throw;
        }
    }

    /// <summary>
    /// One entry to remove. <see cref="Children"/> is null when it goes in a single call: a file, a
    /// link, anything bound for the bin, a folder on a server or one that could not be listed.
    /// Otherwise the children go first and then the emptied folder.
    /// </summary>
    private sealed record Step(IFileEntry Entry, bool ToRecycleBin, IReadOnlyList<Step>? Children)
    {
        /// <summary>Items this step counts in the progress window: itself and everything under it.</summary>
        public int Count { get; } = 1 + (Children?.Sum(c => c.Count) ?? 0);
    }

    private async Task<Step> PlanAsync(IFileEntry entry, ItemCounter counter, CancellationToken ct)
    {
        // Only what really has a bin goes there; the confirmation dialog was built with the same rule,
        // so what the user was told is what happens. The shell takes a whole folder in one call.
        if (_moveToRecycleBin && RecycleBin.IsAvailableFor(_fs, entry.Path))
        {
            return new Step(entry, ToRecycleBin: true, Children: null);
        }

        return await PlanPermanentAsync(entry, counter, ct);
    }

    private async Task<Step> PlanPermanentAsync(IFileEntry entry, ItemCounter counter, CancellationToken ct)
    {
        // On a server every call is a round trip, and its file system asks what a path is before
        // removing it: walking the tree would double the requests of its own recursive delete.
        if (!entry.IsDirectory || OperationPaths.IsReparseDirectory(entry) || IsRemoteServer(_fs) || IsCloudStorage(_fs))
        {
            return new Step(entry, ToRecycleBin: false, Children: null);
        }

        if (PauseHandle is not null)
        {
            await PauseHandle.WaitAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        counter.Starting(entry.Name, mayTakeLong: false);

        IReadOnlyList<IFileEntry> listing;
        try
        {
            listing = await _fs.ListAsync(entry.Path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Deleted in one call, as it always was; if that fails too, the error prompt says why.
            _logger.LogDebug(ex, "Could not list {Path}; deleting it in one call", entry.Path);
            return new Step(entry, ToRecycleBin: false, Children: null);
        }

        var children = new List<Step>(listing.Count);
        foreach (var child in listing)
        {
            children.Add(await PlanPermanentAsync(child, counter, ct));
        }

        return new Step(entry, ToRecycleBin: false, children);
    }

    /// <summary>Removes one planned entry. False when it stays: skipped, or still holding something skipped.</summary>
    private async Task<bool> RemoveAsync(Step step, ItemCounter counter, CancellationToken ct)
    {
        var emptied = true;
        if (step.Children is not null)
        {
            foreach (var child in step.Children)
            {
                emptied &= await RemoveAsync(child, counter, ct);
            }
        }

        if (PauseHandle is not null)
        {
            await PauseHandle.WaitAsync(ct);
        }
        ct.ThrowIfCancellationRequested();

        // A folder that still holds a skipped file cannot go. Asking about it would only put the
        // question the user has just answered once more for every folder above it.
        var removed = false;
        if (emptied)
        {
            var wholeFolder = step.Entry.IsDirectory && step.Children is null
                              && !OperationPaths.IsReparseDirectory(step.Entry);
            counter.Starting(step.Entry.Name, mayTakeLong: wholeFolder);
            removed = await RunGuardedAsync(step.Entry.Path, () => DeleteStepAsync(step, ct), ct);
        }

        counter.Finished(step.Entry.Name);
        return removed;
    }

    private async Task DeleteStepAsync(Step step, CancellationToken ct)
    {
        if (step.ToRecycleBin)
        {
            var path = FileSystem.WindowsFileSystem.ToWindowsPath(step.Entry.Path);
            await RunOnStaThreadAsync(() => DeleteToRecycleBin(path));
        }
        else if (OperationPaths.IsReparseDirectory(step.Entry))
        {
            // A junction or symbolic link goes as a link, never entered: what it points to is not part
            // of the folder. It is removed here rather than left to the folder's recursive delete,
            // which fails with "access denied" on a junction when the app is not elevated — and pnpm
            // fills node_modules with them.
            await _fs.DeleteAsync(step.Entry.Path, recursive: false, ct);
        }
        else
        {
            // Recursive for an emptied folder too: whatever appeared in it since it was listed goes
            // the way a whole folder always went.
            await _fs.DeleteAsync(step.Entry.Path, recursive: true, ct);
        }
    }

    /// <summary>
    /// Counts removed items into <see cref="Progress"/>, at most every 100 ms: a tree of small files
    /// must not flood the dispatcher with one update per file.
    /// </summary>
    private sealed class ItemCounter(IProgress<ItemProgress>? sink, Stopwatch clock)
    {
        private static readonly long ReportIntervalTicks = Stopwatch.Frequency / 10;
        private long _lastReportTicks;
        private bool _reported;
        private int _done;

        /// <summary>0 while the tree is still being listed.</summary>
        public int Total { get; set; }

        /// <summary>
        /// About to work on <paramref name="name"/>. One call that may run for minutes (a folder going
        /// to the bin) is named at once, or the window would show the previous file all that time.
        /// </summary>
        public void Starting(string name, bool mayTakeLong) => Report(name, force: mayTakeLong);

        public void Finished(string name)
        {
            _done++;
            Report(name, force: _done >= Total);
        }

        private void Report(string name, bool force)
        {
            if (sink is null)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (!force && _reported && now - _lastReportTicks < ReportIntervalTicks)
            {
                return;
            }

            _reported = true;
            _lastReportTicks = now;
            sink.Report(new ItemProgress(_done, Total, name, clock.Elapsed));
        }
    }

    /// <summary>
    /// Runs a shell call on a thread of its own. The shell wants a single-threaded apartment, the
    /// queue's worker is a pool thread (MTA), and a big folder can take the shell minutes — which
    /// must not happen on the UI thread either (INV-06).
    /// </summary>
    private static Task RunOnStaThreadAsync(Action action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "TCV recycle bin",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static void DeleteToRecycleBin(string path)
    {
        var op = new FileSystem.Native.Shell32.SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FileSystem.Native.Shell32.FO_DELETE,
            pFrom = path + '\0',
            pTo = null,
            fFlags = (ushort)(FileSystem.Native.Shell32.FOF_ALLOWUNDO |
                              FileSystem.Native.Shell32.FOF_NOCONFIRMATION |
                              FileSystem.Native.Shell32.FOF_WANTNUKEWARNING |
                              FileSystem.Native.Shell32.FOF_SILENT),
        };
        int result = FileSystem.Native.Shell32.SHFileOperation(ref op);
        if (result != 0)
        {
            throw new IOException(
                new System.ComponentModel.Win32Exception(result).Message,
                unchecked((int)(0x80070000u | (uint)result)));
        }
        if (op.fAnyOperationsAborted)
        {
            throw new OperationCanceledException();
        }
    }
}
