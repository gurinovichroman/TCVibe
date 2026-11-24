using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.Sync;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

/// <summary>
/// The dev-remote half of <see cref="MainViewModel"/> (6.23): comparing and synchronising two
/// directories, and editing a file that does not live on a disk the editor can see.
/// </summary>
public partial class MainViewModel
{
    // ── Compare and synchronise (6.23) ────────────────────────────────────────

    /// <summary>
    /// Ctrl+K Ctrl+S — compare the two panels and offer to copy the differences either way. Works
    /// between any two file systems, which is what makes it useful for a local checkout against a
    /// server, or for two worktrees of the same repository.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task CompareDirectories()
    {
        // One comparison at a time: the second press finds the first, which may be behind other windows.
        if (_dialogs.ActivateCompareDirectories())
        {
            return;
        }

        // The panels as they sit on screen, not active/opposite: the dialog's "Left", "Right" and
        // "All to the right →" must mean what the user sees, whichever panel had the focus.
        var left = LeftPanel;
        var right = RightPanel;

        // Fixed now, for as long as the window lives: it is not modal, and a panel may leave this folder,
        // close the tab or reconnect meanwhile (6.96). The rows and the plan are about these two places,
        // never about wherever the panels are by then, and the leases keep them open: a closed tab or a
        // replaced connection leaves its file system to this window (INV-09, TRAP-37, TRAP-81).
        var leftSide = new SyncEndpoint(left.FileList.ActiveVfs, left.CurrentPath, left.FileList.IsMounted);
        var rightSide = new SyncEndpoint(right.FileList.ActiveVfs, right.CurrentPath, right.FileList.IsMounted);
        using var leases = new CompositeDisposable(left.FileList.LeaseActiveVfs(), right.FileList.LeaseActiveVfs());

        var vm = new CompareDirectoriesDialogViewModel(leftSide, rightSide);
        await _dialogs.ShowCompareDirectoriesAsync(vm);
        if (!vm.Confirmed)
        {
            return;
        }

        var plan = vm.Plan;
        if (plan.Count == 0) return;

        // INV-11: the confirmation says what will be created and what will be replaced — with the
        // files named, and the ones whose newer copy would be lost called out — not a bare count.
        var summary = SyncPlanSummary.Of(plan, vm.TimeTolerance);
        var confirmed = await _dialogs.ConfirmAsync(
            LocalizationManager.Get("Cmp_Title"),
            SyncConfirmation.Describe(summary, plan, leftSide, rightSide));
        if (!confirmed) return;

        await ExecuteSyncPlanAsync(plan, leftSide, rightSide, left, right);
    }


    /// <summary>
    /// Carries the plan out through the operation queue (INV-10), so every step lands in the log.
    /// Files about to be replaced on the local disk go to the Recycle Bin first, and copies keep
    /// their timestamps (see <see cref="SyncPlanRunner"/>).
    /// </summary>
    private async Task ExecuteSyncPlanAsync(
        IReadOnlyList<SyncPlanItem> plan, SyncEndpoint leftSide, SyncEndpoint rightSide,
        PanelViewModel left, PanelViewModel right)
    {
        var title = LocalizationManager.Get("Cmp_Title");
        var runner = new SyncPlanRunner(_copyLogger, _deleteLogger, _mkdirLogger);
        SyncRunResult result;

        using (LeasePanelFileSystems())
        {
            result = await runner.RunAsync(plan, leftSide, rightSide, op => RunSyncOperationAsync(op, title));
        }

        if (!result.Succeeded)
        {
            var shown = result.Failures.Take(10).ToList();
            var more = result.Failures.Count - shown.Count;
            await _dialogs.ShowErrorAsync(
                title,
                string.Join(Environment.NewLine, shown) +
                (more > 0 ? Environment.NewLine + string.Format(LocalizationManager.Get("Cmp_ConfirmMore"), more) : string.Empty));
        }

        // A target that would not take the source's time (WebDAV, FTP without MFMT) makes every copy
        // look newer next time — and a later local edit look older than it. The user has to know.
        if (result.Unstamped > 0)
        {
            await _dialogs.ShowInfoAsync(title, string.Format(LocalizationManager.Get("Cmp_Unstamped"), result.Unstamped));
        }

        await left.FileList.LoadAsync(left.CurrentPath);
        await right.FileList.LoadAsync(right.CurrentPath);
    }

    /// <summary>
    /// Runs one step of a synchronisation; copies and a rewrite of an archive get a progress window, the
    /// small steps do not.
    /// </summary>
    private async Task<bool> RunSyncOperationAsync(FileOperationBase op, string title)
    {
        if (op is not (CopyOperation or DelegateOperation))
        {
            await _operationQueue.EnqueueAsync(op);
            return op.State == OperationState.Completed;
        }

        var progress = _dialogs.ShowProgressDialog(title);
        switch (op)
        {
            case CopyOperation copy:
                copy.Progress = progress;
                copy.PauseHandle = progress.PauseHandle;
                break;
            case DelegateOperation:
                // An archive rewrite is one call, with nowhere to wait and nothing to count: the bar says
                // it is working, where a bare 0 % looked like a hang.
                progress.CanPause = false;
                progress.IsIndeterminate = true;
                break;
        }

        progress.Attach(op);

        await _operationQueue.EnqueueAsync(op);
        await FinishOperationAsync(op, progress, title);
        return op.State == OperationState.Completed;
    }

