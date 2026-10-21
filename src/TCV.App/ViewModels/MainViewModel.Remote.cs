using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.Sync;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;
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
    [RelayCommand]
    private async Task CompareDirectories()
    {
        // The panels as they sit on screen, not active/opposite: the dialog's "Left", "Right" and
        // "All to the right →" must mean what the user sees, whichever panel had the focus.
        var left = LeftPanel;
        var right = RightPanel;

        var vm = new CompareDirectoriesDialogViewModel(
            left.FileList.ActiveVfs, left.CurrentPath,
            right.FileList.ActiveVfs, right.CurrentPath);
        var dlg = new CompareDirectoriesDialog(vm) { Owner = Application.Current.MainWindow };

        if (dlg.ShowDialog() != true) return;

        var plan = vm.Plan;
        if (plan.Count == 0) return;

        var leftSide = new SyncEndpoint(left.FileList.ActiveVfs, left.CurrentPath, left.FileList.IsMounted);
        var rightSide = new SyncEndpoint(right.FileList.ActiveVfs, right.CurrentPath, right.FileList.IsMounted);

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

    /// <summary>Runs one step of a synchronisation; copies get a progress window, the small steps do not.</summary>
    private async Task<bool> RunSyncOperationAsync(FileOperationBase op, string title)
    {
        if (op is not CopyOperation copy)
        {
            await _operationQueue.EnqueueAsync(op);
            return op.State == OperationState.Completed;
        }

        var progress = _dialogs.ShowProgressDialog(title);
        copy.Progress = progress;
        copy.PauseHandle = progress.PauseHandle;
        progress.Attach(copy);

        await _operationQueue.EnqueueAsync(copy);
        await FinishOperationAsync(copy, progress, title);
        return copy.State == OperationState.Completed;
    }

    // ── Editing a file that is not on a reachable disk (6.23) ─────────────────

    private bool _remoteEditWired;

    /// <summary>
    /// Edits the selected file through a temporary copy that is uploaded again on every save.
    /// This is what F4 falls back to when the panel is mounted — an editor cannot open
    /// <c>sftp://host/etc/nginx.conf</c>, and copying by hand loses the round trip.
    /// </summary>
    [RelayCommand]
    internal async Task EditRemoteFile()
    {
        var item = ActivePanel.FileList.SelectedItem;
        if (item is null || item.IsParentEntry || item.IsDirectory) return;

        WireRemoteEdit();

        // The session outlives this command and uploads whenever the editor saves. A lease on the
        // mounted file system keeps the connection alive for exactly that long: without it, leaving
        // the mount disposes the connection and every later save fails (TRAP-05, TRAP-29).
        var fileList = ActivePanel.FileList;
        var session = await _remoteEdit.BeginAsync(
            fileList.ActiveVfs, item.Entry.Path, item.Name, lease: fileList.LeaseActiveVfs());
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
            // While closing, one summary of the kept copies replaces the per-save dialogs.
            if (_remoteEdit.IsClosing && e.Outcome != RemoteEditOutcome.Uploaded)
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
    /// Ends every remote-edit session. A save made a moment before quitting is uploaded first (waiting
    /// out its settle delay would lose it), and a copy holding edits that never reached the server is
    /// kept instead of deleted.
    /// </summary>
    private void CloseRemoteEditSessions()
    {
        if (_remoteEditSessions.Count == 0)
        {
            return;
        }

        _remoteEdit.FlushAll(TimeSpan.FromSeconds(15));

        var kept = new List<string>();
        foreach (var session in _remoteEditSessions)
        {
            session.Dispose();
            if (session.KeptLocalCopy)
            {
                kept.Add(session.LocalPath);
            }
        }

        _remoteEditSessions.Clear();

        // The copies sit in a temporary folder nobody looks in: name them, or the edits are as good as lost.
        if (kept.Count > 0)
        {
            MessageBox.Show(
                string.Format(LocalizationManager.Get("RemoteEdit_KeptOnExit"), string.Join(Environment.NewLine, kept)),
                LocalizationManager.Get("RemoteEdit_Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
