using System.Windows;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;
using TCV.Core.Interfaces;
using TCV.Git;
using TCV.Network;

namespace TCV.App.Services;

public sealed class DialogService : IDialogService
{
    private readonly Settings.AppSettings _settings;

    public DialogService(Settings.AppSettings settings) => _settings = settings;

    private Window? OwnerWindow => Application.Current.MainWindow;
    private Window? _activeProgressDialog;

    public Task<CopyDialogResult?> ShowCopyDialogAsync(
        IReadOnlyList<IFileEntry> files, string defaultTarget, bool isMove = false,
        CopyOptionSet offered = CopyOptionSet.All)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new CopyDialogViewModel(files, defaultTarget, isMove, offered);

            // "Ask what to do about existing files" off: the dialog starts on Overwrite. The choice
            // is still visible in the dialog and can be changed per copy.
            if (!_settings.Operations.ConfirmOverwrite)
            {
                vm.ConflictMode = Operations.ConflictMode.Overwrite;
            }

            var dlg = new CopyDialog(vm) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true)
                return null;
            return (CopyDialogResult?)new CopyDialogResult(vm.TargetPath, vm.BuildOptions());
        }).Task;
    }

    public Task<(ConflictResolution Resolution, bool ApplyToAll)> ShowConflictDialogAsync(
        IFileEntry source, IFileEntry existing)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new ConflictDialogViewModel(source, existing);
            var dlg = new ConflictDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return (vm.Resolution, vm.ApplyToAll);
        }).Task;
    }

    public Task<bool> ShowDeleteConfirmAsync(IReadOnlyList<IFileEntry> files, int recycleCount)
    {
        // Sending to the Recycle Bin can be undone, so the user may switch its prompt off (INV-11
        // asks for confirmation *or* a way back). A permanent delete has no way back and always asks.
        if (files.Count > 0 && recycleCount == files.Count && !_settings.Operations.ConfirmDelete)
        {
            return Task.FromResult(true);
        }

        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new DeleteDialogViewModel(files, recycleCount);
            var dlg = new DeleteConfirmDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true && vm.DialogResult;
        }).Task;
    }

    public Task<OperationErrorResolution> ShowOperationErrorAsync(OperationErrorInfo error)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new OperationErrorDialogViewModel(error);
            var dlg = new OperationErrorDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return vm.Resolution;
        }).Task;
    }

    public Task<string?> ShowNewFolderDialogAsync()
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new NewFolderDialogViewModel();
            var dlg = new NewFolderDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.FolderName : null;
        }).Task;
    }

    public Task<string?> ShowRenameDialogAsync(string currentName, bool isDirectory = false)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new RenameDialogViewModel(currentName, isDirectory);
            var dlg = new RenameDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.NewName : null;
        }).Task;
    }

    public Task ShowErrorAsync(string title, string message)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error)
        ).Task.ContinueWith(_ => { });
    }

    public ProgressDialogViewModel ShowProgressDialog(string title)
    {
        var vm = new ProgressDialogViewModel(title);
        Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new ProgressDialog(vm) { Owner = OwnerWindow };
            _activeProgressDialog = dlg;
            vm.CloseRequested += (_, _) => _activeProgressDialog = null;
            dlg.Show();
        });
        return vm;
    }

    public void ShowConnectionManager(ConnectionManagerDialogViewModel viewModel)
    {
        var dlg = new ConnectionManagerDialog(viewModel) { Owner = OwnerWindow };
        dlg.ShowDialog();
    }

    public bool ShowSshConfigImport(SshConfigImportDialogViewModel viewModel)
    {
        // Owned by the connection manager it was opened from, which is the active window.
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? OwnerWindow;
        var dlg = new SshConfigImportDialog(viewModel) { Owner = owner };
        return dlg.ShowDialog() == true;
    }

    public Task<PackDialogResult?> ShowPackDialogAsync(
        IReadOnlyList<IFileEntry> selectedFiles, string defaultDestDir, bool sevenZipAvailable)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new PackDialogViewModel(selectedFiles, defaultDestDir, sevenZipAvailable);
            var dlg = new PackDialog(vm) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true) return null;
            return (PackDialogResult?)vm.BuildResult();
        }).Task;
    }

    public Task<UnpackDialogResult?> ShowUnpackDialogAsync(
        IReadOnlyList<string> archiveNames, string defaultDestDir, bool isZip)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new UnpackDialogViewModel(archiveNames, defaultDestDir, canUseWindowsShell: isZip);
            var dlg = new UnpackDialog(vm) { Owner = OwnerWindow };
            if (dlg.ShowDialog() != true) return null;
            return (UnpackDialogResult?)vm.BuildResult();
        }).Task;
    }

    public Task<string?> ShowPasswordDialogAsync(string archiveName)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new PasswordDialogViewModel(archiveName);
            var dlg = new PasswordDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.Password : null;
        }).Task;
    }

    public void ShowFileViewer(
        string fileName, byte[] data, bool fetchTruncated, long fullSize,
        TCV.Ai.Llm.LlmClient? llm = null, TCV.App.Viewer.IViewerSource? source = null,
        TCV.App.Viewer.ViewerSiblings? siblings = null)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = new FileViewerDialogViewModel(fileName, data, fetchTruncated, fullSize, llm, source, siblings);
            // Non-modal, like the progress dialog — several files can be viewed side by side and
            // the viewer never blocks the main window.
            var dlg = new FileViewerDialog(vm) { Owner = OwnerWindow };
            dlg.Show();
        });
    }

    public void ShowOperationLog(OperationLog log, Func<RenameBatchRecord, Task>? undoRename = null)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var vm = new OperationLogDialogViewModel(log, new SavedOperationStore(), undoRename);
            var dlg = new OperationLogDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
        });
    }

    public Task<string?> ShowInputAsync(string title, string prompt, string initial = "", bool allowEmpty = false)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new InputDialogViewModel(title, prompt, initial, allowEmpty);
            var dlg = new InputDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.Value : null;
        }).Task;
    }

    public string? PickOpenFile(string title, string filter, string? initialDirectory = null)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                InitialDirectory = initialDirectory,
            };
            return dlg.ShowDialog(OwnerWindow) == true ? dlg.FileName : null;
        });
    }

    public string? PickSaveFile(string title, string filter, string fileName, string? initialDirectory = null)
    {
        return Application.Current.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = fileName,
                InitialDirectory = initialDirectory,
                AddExtension = true,
            };
            return dlg.ShowDialog(OwnerWindow) == true ? dlg.FileName : null;
        });
    }

    public Task ShowBookmarksManagerAsync(BookmarksManagerDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new BookmarksManagerDialog(viewModel) { Owner = OwnerWindow };
            dlg.ShowDialog();
        }).Task;
    }

    public Task<bool> ShowSavedOperationsManagerAsync(SavedOperationsDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new SavedOperationsDialog(viewModel) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return viewModel.Saved;
        }).Task;
    }

    public Task<Commands.CommandPaletteItem?> PickFromPaletteAsync(CommandPaletteViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new CommandPaletteDialog(viewModel) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? viewModel.SelectedItem : null;
        }).Task;
    }

    public Task ShowSettingsAsync(SettingsDialogViewModel viewModel) =>
        ShowModalAsync(() => new SettingsDialog(viewModel));

    public Task ShowChecksumAsync(ChecksumDialogViewModel viewModel) =>
        ShowModalAsync(() => new ChecksumDialog(viewModel));

    public Task ShowUserToolsAsync(UserToolsDialogViewModel viewModel) =>
        ShowModalAsync(() => new UserToolsDialog(viewModel));

    public Task ShowPluginManagerAsync(PluginManagerDialogViewModel viewModel) =>
        ShowModalAsync(() => new PluginManagerDialog(viewModel));

    public Task ShowHotkeySettingsAsync(HotkeySettingsDialogViewModel viewModel) =>
        ShowModalAsync(() => new HotkeySettingsDialog(viewModel));

    /// <summary>
    /// Creates a window on the UI thread and shows it modally over the main one; returns when it is
    /// closed. For dialogs whose outcome, if any, lives in their view model.
    /// </summary>
    private Task ShowModalAsync(Func<Window> createDialog)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = createDialog();
            dlg.Owner = OwnerWindow;
            dlg.ShowDialog();
        }).Task;
    }

    public Task<WorkspaceNotesResult?> ShowWorkspaceNotesAsync(
        string workspaceName,
        string notes,
        string terminalCommand,
        string terminalDirectory,
        string notesPath)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new WorkspaceNotesDialogViewModel(
                workspaceName, notes, terminalCommand, terminalDirectory, notesPath);
            var dlg = new WorkspaceNotesDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.Result : null;
        }).Task;
    }

    public Task<WorkspacePropertiesResult?> ShowWorkspacePropertiesAsync(
        TCV.App.Workspaces.Workspace workspace,
        IReadOnlyList<string> allNames,
        IReadOnlyList<string> groups,
        bool isNew = false)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new WorkspacePropertiesDialogViewModel(workspace, allNames, groups, isNew);
            var dlg = new WorkspacePropertiesDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.Result : null;
        }).Task;
    }

    public Task ShowInfoAsync(string title, string message)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information)
        ).Task.ContinueWith(_ => { });
    }

    public Task<bool> ConfirmAsync(string title, string message)
    {
        // Our own dialog rather than MessageBox: its Yes/No follow the UI language, not Windows'.
        return Application.Current.Dispatcher.InvokeAsync(() => ConfirmDialog.Ask(title, message)).Task;
    }

    public Task<ConfirmChoice> ChooseAsync(
        string title, string message, string confirmText, string alternateText, bool isWarning = false)
    {
        return Application.Current.Dispatcher.InvokeAsync(
            () => ConfirmDialog.Choose(title, message, confirmText, alternateText, isWarning)).Task;
    }

    public Task<(GitWorktreeAction Action, string Path, bool Removed)> ShowWorktreeDashboardAsync(
        GitWorktreeService worktrees,
        string anyPathInRepository,
        Func<GitWorktreeInfo, IReadOnlyList<string>>? findUsers = null)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // The removal prompt is built here rather than in the view model: it has to name what is
            // about to be destroyed (INV-11) — the uncommitted count, a lock, and whatever still
            // points into the folder — none of which is obvious from the row.
            var vm = new GitWorktreeDialogViewModel(
                worktrees, anyPathInRepository, w => ConfirmRemoveWorktree(w, findUsers?.Invoke(w) ?? []));
            var dlg = new GitWorktreeDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return (vm.Action, vm.ChosenPath, vm.RemovedAny);
        }).Task;
    }

    private static bool ConfirmRemoveWorktree(GitWorktreeInfo worktree, IReadOnlyList<string> users)
    {
        string L(string key) => Localization.LocalizationManager.Get(key);

        // A record whose folder is already gone is just bookkeeping: say that, and skip the scary text.
        if (worktree.Missing)
            return ConfirmDialog.Ask(
                L("Git_WtRemove"),
                string.Format(L("Git_WtPruneConfirm"), worktree.Name, worktree.Path),
                L("Git_WtRemoveButton"),
                L("Cancel"));

        var text = string.Format(L("Git_WtRemoveConfirm"), worktree.Name, worktree.Path);
        if (worktree.DirtyCount == GitWorktreeInfo.UnknownDirtyCount)
            text += Environment.NewLine + Environment.NewLine + L("Git_WtDirtyUnknownWarning");
        else if (worktree.IsDirty)
            text += Environment.NewLine + Environment.NewLine + Localization.LocalizationManager.Format("Git_WtDirtyWarning", worktree.DirtyCount);
        if (worktree.IsLocked)
            text += Environment.NewLine + Environment.NewLine + L("Git_WtLockedWarning");
        if (users.Count > 0)
            text += Environment.NewLine + Environment.NewLine + L("Git_WtUsedBy") + Environment.NewLine + string.Join(Environment.NewLine, users.Select(u => "  • " + u));

        return ConfirmDialog.Ask(
            L("Git_WtRemove"),
            text,
            L("Git_WtRemoveButton"),
            L("Cancel"),
            isWarning: worktree.IsDirty || worktree.IsLocked || users.Count > 0);
    }

    public Task<NewWorktreeResult?> ShowNewWorktreeAsync(
        string repositoryRoot,
        string worktreeRoot,
        IReadOnlyList<string> existingBranches,
        string setupCommand,
        bool createWorkspaceByDefault,
        IReadOnlyCollection<string>? branchesHeldElsewhere = null)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new NewWorktreeDialogViewModel(
                repositoryRoot, worktreeRoot, existingBranches, setupCommand, createWorkspaceByDefault,
                branchesHeldElsewhere);
            var dlg = new NewWorktreeDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.Result : null;
        }).Task;
    }

    public Task<bool> ShowGitCommitAsync(
        GitWriteService git, string anyPathInRepository, TCV.Ai.Llm.LlmClient? llm = null)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new GitCommitDialogViewModel(
                git, anyPathInRepository, llm, this,
                showDiff: path => ShowGitDiffAsync(
                    new GitStatusProvider(Microsoft.Extensions.Logging.Abstractions.NullLogger<GitStatusProvider>.Instance), path));
            var dlg = new GitCommitDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return vm.Changed;
        }).Task;
    }

    public Task<bool> ShowGitBranchAsync(GitWriteService git, string anyPathInRepository)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new GitBranchDialogViewModel(git, anyPathInRepository, this);
            var dlg = new GitBranchDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return vm.Changed;
        }).Task;
    }

    public Task ShowGitDiffAsync(GitStatusProvider provider, string fullPath)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new GitDiffDialogViewModel(provider, fullPath);
            var dlg = new GitDiffDialog(vm) { Owner = OwnerWindow };
            dlg.ShowDialog();
        }).Task;
    }

    public Task<string?> ShowGitCloneAsync(
        GitCloneService clone, string parentDirectory, string initialUrl)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var vm = new GitCloneDialogViewModel(clone, parentDirectory, initialUrl);
            var dlg = new GitCloneDialog(vm) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? vm.ClonedPath : null;
        }).Task;
    }

    // ── Agent-aware layer (6D) ────────────────────────────────────────────────

    public Task<(AgentChangesAction Action, string Path)> ShowAgentChangesAsync(
        AgentChangesDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new AgentChangesDialog(viewModel) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return (viewModel.Action, viewModel.ChosenPath);
        }).Task;
    }

    public Task<bool> ShowAgentApprovalAsync(AgentApprovalDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            // Withdrawn before it was ever shown — the gate's wait expired while this call was queued
            // on the dispatcher. Both the withdrawal and this run on the UI thread, so the check and
            // the ShowDialog below cannot interleave.
            if (viewModel.Withdrawn) return false;

            // Owned by the main window and modal: an agent's write proposal is not something to
            // discover later behind other windows.
            var dlg = new AgentApprovalDialog(viewModel) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return viewModel.Approved;
        }).Task;
    }

    public Task<AgentLaunchResult?> ShowAgentLaunchAsync(AgentLaunchDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new AgentLaunchDialog(viewModel) { Owner = OwnerWindow };
            return dlg.ShowDialog() == true ? viewModel.Result : null;
        }).Task;
    }

    public Task<int> ShowAiRenameAsync(AiRenameDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new AiRenameDialog(viewModel) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return viewModel.RenamedCount;
        }).Task;
    }

    public Task<SemanticSearchAction> ShowSemanticSearchAsync(SemanticSearchDialogViewModel viewModel)
    {
        return Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var dlg = new SemanticSearchDialog(viewModel) { Owner = OwnerWindow };
            dlg.ShowDialog();
            return viewModel.Action;
        }).Task;
    }
}
