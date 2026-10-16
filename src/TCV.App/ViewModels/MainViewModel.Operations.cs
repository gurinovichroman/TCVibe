using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Commands;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels;

/// <summary>
/// Saved operations and the drop stack (6.22 / 6.24). Both are ways of aiming the ordinary
/// operation queue at a different set of files, so neither performs any IO of its own: everything
/// goes through <c>OperationQueue</c> and therefore through the log and undo (INV-10).
/// </summary>
public partial class MainViewModel
{
    // ── Saved operations (6.22) ───────────────────────────────────────────────

    /// <summary>Ctrl+K Ctrl+O — pick a saved operation and run it against the current panels.</summary>
    [RelayCommand]
    private async Task OpenSavedOperations()
    {
        var lang = LocalizationManager.CurrentLanguage;
        var saved = _savedOperations.LoadAll();

        // Nothing to pick yet: the manager, with a first operation drafted from what is selected.
        if (saved.Count == 0)
        {
            await ShowSavedOperationsManagerAsync(DraftFromPanels());
            return;
        }

        var items = saved
            .Select(o => new CommandPaletteItem(o.Name, o.Name, SavedOperationText.Describe(o), string.Empty))
            .ToList();

        var vm = new CommandPaletteViewModel(items, lang,
            LocalizationManager.Get("SavedOp_Title"),
            LocalizationManager.Get("SavedOp_Placeholder"))
        {
            // The picker only runs; this says where adding, editing and deleting live (6.50).
            Status = LocalizationManager.Get("SavedOp_ManageHint"),
        };
        if (await _dialogs.PickFromPaletteAsync(vm) is not { } chosen)
        {
            return;
        }

        var operation = saved.FirstOrDefault(o =>
            string.Equals(o.Name, chosen.Id, StringComparison.OrdinalIgnoreCase));
        if (operation is not null) await RunSavedOperationAsync(operation);
    }

    /// <summary>The saved-operations manager: add, edit, rename and delete (6.50).</summary>
    [RelayCommand]
    private Task ManageSavedOperations() => ShowSavedOperationsManagerAsync(draft: null);

    private Task<bool> ShowSavedOperationsManagerAsync(SavedOperation? draft) =>
        _dialogs.ShowSavedOperationsManagerAsync(new SavedOperationsDialogViewModel(_savedOperations, draft));

    /// <summary>
    /// A first operation drafted from the current context — copy what is selected into the
    /// opposite panel's directory. The name is left to the manager, which also lets the user change
    /// everything else before anything is saved.
    /// </summary>
    private SavedOperation DraftFromPanels() => new()
    {
        Kind = SavedOperationKind.Copy,
        // Masks, not the paths that happen to be selected now: a saved operation that only
        // ever moves the same five files is a macro, not a reusable one.
        SourceMask = SuggestMask(),
        Target = "%O",
        LastUsedUtc = DateTime.UtcNow,
    };

    /// <summary>A mask covering the selected files: their common extension, or everything.</summary>
    private string SuggestMask()
    {
        var extensions = GetSelectedOrCurrent()
            .Where(e => !e.IsDirectory)
            .Select(e => Path.GetExtension(e.Name))
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return extensions.Count == 1 ? "*" + extensions[0] : string.Empty;
    }

    /// <summary>
    /// Runs a saved operation: expands its target, resolves its source mask against the active
    /// panel, confirms if asked, and hands the result to the queue.
    /// </summary>
    private async Task RunSavedOperationAsync(SavedOperation operation)
    {
        // Every kind here needs real disk paths for its destination (TRAP-02). Search hits are disk
        // files, and the list works in the folder it was opened over (ADR-0026).
        if (await WorkingFolderOrRejectAsync(LocalizationManager.Get("SavedOp_Title")) is not { } workingFolder) return;

        var target = OperationMacros.Expand(operation.Target, BuildMacroContext(workingFolder));
        if (string.IsNullOrWhiteSpace(target)) target = OppositePanel.CurrentPath;

        // The target is a folder on the disk: the folder is created there and the files are sent
        // there. A relative one is taken from the active panel's folder; one that is not a disk path
        // at all (%O while the opposite panel shows a server or an archive) is refused rather than
        // created on the current drive while the files go to the server.
        if (operation.Kind != SavedOperationKind.Delete)
        {
            var (resolved, side) = OperationPaths.ResolveTypedTarget(
                target, workingFolder, activeIsLocal: true, oppositeIsLocal: !OppositePanel.FileList.IsMounted);
            if (side == OperationPaths.TargetSide.Opposite && OppositePanel.FileList.IsMounted)
            {
                await _dialogs.ShowErrorAsync(
                    LocalizationManager.Get("SavedOp_Title"),
                    string.Format(LocalizationManager.Get("SavedOp_TargetNotOnDisk"), target));
                return;
            }
            target = resolved;
        }

        var sources = ResolveSources(operation.SourceMask);
        if (sources.Count == 0 && operation.Kind != SavedOperationKind.CreateDirectory)
        {
            await _dialogs.ShowInfoAsync(
                LocalizationManager.Get("SavedOp_Title"),
                string.Format(LocalizationManager.Get("SavedOp_NoMatch"), operation.SourceMask));
            return;
        }

        // A preview before anything irreversible: the whole point of a saved operation is that it
        // runs without a dialog, which is exactly why the destructive ones must show one (INV-11).
        if (operation.Kind == SavedOperationKind.Delete)
        {
            // The delete dialog, not a generic question: it names what the mask picked and says
            // whether it goes to the Recycle Bin or is gone for good.
            var recycle = DeleteGoesToRecycleBin ? RecycleBin.CountRecyclable(SourceVfs, sources) : 0;
            if (!await _dialogs.ShowDeleteConfirmAsync(sources, recycle)) return;
        }
        else if (operation.Confirm || operation.IsDestructive)
        {
            var confirmed = await _dialogs.ConfirmAsync(
                LocalizationManager.Get("SavedOp_Title"),
                LocalizationManager.Format("SavedOp_Confirm", operation.Name, sources.Count, target));
            if (!confirmed) return;
        }

        await ExecuteSavedOperationAsync(operation, sources, target);
        _savedOperations.Touch(operation.Name, DateTime.UtcNow);
    }