    // ── Editing a file that is not on a reachable disk (6.23) ─────────────────

    private bool _remoteEditWired;

    /// <summary>
    /// Edits the selected file through a temporary copy that is uploaded again on every save.
    /// This is what F4 falls back to when the panel is mounted — an editor cannot open
    /// <c>sftp://host/etc/nginx.conf</c>, and copying by hand loses the round trip.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    internal async Task EditRemoteFile()
    {
        var item = ActivePanel.FileList.SelectedItem;
        if (item is null || item.IsParentEntry || item.IsDirectory) return;

        WireRemoteEdit();

        var fileList = ActivePanel.FileList;
        var vfs = fileList.ActiveVfs;
        if (RemoteFetches.TryBegin(vfs, item.Entry.Path, RemoteFetchPurpose.Edit) is not { } ticket)
        {
            // Already on its way: a second download would open a second copy to edit.
            RemoteFetches.Find(vfs, item.Entry.Path, RemoteFetchPurpose.Edit)?.Window?.BringBack();
            return;
        }

        RemoteEditSession? session;
        using (ticket)
        {
            try
            {
                // The session outlives this command and uploads whenever the editor saves. A lease on the
                // mounted file system keeps the connection alive for exactly that long: without it, leaving
                // the mount disposes the connection and every later save fails (TRAP-05, TRAP-29).
                var begin = _remoteEdit.BeginAsync(
                    vfs, item.Entry.Path, item.Name, fileList.LeaseActiveVfs(), ticket.Token, ticket, item.Entry.Size);
                await WatchFetchAsync(ticket, begin);
                session = await begin;
            }
            catch (OperationCanceledException) when (ticket.Token.IsCancellationRequested)
            {
                return;
            }
        }

        if (session is null)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("RemoteEdit_Title"),
                LocalizationManager.Get("RemoteEdit_Failed"));
            return;
        }

        _remoteEditSessions.Add(session);

        await _dialogs.ShowInfoAsync(
            LocalizationManager.Get("RemoteEdit_Title"),
            string.Format(LocalizationManager.Get("RemoteEdit_Started"), item.Name, session.LocalPath));
    }

    // ── Fetching a copy with progress (ADR-0038) ──────────────────────────────

    /// <summary>A copy that takes longer than this gets a progress window; a small file does not flash one.</summary>
    private static readonly TimeSpan FetchWindowAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>The Enter and F4 downloads under way: one per file.</summary>
    internal RemoteFetchTracker RemoteFetches { get; } = new();

    /// <summary>
    /// Waits for a fetch started off the UI thread, with the same window as a background copy once it takes
    /// a while: progress, Cancel, «In background» and the status bar's count (ADR-0038). Returns when the
    /// fetch has ended, however it ended; the caller awaits <paramref name="fetch"/> for its outcome.
    /// </summary>
    internal async Task WatchFetchAsync(RemoteFetchTicket ticket, Task fetch)
    {
        if (await Task.WhenAny(fetch, Task.Delay(FetchWindowAfter)) == fetch)
        {
            return;
        }

        var window = _dialogs.ShowProgressDialog(LocalizationManager.Get("Fetch_Title"));
        ticket.Attach(window);
        await Task.WhenAny(fetch);

        if (fetch.IsCompletedSuccessfully)
        {
            window.Complete();
        }
        else
        {
            window.Dismiss();
        }
    }

    /// <summary>
    /// Connects the service's outcomes to the user, once: success is a notice in the caption, a failed
    /// or declined upload is a dialog (the edits are still in the local copy, and they must know it).
    /// </summary>
    private void WireRemoteEdit()
    {
        if (_remoteEditWired)
        {
            return;
        }
        _remoteEditWired = true;

        _remoteEdit.ConfirmOverwrite = conflict => _dialogs.ConfirmAsync(
            LocalizationManager.Get("RemoteEdit_Title"),
            string.Format(
                LocalizationManager.Get("RemoteEdit_Conflict"),
                conflict.FileName, conflict.OpenedSize, conflict.OpenedModified,
                conflict.CurrentSize, conflict.CurrentModified));

        _remoteEdit.Uploaded += (_, e) =>
        {
            // While closing, one summary of the kept copies replaces the per-save dialogs. A stop has its
            // say there or in the refused update's message, and may arrive after either.
            if (e.Outcome == RemoteEditOutcome.Interrupted || (_remoteEdit.IsClosing && e.Outcome != RemoteEditOutcome.Uploaded))
            {
                return;
            }

            var title = LocalizationManager.Get("RemoteEdit_Title");
            switch (e.Outcome)
            {
                case RemoteEditOutcome.Uploaded:
                    ShowNotice(string.Format(LocalizationManager.Get("RemoteEdit_Uploaded"), e.FileName), TimeSpan.FromSeconds(6));
                    break;
                case RemoteEditOutcome.Declined:
                    _ = _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("RemoteEdit_Declined"), e.FileName, e.LocalPath));
                    break;
                default:
                    _ = _dialogs.ShowErrorAsync(title, string.Format(LocalizationManager.Get("RemoteEdit_UploadFailed"), e.FileName, e.Error, e.LocalPath));
                    break;
            }
        };
    }

    /// <summary>
    /// Before an update hands over (TRAP-72): the edits go up now, with the window alive, not with the
    /// updater already waiting. True when nothing stayed behind. False when something did (a stop, a
    /// failed upload, a conflict): the update does not go ahead, the sessions go on as if nothing had been
    /// asked, and <paramref name="copies"/> names the local copies for the refusal.
    /// </summary>
    public async Task<(bool Clean, IReadOnlyList<string> Copies)> FlushRemoteEditsBeforeUpdateAsync()
    {
        if (!_remoteEdit.HasPendingUploads)
        {
            return (true, []);
        }

        await FlushRemoteEditsAsync();
        var copies = _remoteEdit.UnuploadedCopies();
        if (copies.Count == 0)
        {
            return (true, []);
        }

        ResumeRemoteEditsAfterFlush();
        return (false, copies);
    }

    /// <summary>The flush did not end in an exit after all: the sessions go on as if it had not been asked for.</summary>
    public void ResumeRemoteEditsAfterFlush()
    {
        _remoteEditsFlushed = false;
        _remoteEdit.ResumeAfterFlush();
    }

    /// <summary>A window that waits appears only if the uploads take longer than this: most are over in a blink.</summary>
    private static readonly TimeSpan RemoteEditFlushWindowAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>Set once the exit has waited for the uploads: <see cref="CloseRemoteEditSessions"/> does not wait again.</summary>
    private bool _remoteEditsFlushed;

    /// <summary>Some F4 copy holds edits the server does not have: closing has to wait for them first.</summary>
    public bool HasRemoteEditsToFlush => _remoteEdit.HasPendingUploads;

    /// <summary>
    /// Sends what the F4 copies hold to the server while the window stays alive, and returns when that has
    /// ended or the user stopped it (TRAP-105). A window shows what is on its way and offers to stop;
    /// stopping loses nothing, the copies stay and <see cref="CloseRemoteEditSessions"/> names them.
    /// </summary>
    /// <param name="limit">Stops by itself after this long: the updater does not wait for ever (TRAP-72).</param>
    /// <returns>False when it was stopped before every upload had ended.</returns>
    public async Task<bool> FlushRemoteEditsAsync(TimeSpan? limit = null)
    {
        var window = new RemoteEditFlushDialogViewModel();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(window.StopToken);
        if (limit is { } after)
        {
            stop.CancelAfter(after);
        }

        // The Progress is made here, on the UI thread: reports come back to it, also under the modal window's loop.
        var flush = _remoteEdit.FlushAllAsync(new Progress<RemoteEditFlushProgress>(window.Report), stop.Token);
        if (await Task.WhenAny(flush, Task.Delay(RemoteEditFlushWindowAfter)) != flush)
        {
            _ = flush.ContinueWith(
                _ => window.Finish(), CancellationToken.None, TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());
            _dialogs.ShowRemoteEditFlush(window);
        }

        var finished = await flush;
        _remoteEditsFlushed = true;
        return finished;
    }

    /// <summary>
    /// Ends every remote-edit session. A save made a moment before quitting is uploaded first: the window
    /// has waited for that already (<see cref="FlushRemoteEditsAsync"/>), except when Windows is shutting
    /// down and closing cannot be put off, so it is done here, bounded. A copy holding edits that never
    /// reached the server is kept instead of deleted.
    /// </summary>
    private void CloseRemoteEditSessions()
    {
        if (_remoteEditSessions.Count == 0)
        {
            return;
        }

        if (!_remoteEditsFlushed)
        {
            _remoteEdit.FlushAll(TimeSpan.FromSeconds(15));
        }

        var kept = new List<string>();
        var inDoubt = false;
        foreach (var session in _remoteEditSessions)
        {
            session.Dispose();
            inDoubt |= session.KeptLocalCopy && session.ServerCopyInDoubt;
            if (session.KeptLocalCopy)
            {
                kept.Add(session.LocalPath);
            }
        }

        _remoteEditSessions.Clear();

        // The copies sit in a temporary folder nobody looks in: name them, or the edits are as good as lost.
        if (kept.Count > 0)
        {
            // An upload that did not finish may have left part of the file on the server: theirs to know too.
            var message = string.Format(LocalizationManager.Get("RemoteEdit_KeptOnExit"), string.Join(Environment.NewLine, kept));
            if (inDoubt)
            {
                message += Environment.NewLine + Environment.NewLine + LocalizationManager.Get("RemoteEdit_ServerMayBePartial");
            }

            _dialogs.ShowMessage(LocalizationManager.Get("RemoteEdit_Title"), message, MessageBoxImage.Warning);
        }
    }
}