    private async Task ExecuteSavedOperationAsync(
        SavedOperation operation, IReadOnlyList<IFileEntry> sources, string target)
    {
        if ((operation.CreateTarget && operation.Kind != SavedOperationKind.Delete)
            || operation.Kind == SavedOperationKind.CreateDirectory)
        {
            // Through the queue like every other mutation (INV-10): logged, and off the UI thread.
            var mkdir = new CreateDirectoryOperation(_fs, new VfsPath(target), _logger);
            await _operationQueue.EnqueueAsync(mkdir);
            if (mkdir.State == OperationState.Failed && mkdir.FailureException is not null)
            {
                await _dialogs.ShowErrorAsync(
                    LocalizationManager.Get("SavedOp_Title"), GetFileErrorMessage(mkdir.FailureException));
                return;
            }
        }

        switch (operation.Kind)
        {
            case SavedOperationKind.Copy:
            case SavedOperationKind.Move:
                // The disk, where the target was resolved and created — not the opposite panel's
                // file system, which may be a server (INV-08).
                await TransferAsync(sources, target, isMove: operation.Kind == SavedOperationKind.Move, targetVfs: _fs);
                break;

            case SavedOperationKind.Delete:
                await DeleteEntriesAsync(sources);
                break;

            case SavedOperationKind.CreateDirectory:
                // The directory was already created above; nothing else to enqueue.
                break;
        }

        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
        await OppositePanel.FileList.LoadAsync(OppositePanel.CurrentPath);
    }

    /// <summary>Entries the mask selects, or the panel's current selection when it is empty.</summary>
    private IReadOnlyList<IFileEntry> ResolveSources(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask)) return GetSelectedOrCurrent();

        var patterns = mask
            .Split([';', ',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new Regex(
                "^" + Regex.Escape(p == "*.*" ? "*" : p).Replace("\\*", ".*").Replace("\\?", ".") + "$",
                RegexOptions.IgnoreCase))
            .ToList();

        return ActivePanel.FileList.Files
            .Where(f => !f.IsParentEntry && f.Entry is not null)
            .Where(f => patterns.Any(p => p.IsMatch(f.Name)))
            .Select(f => f.Entry)
            .ToList();
    }

    private OperationMacros.Context BuildMacroContext(string activeFolder)
    {
        var focused = ActivePanel.FileList.SelectedItem;
        return new OperationMacros.Context(
            activeFolder,
            OppositePanel.CurrentPath,
            focused is { IsParentEntry: false } ? focused.NameWithoutExtension : string.Empty,
            focused is { IsParentEntry: false } ? focused.Extension : string.Empty,
            DateTime.Now);
    }

    // ── Drop stack (6.24) ─────────────────────────────────────────────────────

    /// <summary>Ctrl+K Ctrl+A — add the panel's selection to the drop stack.</summary>
    [RelayCommand]
    private async Task DropStackAdd()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0) return;

        // The stack keeps bare entries with no record of the file system they came from, and later
        // resolves them against the local disk. An archive or FTP path collected here would then
        // name a different file on the current drive (INV-07), so only disk entries go in — search
        // hits among them (TRAP-28).
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get("Drop_Title"))) return;

        // A notice, not a dialog: collecting is done a few files at a time from several folders,
        // and an OK to press after every add made that a chore.
        var added = _dropStack.Add(selected);
        ShowNotice(
            added == 0
                ? LocalizationManager.Get("Drop_AlreadyThere")
                : string.Format(LocalizationManager.Get("Drop_Added"), added, _dropStack.Count),
            TimeSpan.FromSeconds(5));
    }

    /// <summary>Ctrl+K Ctrl+D — show the stack and decide what to do with it.</summary>
    [RelayCommand]
    private async Task DropStackShow()
    {
        if (_dropStack.IsEmpty)
        {
            await _dialogs.ShowInfoAsync(
                LocalizationManager.Get("Drop_Title"),
                LocalizationManager.Get("Drop_Empty"));
            return;
        }

        var vm = new DropStackDialogViewModel(_dropStack);
        var dlg = new DropStackDialog(vm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;

        // Clearing touches no file, so it does not need anything ticked.
        if (vm.Action == DropStackAction.Clear)
        {
            _dropStack.Clear();
            return;
        }

        var entries = vm.Chosen;
        if (entries.Count == 0) return;

        // "Here" is the active panel, and the stack collects entries across directories with no
        // record of which file system each came from — so both ends have to be the plain disk
        // (TRAP-02). Refusing is honest; guessing would write to the wrong place. A hit's folder
        // opened from the search results is a disk folder; the list itself is none.
        if (await DiskFolderOrRejectAsync(LocalizationManager.Get("Drop_Title")) is not { } folder) return;

        // The destination is the disk, not the opposite panel's default: with an archive or an FTP
        // host open opposite, TargetVfs would resolve this local path on the remote side (INV-08).
        var op = await TransferAsync(
            entries, folder,
            isMove: vm.Action == DropStackAction.MoveHere,
            targetVfs: _fs);

        // Files that were moved are gone from where they came from; keeping them on the stack
        // would leave it full of dead paths. A move that was cancelled or failed, or left files
        // behind, keeps the stack: the user has just been told it did not finish, and the list
        // is what they would need to try again.
        if (vm.Action == DropStackAction.MoveHere && op is { State: OperationState.Completed, SkippedCount: 0 })
            _dropStack.Clear();
        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
    }

    // ── Shared execution ──────────────────────────────────────────────────────

    /// <summary>
    /// Copies or moves <paramref name="entries"/> into <paramref name="target"/> through the
    /// queue, so the transfer is logged, undoable and cancellable like any other (INV-10).
    /// The source file system is the active panel's; a lease keeps it alive (INV-09).
    /// </summary>
    /// <param name="targetVfs">
    /// File system <paramref name="target"/> is a path on. Defaults to the opposite panel's,
    /// which is right for an F5-shaped transfer and wrong for anything whose destination is the
    /// active panel — those must pass their own.
    /// </param>
    private async Task<FileOperationBase?> TransferAsync(
        IReadOnlyList<IFileEntry> entries, string target, bool isMove, IVirtualFileSystem? targetVfs = null)
    {
        if (entries.Count == 0) return null;

        var destinationVfs = targetVfs ?? TargetVfs;
        var progress = _dialogs.ShowProgressDialog(isMove ? LocalizationManager.Get("Op_Moving") : LocalizationManager.Get("Op_Copying"));
        var strategy = BuildConflictStrategy(ConflictMode.Ask);
        var destination = new VfsPath(target);

        // Progress is declared on the concrete operations rather than on the base, so the two
        // branches configure their own instance before being widened for the queue.
        FileOperationBase op;
        if (isMove)
        {
            op = new MoveOperation(SourceVfs, destinationVfs, entries, destination, strategy, _moveLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
            };
        }
        else
        {
            op = new CopyOperation(SourceVfs, destinationVfs, entries, destination, strategy, new CopyOptions(), _copyLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
            };
        }
        op.LinkCancellation(progress.Cts.Token);
        op.ErrorHandler = BuildErrorHandler();

        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress,
                isMove ? LocalizationManager.Get("Fc_MoveFailed") : LocalizationManager.Get("Fc_CopyFailed"));
        }

        return op;
    }

    private async Task DeleteEntriesAsync(IReadOnlyList<IFileEntry> entries)
    {
        if (entries.Count == 0) return;

        // The same window as F8's: a saved delete can pick a big tree, and must be stoppable (6.36).
        var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Op_Deleting"));
        var op = new DeleteOperation(SourceVfs, entries, DeleteGoesToRecycleBin, _deleteLogger)
        {
            Progress = progress,
            PauseHandle = progress.PauseHandle,
        };
        op.LinkCancellation(progress.Cts.Token);
        op.ErrorHandler = BuildErrorHandler();

        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, LocalizationManager.Get("Fc_DeleteFailed"));
        }
    }

    /// <summary>The same rule F8 applies: the setting, and never for a server.</summary>
    private bool DeleteGoesToRecycleBin =>
        _settings.Operations.MoveToRecycleBin && !ActivePanel.FileList.IsRemote;
}
