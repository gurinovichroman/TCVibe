using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.App.FileSystem.Native;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.PluginHost;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Views.Dialogs;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.ViewModels;

/// <summary>
/// Everything that acts on the files themselves: copy, move, delete, rename, create, open, view,
/// edit, the clipboard, drag &amp; drop, the shell context menu, and packing / unpacking. The
/// largest of the four command facades <see cref="MainViewModel"/> was split into by task 6.26.
/// </summary>
/// <remarks>
/// The plumbing every file operation shares — the mount guard, the file-system lease, the conflict
/// strategy and the progress teardown — deliberately stayed on the shell: the git, agent, saved-
/// operation and remote halves run operations too, and duplicating that here would give the app two
/// answers to «how is a file operation run» (INV-10).
/// </remarks>
public sealed partial class FileCommands : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly AppSettings _settings;
    private readonly IDialogService _dialogs;
    private readonly WindowsFileSystem _fs;
    private readonly OperationQueue _operationQueue;
    private readonly IClipboardBuffer _clipboard;
    private readonly IArchivePlugin _archivePlugin;
    private readonly AgentServices _agents;
    private readonly ILogger _logger;
    private readonly ILogger<CopyOperation> _copyLogger;
    private readonly ILogger<MoveOperation> _moveLogger;
    private readonly ILogger<DeleteOperation> _deleteLogger;
    private readonly ILogger<CreateDirectoryOperation> _mkdirLogger;
    private readonly FileEditorLauncher _fileEditor;

    public FileCommands(
        MainViewModel shell,
        AppSettings settings,
        IDialogService dialogs,
        WindowsFileSystem fs,
        OperationQueue operationQueue,
        IClipboardBuffer clipboard,
        IArchivePlugin archivePlugin,
        AgentServices agents,
        ILogger logger,
        ILogger<CopyOperation> copyLogger,
        ILogger<MoveOperation> moveLogger,
        ILogger<DeleteOperation> deleteLogger,
        ILogger<CreateDirectoryOperation> mkdirLogger,
        FileEditorLauncher fileEditor)
    {
        _shell = shell;
        _settings = settings;
        _dialogs = dialogs;
        _fs = fs;
        _operationQueue = operationQueue;
        _clipboard = clipboard;
        _archivePlugin = archivePlugin;
        _agents = agents;
        _logger = logger;
        _copyLogger = copyLogger;
        _moveLogger = moveLogger;
        _deleteLogger = deleteLogger;
        _mkdirLogger = mkdirLogger;
        _fileEditor = fileEditor;
    }

    // ── What this facade borrows from the shell ──────────────────────────────

    private PanelViewModel ActivePanel => _shell.ActivePanel;
    private PanelViewModel OppositePanel => _shell.OppositePanel;

    /// <summary>
    /// A connection opened read-only refuses a write up front, once, rather than file by file as each
    /// write meets the file system's own refusal (<see cref="TCV.Network.ReadOnlyFileSystem"/>).
    /// True when one of <paramref name="panels"/> stands on such a connection.
    /// </summary>
    private async Task<bool> RejectIfReadOnlyAsync(params PanelViewModel[] panels)
    {
        if (panels.Select(p => p.FileList.ActiveConnection).FirstOrDefault(c => c is { ReadOnly: true }) is not { } connection)
        {
            return false;
        }

        await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_ReadOnly"),
            string.Format(LocalizationManager.Get("Conn_ReadOnlyRefused"), PanelViewModel.ConnectionTitle(connection)));
        return true;
    }
    private PanelViewModel LeftPanel => _shell.LeftPanel;
    private PanelViewModel RightPanel => _shell.RightPanel;

    private IVirtualFileSystem SourceVfs => _shell.SourceVfs;
    private IVirtualFileSystem TargetVfs => _shell.TargetVfs;

    private IReadOnlyList<IFileEntry> GetSelectedOrCurrent() => _shell.GetSelectedOrCurrent();
    private Task<bool> RejectIfMountedAsync(string title) => _shell.RejectIfMountedAsync(title);
    private Task<bool> RejectIfNotOnDiskAsync(string title) => _shell.RejectIfNotOnDiskAsync(title);
    private IDisposable LeasePanelFileSystems() => _shell.LeasePanelFileSystems();
    private IConflictStrategy BuildConflictStrategy(ConflictMode mode) => _shell.BuildConflictStrategy(mode);
    private IConflictStrategy BuildAskStrategy() => _shell.BuildAskStrategy();
    private static string GetFileErrorMessage(Exception ex) => MainViewModel.GetFileErrorMessage(ex);

    private Task FinishOperationAsync(FileOperationBase op, ProgressDialogViewModel progress, string errorTitle) =>
        _shell.FinishOperationAsync(op, progress, errorTitle);

    /// <summary>
    /// Connects a user-driven operation to its progress window: Cancel stops it, and an item that
    /// fails is put to the user (retry / skip / abort) instead of aborting everything.
    /// </summary>
    private void Wire(FileOperationBase op, ProgressDialogViewModel progress)
    {
        op.LinkCancellation(progress.Cts.Token);
        op.ErrorHandler = _shell.BuildErrorHandler();
    }

    /// <summary>
    /// An F5/F6 destination made concrete: the file system it is on, the folder, and the new name when
    /// a single item is copied or moved under another name.
    /// </summary>
    private sealed record TransferTarget(IVirtualFileSystem Fs, VfsPath Directory, string? NewName);

    /// <summary>
    /// Reads what the user typed as a destination. Untouched, it is the folder the dialog offered. Edited,
    /// a relative path is taken from <paramref name="baseDirectory"/> (not from the process's working
    /// directory), a drive path means the disk, and a single item sent to a path that is not a folder
    /// is renamed on the way — F6 as rename, as in Total Commander. Call it under the panel lease:
    /// it may ask a server whether the path exists (INV-09).
    /// </summary>
    private async Task<TransferTarget> ResolveTransferTargetAsync(
        string typed, string offered, int itemCount,
        string baseDirectory, IVirtualFileSystem baseFs, IVirtualFileSystem offeredFs)
    {
        if (string.Equals(typed.Trim(), offered.Trim(), StringComparison.OrdinalIgnoreCase))
            return new TransferTarget(offeredFs, new VfsPath(offered), null);

        var (path, side) = OperationPaths.ResolveTypedTarget(
            typed, baseDirectory, IsLocalDisk(baseFs), IsLocalDisk(offeredFs));
        var fs = side switch
        {
            OperationPaths.TargetSide.Opposite => offeredFs,
            OperationPaths.TargetSide.Active => baseFs,
            _ => _fs,
        };

        var target = new VfsPath(path);
        IFileEntry? existing;
        try
        {
            existing = await fs.GetEntryAsync(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            existing = null;
        }

        var trimmed = typed.TrimEnd().TrimEnd('"');
        var markedAsFolder = trimmed.EndsWith('\\') || trimmed.EndsWith('/');
        var (directory, newName) = OperationPaths.SplitTarget(target, itemCount, existing, markedAsFolder);
        return new TransferTarget(fs, directory, newName);
    }

    private static bool IsLocalDisk(IVirtualFileSystem fs) => FileSystem.FileSystemIdentity.IsLocalDisk(fs);

    /// <summary>
    /// What a relative F5/F6 destination is read from: the active panel's folder. The search-results
    /// list is no folder — "backup" would become "/backup" in the list and be refused — so there it is
    /// the disk folder the hits were listed over (<see cref="FileListViewModel.WorkingFolder"/>).
    /// </summary>
    private (string Directory, IVirtualFileSystem Fs) TypedTargetBase() =>
        ActivePanel.FileList.IsSearchResults && ActivePanel.FileList.WorkingFolder is { } folder
            ? (folder, _fs)
            : (ActivePanel.CurrentPath, SourceVfs);

    /// <summary>Shows an error whose title and text are localisation keys.</summary>
    private Task ShowErrorKeyAsync(string titleKey, string messageKey) =>
        _dialogs.ShowErrorAsync(LocalizationManager.Get(titleKey), LocalizationManager.Get(messageKey));

    // ── Entry points other parts of the window call into ─────────────────────

    /// <summary>F5 on the current selection. Also what «copy» from the search results runs.</summary>
    internal Task CopySelectionAsync() => Copy();

    /// <summary>F6 on the current selection.</summary>
    internal Task MoveSelectionAsync() => Move();

    /// <summary>F8 on the current selection.</summary>
    internal Task DeleteSelectionAsync() => Delete();

    // F5 — Copy. Concurrent: a copy runs in the background (the progress window hides), and the next
    // F5 from the menu must not wait for it (ADR-0038).
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Copy()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0) return;

        // The panels as they are now: by the time the copy ends the user may be working in the other one (TRAP-92).
        var sourcePanel = ActivePanel;
        var targetPanel = OppositePanel;
        if (await RejectIfReadOnlyAsync(OppositePanel))
        {
            return;
        }

        // Archive entries are not reachable through the local file system — extract instead
        if (ActiveArchive is not null)
        {
            await CopyOutOfArchiveAsync(selected, isMove: false);
            return;
        }

        // Writing single entries into an archive is not supported — adding goes through
        // the archive engine, which repacks the whole file.
        var destArchive = TargetArchive;
        if (destArchive is not null && !await CanAddToArchiveAsync(destArchive))
        {
            return;
        }

        var offered = OppositePanel.CurrentPath;
        var result = await _dialogs.ShowCopyDialogAsync(selected, offered, isMove: false,
            destArchive is null ? CopyOptionSet.All : IntoArchiveOptions);
        if (result is null) return;

        var landsInActive = false;
        using (LeasePanelFileSystems())
        {
            // Read before the first await: an edited target is looked up on the server, and the user
            // may switch panels meanwhile — the selection belongs to this file system (TRAP-92).
            var sourceVfs = SourceVfs;
            var sourceFolder = new VfsPath(sourcePanel.CurrentPath);
            var (baseDirectory, baseFs) = TypedTargetBase();
            var target = await ResolveTransferTargetAsync(
                result.TargetPath, offered, selected.Count, baseDirectory, baseFs, TargetVfs);
            if (destArchive is not null && ReferenceEquals(target.Fs, destArchive))
            {
                await CopyIntoArchiveAsync(selected, destArchive, target, result.Options, isMove: false);
                return;
            }

            // Same, not ReferenceEquals: in a hit's folder opened from the search results a typed name
            // resolves on the disk (TypedTargetBase), while the panel lists that folder through the view.
            landsInActive = FileSystem.FileSystemIdentity.Same(target.Fs, sourceVfs)
                            && target.Directory.Equals(sourceFolder);

            var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Op_Copying"));
            var strategy = BuildConflictStrategy(result.Options.ConflictMode);
            var op = new CopyOperation(sourceVfs, target.Fs, selected, target.Directory,
                strategy, result.Options, _copyLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
                TargetName = target.NewName,
                CreateDestination = true,
                DuplicateInPlace = true,
            };
            Wire(op, progress);

            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, LocalizationManager.Get("Fc_CopyFailed"));
        }

        // A copy under another name, or into the folder it came from, can land in the active panel.
        if (landsInActive)
        {
            await ReloadAfterOperationAsync(sourcePanel);
        }

        await ReloadAfterOperationAsync(targetPanel);
    }

    /// <summary>
    /// Reloads a panel remembered at the start of an F5/F6/F8, which may have run in the background
    /// (ADR-0038). A name the user is editing there by now is not dropped by the reload (TRAP-76): the
    /// reload waits for the edit to end. <paramref name="beforeLoad"/> places the cursor for the reload.
    /// </summary>
    private static async Task ReloadAfterOperationAsync(PanelViewModel panel, Action<FileListViewModel>? beforeLoad = null)
    {
        if (panel.FileList.PostponeReloadForNameEdit())
        {
            return;
        }

        beforeLoad?.Invoke(panel.FileList);
        await panel.FileList.LoadAsync(panel.CurrentPath);
    }

    /// <summary>
    /// The archive whose entries the active panel lists: one being browsed, or one searched with
    /// Ctrl+F whose hits are shown in the panel (6.60). Its entries are read, extracted and deleted
    /// through the archive engine, never through the file system's single-entry calls.
    /// </summary>
    private ArchiveFileSystem? ActiveArchive => ActivePanel.FileList.ActiveVfs switch
    {
        ArchiveFileSystem archive when ActivePanel.FileList.IsInsideArchive => archive,
        FileSystem.SearchResultsFileSystem { Inner: ArchiveFileSystem archive } => archive,
        _ => null,
    };

    /// <summary>
    /// True, after saying why, when the archive the active panel shows cannot take an edit in place
    /// (F2, F7, Multi-Rename): a format the engine cannot write, or a scratch copy — an archive opened
    /// from a server or from inside another archive (<see cref="TemporaryFiles"/>), where the change
    /// would reach only the copy and be lost once the panel leaves it. False on anything but an archive.
    /// </summary>
    private async Task<bool> RejectArchiveEditAsync(VfsOperation operation, string titleKey, string unsupportedKey)
    {
        if (ActiveArchive is not { } archive)
        {
            return false;
        }

        if (!archive.SupportsOperation(operation))
        {
            await ShowErrorKeyAsync(titleKey, unsupportedKey);
            return true;
        }

        if (TemporaryFiles.Holds(archive.ArchivePath))
        {
            await ShowErrorKeyAsync(titleKey, "Fc_ArchiveTemporaryCopy");
            return true;
        }

        return false;
    }

    /// <summary>The archive open in the destination panel, or null when it is a normal directory.</summary>
    private ArchiveFileSystem? TargetArchive => OppositePanel.FileList.IsInsideArchive
        ? OppositePanel.FileList.ActiveVfs as ArchiveFileSystem
        : null;

    /// <summary>
    /// What the copy dialog offers when an archive is on one side: no threads (an archive is read in
    /// one pass and rewritten in one call), and no "keep the dates" when adding to it (it stores each
    /// file's time by itself).
    /// </summary>
    private const CopyOptionSet FromArchiveOptions = CopyOptionSet.Timestamps | CopyOptionSet.OnlyIfNewer;
    private const CopyOptionSet IntoArchiveOptions = CopyOptionSet.OnlyIfNewer;

    /// <summary>
    /// F5/F6 into a panel that has an archive open is possible: the files come from the disk and the
    /// archive takes additions. Said before the dialog, which would otherwise ask for nothing.
    /// </summary>
    private async Task<bool> CanAddToArchiveAsync(ArchiveFileSystem destArchive)
    {
        // Search hits are disk files and go in like any (TRAP-28); an archive's or a server's do not.
        if (!ActivePanel.FileList.EntriesOnDisk)
        {
            await ShowErrorKeyAsync("Fc_PackUnavailable", "Fc_PackLocalOnly");
            return false;
        }

        if (!destArchive.Engine.CanWrite)
        {
            await ShowErrorKeyAsync("Fc_ArchiveReadOnly", "Fc_ArchiveReadOnlyAdd");
            return false;
        }

        return true;
    }

    /// <summary>
    /// F5/F6 into a panel that has an archive open: hand the files to the archive engine, into the
    /// archive folder the dialog settled on (a single item under the new name typed for it), with the
    /// dialog's answer for names already in the archive.
    /// </summary>
    private Task CopyIntoArchiveAsync(
        IReadOnlyList<IFileEntry> selected, ArchiveFileSystem destArchive, TransferTarget target,
        CopyOptions options, bool isMove)
    {
        var paths = selected
            .Select(e => FileSystem.WindowsFileSystem.ToWindowsPath(e.Path))
            .ToArray();
        return AddToArchiveAsync(
            paths, destArchive, ArchiveFileSystem.NormalizePath(target.Directory.Value), target.NewName,
            isMove, BuildConflictStrategy(options.ConflictMode), options.OnlyIfNewer);
    }

    // F6 — Move. Concurrent, as F5.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task Move()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0) return;

        var sourcePanel = ActivePanel;
        var targetPanel = OppositePanel;
        if (await RejectIfReadOnlyAsync(ActivePanel, OppositePanel))
        {
            return;
        }

        if (ActiveArchive is not null)
        {
            await CopyOutOfArchiveAsync(selected, isMove: true);
            return;
        }

        var destArchive = TargetArchive;
        if (destArchive is not null && !await CanAddToArchiveAsync(destArchive))
        {
            return;
        }

        var offered = OppositePanel.CurrentPath;
        var result = await _dialogs.ShowCopyDialogAsync(selected, offered, isMove: true,
            destArchive is null ? CopyOptionSet.All : IntoArchiveOptions);
        if (result is null) return;

        string? renamedTo = null;
        using (LeasePanelFileSystems())
        {
            // Before the first await, as in F5: a move deletes what it took from this file system.
            var sourceVfs = SourceVfs;
            var (baseDirectory, baseFs) = TypedTargetBase();
            var target = await ResolveTransferTargetAsync(
                result.TargetPath, offered, selected.Count, baseDirectory, baseFs, TargetVfs);
            if (destArchive is not null && ReferenceEquals(target.Fs, destArchive))
            {
                await CopyIntoArchiveAsync(selected, destArchive, target, result.Options, isMove: true);
                return;
            }

            var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Op_Moving"));
            var strategy = BuildConflictStrategy(result.Options.ConflictMode);
            var op = new MoveOperation(sourceVfs, target.Fs, selected, target.Directory,
                strategy, result.Options, _moveLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
                TargetName = target.NewName,
                CreateDestination = true,
            };
            Wire(op, progress);

            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, LocalizationManager.Get("Fc_MoveFailed"));

            if (op.State == OperationState.Completed && FileSystem.FileSystemIdentity.Same(target.Fs, sourcePanel.FileList.ActiveVfs)
                && target.Directory.Equals(new VfsPath(sourcePanel.CurrentPath)))
            {
                renamedTo = target.NewName;
            }
        }

        await ReloadAfterOperationAsync(sourcePanel, list =>
        {
            // F6 as rename in place: the cursor follows the new name, as it does after F2.
            if (renamedTo is not null)
            {
                list.SetPendingSelectName(renamedTo);
            }

            list.PrepareForRefresh();   // preserve approx. scroll after files removed
        });
        await ReloadAfterOperationAsync(targetPanel);
    }

    /// <summary>
    /// F5/F6 while browsing an archive: extract the selection to the other panel, or wherever the
    /// dialog says, with its options — as F5 between two disk folders.
    /// </summary>
    private async Task CopyOutOfArchiveAsync(IReadOnlyList<IFileEntry> selected, bool isMove)
    {
        if (ActiveArchive is not { } archiveVfs) return;

        // The archive engine extracts to a directory on disk; a mounted panel's path would be
        // taken as a local one and the files would quietly land on the wrong file system. A hit's
        // folder opened from the search results is a disk folder.
        if (OppositePanel.FileList.DiskFolder is not { } offered)
        {
            await ShowErrorKeyAsync(isMove ? "Fc_MoveUnavailable" : "Fc_CopyUnavailable", "Fc_ExtractLocalOnly");
            return;
        }

        var result = await _dialogs.ShowCopyDialogAsync(selected, offered, isMove, FromArchiveOptions);
        if (result is null) return;

        // Read as F5 reads it anywhere else. A relative path is taken from where the user stands: the
        // disk folder the archive was opened from, as the active panel's folder for a disk-to-disk
        // copy. A single item sent to a path that is not a folder is extracted under that name.
        TransferTarget target;
        using (LeasePanelFileSystems())
        {
            target = await ResolveTransferTargetAsync(
                result.TargetPath, offered, selected.Count, ActivePanel.FileList.RootLocalPath, _fs, TargetVfs);
        }

        var entryPaths = selected
            .Select(e => ArchiveFileSystem.NormalizePath(e.Path.Value))
            .ToArray();

        await ExtractToDiskAsync(
            archiveVfs.ArchivePath, entryPaths, FileSystem.WindowsFileSystem.ToWindowsPath(target.Directory),
            target.NewName, isMove, result.Options, BuildConflictStrategy(result.Options.ConflictMode));
    }

    // F7 — New Folder
    [RelayCommand]
    private async Task NewFolder()
    {
        if (await RejectIfReadOnlyAsync(ActivePanel))
        {
            return;
        }

        // A ZIP takes an empty folder (a directory entry); a format the engine cannot write refuses here,
        // before the name is asked for. The archive's search hits are a list, not a folder: there, as in
        // the disk's, only a hit's folder takes one (SearchResultsFileSystem refuses the list itself).
        if (await RejectArchiveEditAsync(VfsOperation.CreateDirectory, "Fc_MkdirUnavailable", "Fc_MkdirInArchive"))
        {
            return;
        }

        var name = await _dialogs.ShowNewFolderDialogAsync();
        if (name is null) return;

        var vfs = SourceVfs;
        var path = new VfsPath(ActivePanel.CurrentPath).Combine(name);

        var existing = await vfs.GetEntryAsync(path);
        if (existing is not null)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Fc_MkdirFailed"),
                string.Format(LocalizationManager.Get("NewFolder_Exists"), name));
            return;
        }

        var op = new CreateDirectoryOperation(vfs, path, _mkdirLogger);
        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
        }
        if (op.State == OperationState.Failed && op.FailureException is not null)
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_MkdirFailed"), GetFileErrorMessage(op.FailureException));

        // Land the cursor on the new folder, as TC does.
        ActivePanel.FileList.SetPendingSelectName(name);
        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
    }

    // F8 — Delete. Concurrent, as F5.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Delete() => DeleteSelectedAsync(allowRecycleBin: true);

    // Shift+Delete — Delete permanently (bypass recycle bin)
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task DeletePermanent() => DeleteSelectedAsync(allowRecycleBin: false);

    private async Task DeleteSelectedAsync(bool allowRecycleBin, IReadOnlyList<IFileEntry>? entries = null)
    {
        var selected = entries ?? GetSelectedOrCurrent();
        if (selected.Count == 0) return;
        var panel = ActivePanel;
        if (await RejectIfReadOnlyAsync(ActivePanel))
        {
            return;
        }

        if (ActiveArchive is not null)
        {
            await DeleteFromArchiveAsync(selected);
            return;
        }

        var vfs = SourceVfs;

        // The bin only exists for fixed local drives. What the dialog promises is computed from the
        // same rule the operation applies, per item, so "Recycle Bin" is never said of a USB stick
        // or a network share (INV-11) — and is said of search hits on a fixed drive (TRAP-28).
        var wantBin = allowRecycleBin && _settings.Operations.MoveToRecycleBin && !ActivePanel.FileList.IsRemote;
        var recycleCount = wantBin ? RecycleBin.CountRecyclable(vfs, selected) : 0;

        var confirmed = await _dialogs.ShowDeleteConfirmAsync(selected, recycleCount);
        if (!confirmed) return;

        // Like a copy: a progress window counting what is gone, with Pause and a Cancel that stops
        // between two items. What went before it stays gone, or in the bin (6.36).
        using (LeasePanelFileSystems())
        {
            var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Op_Deleting"));
            var op = new DeleteOperation(vfs, selected, wantBin, _deleteLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
            };
            Wire(op, progress);

            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, LocalizationManager.Get("Fc_DeleteFailed"));
        }

        // Keep the cursor near where the files were.
        await ReloadAfterOperationAsync(panel, list => list.PrepareForRefresh());
    }

    private async Task DeleteFromArchiveAsync(IReadOnlyList<IFileEntry> entries)
    {
        if (ActiveArchive is not { } archiveVfs) return;

        // F8 runs concurrently (TRAP-92): by the time the rewrite ends another panel may be active.
        var panel = ActivePanel;

        if (!archiveVfs.Engine.CanWrite)
        {
            await ShowErrorKeyAsync("Fc_ArchiveReadOnly", "Fc_ArchiveReadOnlyDelete");
            return;
        }

        // The archive is rewritten and has no bin: the dialog says so (recycleCount 0).
        var confirmed = await _dialogs.ShowDeleteConfirmAsync(entries, recycleCount: 0);
        if (!confirmed) return;

        // One rewrite for the whole selection, all or nothing (6.35).
        var paths = entries.Select(e => e.Path.Value.TrimStart('/')).ToList();
        var op = new DelegateOperation(
            new OperationLogDescriptor(OperationKind.Delete,
                OperationLogDescriptor.DescribeEntries(entries), archiveVfs.ArchivePath, entries.Count),
            (_, ct) => Task.Run(() => archiveVfs.Engine.DeleteEntries(archiveVfs.ArchivePath, paths, ct), ct));

        // The archive file system must outlive the rewrite (INV-09).
        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
        }

        if (op.State == OperationState.Failed && op.FailureException is not null)
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_DeleteFailed"), op.FailureException.Message);

        archiveVfs.Invalidate();
        await panel.FileList.LoadAsync(panel.FileList.CurrentPath);
    }

    // F2 — Rename
    [RelayCommand]
    private Task Rename()
    {
        var item = ActivePanel.FileList.SelectedItem ?? ActivePanel.FileList.Files.FirstOrDefault();
        return item is null || item.IsParentEntry ? Task.CompletedTask : RenameAsync(item);
    }

    /// <summary>
    /// Renames one entry: the one under the cursor for F2, the one right-clicked for the shell menu's
    /// Rename (a marked file the cursor is not on). The name is edited in the list itself, as in Explorer
    /// and Total Commander; the Tree view, which has no rows, asks in a dialog.
    /// </summary>
    private async Task RenameAsync(FileEntryViewModel item)
    {
        if (await RejectIfReadOnlyAsync(ActivePanel))
        {
            return;
        }

        // Inside a ZIP (or its search hits) the entry is renamed by rewriting the archive, through the
        // panel's own file system so a renamed hit stays in the list (TRAP-28); other formats refuse.
        if (await RejectArchiveEditAsync(VfsOperation.Rename, "Fc_RenameUnavailable", "Fc_RenameInArchive"))
        {
            return;
        }

        // Taken now: a click on the other panel ends the edit and makes that one active.
        var panel = ActivePanel;
        var fileList = panel.FileList;
        var vfs = SourceVfs;
        var inPlace = fileList.CanEditNameInPlace;
        var proposed = item.Name;

        while (true)
        {
            var newName = inPlace
                ? await fileList.EditNameInPlaceAsync(item, proposed)
                : await _dialogs.ShowRenameDialogAsync(item.Name, item.IsDirectory);

            // Esc, or the name left as it was (a change of case is a rename).
            if (newName is null || newName == item.Name)
            {
                return;
            }

            // Through the queue and the log like every other mutation (INV-10): the rename shows up in
            // the operation log, so the agent-changes dashboard attributes it to the manager.
            var op = new RenameEntryOperation(vfs, item.Entry, newName, _logger);
            using (LeasePanelFileSystems())
            {
                await _operationQueue.EnqueueAsync(op);
            }

            if (op.State == OperationState.Failed && op.FailureException is not null)
            {
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_RenameFailed"), GetFileErrorMessage(op.FailureException));

                // As in Explorer: back into the name, with what was typed, to fix it (the name is
                // taken, a file is locked). The row must still be in the list to edit it, and its panel
                // still the active one: a click on the other panel ended the edit, and a box reopened
                // there would take the keys while the commands act on the panel clicked.
                if (inPlace && panel.IsActive && fileList.Files.Contains(item))
                {
                    proposed = newName;
                    continue;
                }

                return;
            }

            // The cursor follows the new name — unless a click on another row is what ended the edit:
            // then it stays on the row clicked.
            if (ReferenceEquals(fileList.SelectedItem, item))
            {
                fileList.SetPendingSelectName(newName);
            }
            else
            {
                fileList.PrepareForRefresh();
            }

            await fileList.LoadAsync(panel.CurrentPath);
            return;
        }
    }


    /// <summary>Goes into a folder in the active panel: Enter on it, or Open in its shell menu.</summary>
    private Task EnterDirectory(FileEntryViewModel item)
    {
        // Mounted file systems keep POSIX separators; only the local disk uses backslashes
        ActivePanel.CurrentPath = ActivePanel.FileList.IsMounted
            ? item.Entry.Path.Value
            : FileSystem.WindowsFileSystem.ToWindowsPath(item.Entry.Path);
        return Task.CompletedTask;
    }

    // F3/Enter — Open file or navigate into directory/archive
    [RelayCommand]
    private async Task OpenFile()
    {
        var item = ActivePanel.FileList.SelectedItem;
        if (item is null) return;

        if (item.IsParentEntry)
        {
            ActivePanel.FileList.NavigateUpCommand.Execute(null);
            return;
        }

        if (item.IsDirectory)
        {
            await EnterDirectory(item);
            return;
        }

        // Inside an archive or on a remote host — fetch to temp and open with the shell. Search hits
        // are disk files: a temporary copy of one would swallow the edits and be swept on exit.
        if (!ActivePanel.FileList.EntriesOnDisk)
        {
            var vfs = ActivePanel.FileList.ActiveVfs;
            try
            {
                // A folder of its own per open (two readme.txt from different places must not collide),
                // with a name Windows can hold, swept on exit: decrypted archive content does not linger.
                var tempPath = TemporaryFiles.PathFor(item.Name);
                using (LeasePanelFileSystems())
                {
                    var stream = await vfs.OpenReadAsync(item.Entry.Path);
                    await using (stream)
                    await using (var file = File.Create(tempPath))
                        await stream.CopyToAsync(file);
                }

                // An archive inside an archive (or on a server) opens in the panel like any other;
                // Backspace at its root returns to the one it came from.
                if (KnownArchiveExtensions.IsArchiveFileName(item.Name) && _archivePlugin.IsArchive(tempPath))
                {
                    await TryOpenArchiveInPanelAsync(tempPath);
                    return;
                }

                Shell32.ShellExecute(IntPtr.Zero, "open", tempPath, null, null, Shell32.SW_SHOWNORMAL);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open {Name} from {Vfs}", item.Name, vfs.DisplayName);
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_OpenFailed"), GetFileErrorMessage(ex));
            }
            return;
        }

        var winPath = FileSystem.WindowsFileSystem.ToWindowsPath(item.Entry.Path);

        // Open archives as virtual directories — by extension only (see ArchiveExtensions)
        if (KnownArchiveExtensions.IsArchiveFileName(item.Name) && _archivePlugin.IsArchive(winPath))
        {
            await TryOpenArchiveInPanelAsync(winPath);
            return;
        }

        Shell32.ShellExecute(IntPtr.Zero, "open", winPath, null, null, Shell32.SW_SHOWNORMAL);
    }

    // F3 — internal viewer (Lister): render the file's content in-app (text/hex/image) instead of
    // launching an external program. Reads through the active VFS, so archive and FTP entries are
    // viewed where they are (an archive entry is unpacked once for the window, not once per block of it,
    // and the copy goes with the window). Only the first window is read up front
    // (with progress and Cancel when it takes a while); the viewer reaches the rest of a big file itself
    // (Viewer/ViewerLauncher).
    [RelayCommand]
    private async Task ViewFile()
    {
        var item = ActivePanel.FileList.SelectedItem;
        if (item is null || item.IsParentEntry || item.IsDirectory) return;

        // The lease is handed to the viewer, which holds the mounted file system for as long as its
        // window is open — an unmount/navigate must not dispose it out from under a later read. The
        // panel's files as it shows them (sort, quick filter) go along for N/P, taken together with the
        // lease, so every one of them is on the leased file system (TRAP-37).
        var list = ActivePanel.FileList;
        var shown = list.Files.Where(f => !f.IsParentEntry && !f.IsDirectory).Select(f => f.Entry).ToList();
        var launcher = new Viewer.ViewerLauncher(_dialogs, _logger);
        await launcher.OpenAsync(
            list.ActiveVfs, item.Entry, list.LeaseActiveVfs(), _agents.Llm, shown,
            extractOnce: list.IsInsideArchive || ActiveArchive is not null);   // hits of an archive search too (6.37)
    }

    /// <summary>
    /// Mounts an archive into the active panel, asking for a password and retrying while the
    /// archive stays encrypted. Returns false when it could not be opened — cancelled or broken.
    /// </summary>
    internal async Task<bool> TryOpenArchiveInPanelAsync(string archivePath)
    {
        string? password = null;
        while (true)
        {
            try
            {
                var options = password is not null
                    ? new Dictionary<string, string> { ["password"] = password }
                    : null;

                // Create lists the whole archive (and probes the password): seconds of decoding for a
                // big 7z or ISO, so it runs off the UI thread (INV-06).
                IVirtualFileSystem archiveFs;
                System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
                try
                {
                    archiveFs = await Task.Run(() => _archivePlugin.Create(archivePath, options));
                }
                finally
                {
                    System.Windows.Input.Mouse.OverrideCursor = null;
                }

                ActivePanel.FileList.NavigateToArchive(archiveFs);
                return true;
            }
            catch (ArchivePasswordRequiredException)
            {
                // A second prompt says why it is back, as Alt+F9 does.
                var name = password is null
                    ? Path.GetFileName(archivePath)
                    : $"{Path.GetFileName(archivePath)} — {LocalizationManager.Get("Password_Wrong")}";
                password = await _dialogs.ShowPasswordDialogAsync(name);
                if (password is null) return false; // user cancelled
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to open archive {Path}", archivePath);
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_ArchiveOpenFailed"), GetFileErrorMessage(ex));
                return false;
            }
        }
    }

    // Shift+F4 — create a file and open it in the editor (an existing one is just opened), as in TC
    [RelayCommand]
    private async Task NewFile()
    {
        if (await RejectIfReadOnlyAsync(ActivePanel))
        {
            return;
        }

        var title = LocalizationManager.Get("Fc_NewFileTitle");
        if (ActiveArchive is not null)
        {
            await ShowErrorKeyAsync("Fc_NewFileTitle", "Fc_NewFileInArchive");
            return;
        }

        // A server gets the file through its own file system, a hit's folder opened from the search
        // results through the view (it forwards to the disk); the list itself is no folder to put it in.
        if (!ActivePanel.FileList.IsRemote && await _shell.DiskFolderOrRejectAsync(title) is null) return;

        var suggested = ActivePanel.FileList.SelectedItem is { IsParentEntry: false, IsDirectory: false } current
            ? current.Name
            : string.Empty;
        var name = (await _dialogs.ShowInputAsync(title, LocalizationManager.Get("Fc_NewFilePrompt"), suggested))?.Trim();
        if (string.IsNullOrEmpty(name)) return;

        if (FileNameRules.GetErrorKey(name) is { } invalid)
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_NewFileFailed"), LocalizationManager.Get(invalid));
            return;
        }

        var vfs = SourceVfs;
        var path = new VfsPath(ActivePanel.CurrentPath).Combine(name);

        using (LeasePanelFileSystems())
        {
            var existing = await vfs.GetEntryAsync(path);
            if (existing is { IsDirectory: true })
            {
                await _dialogs.ShowErrorAsync(
                    LocalizationManager.Get("Fc_NewFileFailed"),
                    string.Format(LocalizationManager.Get("NewFolder_Exists"), name));
                return;
            }

            if (existing is null)
            {
                var op = new CreateFileOperation(vfs, path, _logger);
                await _operationQueue.EnqueueAsync(op);
                if (op.State != OperationState.Completed)
                {
                    if (op.FailureException is not null)
                        await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_NewFileFailed"), GetFileErrorMessage(op.FailureException));
                    return;
                }
            }
        }

        // Land the cursor on the file, then edit it the way F4 would (a server file round-trips
        // through a watched temporary copy).
        ActivePanel.FileList.SetPendingSelectName(name);
        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
        if (string.Equals(ActivePanel.FileList.SelectedItem?.Name, name, StringComparison.OrdinalIgnoreCase))
            await EditFile();
    }

    // F4 — Edit file in the associated editor
    [RelayCommand]
    private async Task EditFile()
    {
        var item = ActivePanel.FileList.SelectedItem;
        if (item is null || item.IsParentEntry || item.IsDirectory) return;

        // The editor is handed a disk path and writes back to it, which a mounted panel does not
        // have — so there F4 round-trips through a watched temporary copy instead (6.23). Search
        // hits are the exception: they are disk files, and are edited where they are.
        if (ActiveArchive is not null)
        {
            await _dialogs.ShowInfoAsync(
                Controls.MenuHeaderText.Plain(LocalizationManager.Get("Menu_Edit")),
                LocalizationManager.Get("Edit_InsideArchive"));
            return;
        }

        if (!ActivePanel.FileList.EntriesOnDisk)
        {
            await _shell.EditRemoteFile();
            return;
        }

        var winPath = FileSystem.WindowsFileSystem.ToWindowsPath(item.Entry.Path);

        // The editor chosen in the settings, else the type's "edit" verb, else Notepad, like TC. A chosen
        // editor's command is looked up on PATH, which can hang on a dead share: off the UI thread (INV-06).
        // The Windows association is ShellExecute, which wants this STA thread, so it stays here.
        string? refused = null;
        var editor = _fileEditor.ConfiguredEditor;
        var opened = editor is null
            ? _fileEditor.TryOpen(winPath, out refused)
            : await Task.Run(() => _fileEditor.TryOpen(winPath, out refused));
        if (!opened)
        {
            _logger.LogWarning("Failed to edit {Path}", winPath);
            var title = Controls.MenuHeaderText.Plain(LocalizationManager.Get("Menu_Edit"));

            // A name with % or ! cannot go to a .cmd shim (code.cmd): the editor is fine, the file is the problem.
            await _dialogs.ShowErrorAsync(title, refused is null
                ? LocalizationManager.Format("Edit_LaunchFailed", editor ?? "notepad.exe")
                : LocalizationManager.Format("Edit_UnsafePath", editor ?? "notepad.exe", refused));
        }
    }

    // Ctrl+M — Multi-Rename
    [RelayCommand]
    private Task OpenMultiRename() => OpenMultiRenameAsync(GetSelectedOrCurrent());

    private async Task OpenMultiRenameAsync(IReadOnlyList<IFileEntry> selected)
    {
        if (selected.Count == 0) return;
        if (await RejectIfReadOnlyAsync(ActivePanel))
        {
            return;
        }

        // Inside a ZIP the whole batch is one rewrite of the archive (IBatchRenameFileSystem); a format
        // the engine cannot write refuses.
        if (await RejectArchiveEditAsync(VfsOperation.Rename, "Fc_MultiRenameUnavailable", "Fc_MultiRenameInArchive"))
        {
            return;
        }

        // Renames go through the panel's own file system, so a remote panel renames remotely. The dialog
        // renames (and undoes) while it is open: the mounted file system must outlive it (INV-09).
        var vm = new MultiRenameDialogViewModel(selected, SourceVfs, _multiRenameProfiles, _operationQueue);
        var dlg = new MultiRenameDialog(vm) { Owner = Application.Current.MainWindow };
        using (LeasePanelFileSystems())
        {
            dlg.ShowDialog();
        }

        // The dialog can rename and undo several times before closing — reload unconditionally.
        _ = ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
    }

    /// <summary>Portable store for Multi-Rename profiles (JSON next to the exe).</summary>
    private readonly Services.MultiRenameProfileStore _multiRenameProfiles = new();


    /// <summary>
    /// Drag &amp; Drop handler — called from FileListControl.OnDrop. Sources are always local
    /// paths; <paramref name="targetVfs"/> is set when the drop landed on a mounted panel.
    /// </summary>
    public async Task HandleDropAsync(
        string[] sourcePaths, string targetDirectory, bool isMove, IVirtualFileSystem? targetVfs = null)
    {
        if (sourcePaths.Length == 0 || string.IsNullOrEmpty(targetDirectory)) return;

        _logger.LogInformation("Drop {Count} files to {Target} (move={Move})", sourcePaths.Length, targetDirectory, isMove);

        // Build IFileEntry list from dropped paths
        var entries = new List<TCV.Core.Interfaces.IFileEntry>();
        foreach (var p in sourcePaths)
        {
            var entry = await _fs.GetEntryAsync(new VfsPath(p), CancellationToken.None);
            if (entry is not null)
                entries.Add(entry);
            else
                _logger.LogWarning("Cannot get entry for dropped path {Path}", p);
        }

        if (entries.Count == 0) return;

        await TransferDroppedAsync(
            _fs, entries, targetDirectory, targetVfs ?? _fs, isMove, [ActivePanel.FileList, OppositePanel.FileList]);
    }

    /// <summary>
    /// A drop of entries of a server panel onto the other panel: the transfer F5 would run for the same
    /// selection, with its copy dialog. <paramref name="sourceFs"/> is the file system the entries were
    /// listed from when the drag began; a panel that has since moved on to another one (a reconnect, a
    /// disconnect) no longer owns those paths, and the drop is dropped (TRAP-81).
    /// </summary>
    public async Task HandleDropFromPanelAsync(
        FileListViewModel source, IVirtualFileSystem sourceFs, IReadOnlyList<IFileEntry> entries,
        FileListViewModel target, bool isMove)
    {
        if (entries.Count == 0 || string.IsNullOrEmpty(target.CurrentPath)
            || !ReferenceEquals(source.ActiveVfs, sourceFs))
        {
            return;
        }

        // F6 refuses a read-only connection on either side, because a move deletes from the source.
        if (isMove && source.ActiveConnection is { ReadOnly: true } readOnly)
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_ReadOnly"),
                string.Format(LocalizationManager.Get("Conn_ReadOnlyRefused"), PanelViewModel.ConnectionTitle(readOnly)));
            return;
        }

        _logger.LogInformation("Drop {Count} entries between panels to {Target} (move={Move})",
            entries.Count, target.CurrentPath, isMove);

        await TransferDroppedAsync(sourceFs, entries, target.CurrentPath, target.ActiveVfs, isMove, [source, target]);
    }

    /// <summary>
    /// What both drops share: the copy dialog, then the copy or move through the queue, then a reload of
    /// the panels. <paramref name="panels"/> are held for the whole run so a disconnect cannot dispose
    /// the connection under it (INV-09).
    /// </summary>
    private async Task TransferDroppedAsync(
        IVirtualFileSystem sourceFs, IReadOnlyList<IFileEntry> entries, string targetDirectory,
        IVirtualFileSystem destFs, bool isMove, IReadOnlyList<FileListViewModel> panels)
    {
        // Held from before the dialog: a panel that disconnects or reconnects while the dialog is open
        // would otherwise close the file system the entries were listed from.
        var leases = panels.Select(p => p.LeaseActiveVfs()).ToList();
        try
        {
            var dialogResult = await _dialogs.ShowCopyDialogAsync(entries, targetDirectory, isMove);
            if (dialogResult is null) return;

            // An edited destination is read like F5's, relative to the folder the drop landed in.
            var target = await ResolveTransferTargetAsync(
                dialogResult.TargetPath, targetDirectory, entries.Count, targetDirectory, destFs, destFs);

            var progress = _dialogs.ShowProgressDialog(isMove ? LocalizationManager.Get("Op_Moving") : LocalizationManager.Get("Op_Copying"));
            var strategy = BuildConflictStrategy(dialogResult.Options.ConflictMode);

            FileOperationBase op = isMove
                ? new MoveOperation(sourceFs, target.Fs, entries, target.Directory,
                    strategy, dialogResult.Options, _moveLogger)
                {
                    Progress = progress,
                    PauseHandle = progress.PauseHandle,
                    TargetName = target.NewName,
                    CreateDestination = true,
                }
                : new CopyOperation(sourceFs, target.Fs, entries, target.Directory,
                    strategy, dialogResult.Options, _copyLogger)
                {
                    Progress = progress,
                    PauseHandle = progress.PauseHandle,
                    TargetName = target.NewName,
                    CreateDestination = true,
                    DuplicateInPlace = true,
                };
            Wire(op, progress);

            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, isMove ? LocalizationManager.Get("Fc_MoveFailed") : LocalizationManager.Get("Fc_CopyFailed"));
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }

        // Preserve scroll/selection state on both panels across the refresh
        LeftPanel.FileList.PrepareForRefresh();
        RightPanel.FileList.PrepareForRefresh();
        await LeftPanel.FileList.LoadAsync(LeftPanel.CurrentPath);
        await RightPanel.FileList.LoadAsync(RightPanel.CurrentPath);
    }

    // Drag & drop into and out of archives lives in FileCommands.Archive.cs.

    // Right-click — Shell context menu. Rename, Delete, Cut/Copy/Paste, New ▸ Folder and Open on a
    // folder are run by the manager's own commands (ShellMenuVerbs): through the queue and the log, with
    // our confirmation, on exactly the items the menu was opened for.
    [RelayCommand]
    private async Task ShowContextMenu(ContextMenuRequest req)
    {
        // Shell verbs only make sense for real disk paths — search hits have them (TRAP-28)
        var fileList = ActivePanel.FileList;
        if (!fileList.EntriesOnDisk)
        {
            return;
        }

        List<FileEntryViewModel> items = req.SelectedItems.Count > 0
            ? req.SelectedItems.Where(f => !f.IsParentEntry).ToList()
            : req.Target is { IsParentEntry: false } target ? [target] : [];

        // No items — a right-click on the empty space or on "..": the folder's background menu.
        var background = items.Count == 0;
        var scope = background
            ? ShellMenuScope.Background
            : ShellMenuScope.Items(items.Count, items.Count == 1 && items[0].IsDirectory);
        bool OwnsVerb(string verb) => ShellMenuVerbs.Route(verb, scope) switch
        {
            ShellMenuAction.Shell => false,
            // Paste takes file paths only. Files copied from where only the shell reads them (a zip
            // folder in Explorer, a phone, mail attachments) come as virtual files: those the shell pastes.
            ShellMenuAction.Paste => ClipboardHasFilesToPaste(),
            _ => true,
        };

        var owner = Application.Current.MainWindow;

        // Only the background menu has New ▸, whose new file the cursor lands on below.
        var namesBefore = background ? fileList.Files.Select(f => f.Name).ToList() : null;
        ShellMenuResult result;
        if (background)
        {
            if (string.IsNullOrEmpty(req.FolderPath))
            {
                return;
            }

            result = ShellContextMenu.ShowForFolderBackground(req.FolderPath, owner, req.ScreenPoint, req.Extended, OwnsVerb);
        }
        else
        {
            var paths = items
                .Select(f => FileSystem.WindowsFileSystem.ToWindowsPath(f.Entry.Path))
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();
            result = ShellContextMenu.ShowForItems(paths, owner, req.ScreenPoint, req.Extended, OwnsVerb);
        }

        if (!result.Picked)
        {
            return;
        }

        if (result.OwnVerb is { } verb)
        {
            var shift = System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift);
            await RunShellMenuActionAsync(ShellMenuVerbs.Route(verb, scope, shift), items, req.FolderPath);
            return;
        }

        // The shell ran it (Send to, Create shortcut, New ▸ Text Document…) and may have changed the folder.
        fileList.PrepareForRefresh();
        await fileList.LoadAsync(ActivePanel.CurrentPath);

        // New ▸ something makes one file with a placeholder name: the cursor lands on it, ready for F2.
        if (namesBefore is not null
            && ShellMenuVerbs.SingleNewName(namesBefore, fileList.Files.Select(f => f.Name)) is { } added
            && fileList.Files.FirstOrDefault(f => f.Name == added) is { } row)
        {
            fileList.MoveCursorTo(fileList.Files.IndexOf(row), extendMarks: false);
        }
    }

    private Task RunShellMenuActionAsync(ShellMenuAction action, IReadOnlyList<FileEntryViewModel> items, string folder)
    {
        var entries = items.Select(f => f.Entry).ToList();
        return action switch
        {
            ShellMenuAction.Rename => RenameAsync(items[0]),
            ShellMenuAction.MultiRename => OpenMultiRenameAsync(entries),
            ShellMenuAction.Delete => DeleteSelectedAsync(allowRecycleBin: true, entries),
            ShellMenuAction.DeletePermanent => DeleteSelectedAsync(allowRecycleBin: false, entries),
            ShellMenuAction.Cut => SetFileClipboardAsync(cut: true, entries),
            ShellMenuAction.Copy => SetFileClipboardAsync(cut: false, entries),
            // On the background into the folder itself; on a folder, into that folder.
            ShellMenuAction.Paste => PasteAsync(items.Count == 0
                ? folder
                : FileSystem.WindowsFileSystem.ToWindowsPath(items[0].Entry.Path)),
            ShellMenuAction.NewFolder => NewFolder(),
            ShellMenuAction.EnterFolder => EnterDirectory(items[0]),
            _ => Task.CompletedTask,
        };
    }

    /// <summary>
    /// The clipboard buffer stores disk paths (or archive entries) — a remote panel has neither,
    /// so clipboard commands are declined there instead of storing unusable paths. So are the hits
    /// of a search on a server or in an archive: neither disk paths nor entries of an open archive.
    /// </summary>
    private async Task<bool> RejectIfRemoteAsync()
    {
        var fileList = ActivePanel.FileList;
        if (fileList.IsRemote)
        {
            await ShowErrorKeyAsync("Fc_ClipboardUnavailable", "Fc_ClipboardRemote");
            return true;
        }

        if (fileList.IsSearchResults && !fileList.EntriesOnDisk)
        {
            await ShowErrorKeyAsync("Fc_ClipboardUnavailable", "Fc_ClipboardMountResults");
            return true;
        }

        return false;
    }

    // Ctrl+P — Copy the full path(s) of the selection to the clipboard as text.
    // Works on mounted panels too: there the VFS path (FTP/archive-internal) is the meaningful
    // path, so we copy it verbatim rather than mangling it through ToWindowsPath.
    [RelayCommand]
    private void CopyFullPathToClipboard()
    {
        var selected = GetSelectedOrCurrent();
        // Search hits are disk files: their path is the Windows one, not "C:/proj/a.cs".
        var mounted = !ActivePanel.FileList.EntriesOnDisk;

        // Nothing selected → the current folder; for the search-results list, which is no folder,
        // the one it works in (not its "/").
        var text = selected.Count == 0
            ? ActivePanel.FileList.WorkingFolder ?? ActivePanel.CurrentPath
            : string.Join(Environment.NewLine, selected.Select(e =>
                mounted ? e.Path.Value : FileSystem.WindowsFileSystem.ToWindowsPath(e.Path)));

        // Copying text changes nothing on screen; without a word the user cannot tell it worked.
        // Only when it did: a clipboard held by another program refuses the write.
        if (TrySetClipboardText(text))
        {
            _shell.ShowNotice(LocalizationManager.Format("Clip_PathsCopied", Math.Max(1, selected.Count)), NoticeTime);
        }
    }

    /// <summary>How long a "copied" confirmation stays after the caption.</summary>
    private static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(4);

    // Ctrl+Shift+P — Copy just the file name(s) of the selection to the clipboard as text.
    [RelayCommand]
    private void CopyNamesToClipboard()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0) return;

        var text = string.Join(Environment.NewLine, selected.Select(e => e.Name));
        if (TrySetClipboardText(text))
        {
            _shell.ShowNotice(LocalizationManager.Format("Clip_NamesCopied", selected.Count), NoticeTime);
        }
    }

    // «Copy link» (6.81): links anyone can open to read the marked files in S3 or Azure Blob for a while.
    // The parameter is the lifetime in hours from a menu item; the palette passes none and gets the shortest.
    [RelayCommand]
    private async Task CopyReadLink(string? hours)
    {
        if (ReadLinks.SourceOf(ActivePanel.FileList.ActiveVfs) is not { } source)
        {
            await ShowErrorKeyAsync("Fc_LinkUnavailable", "Fc_LinkNotCloud");
            return;
        }

        var files = GetSelectedOrCurrent().Where(e => !e.IsDirectory).ToList();
        if (files.Count == 0)
        {
            await ShowErrorKeyAsync("Fc_LinkUnavailable", "Fc_LinkNoFiles");
            return;
        }

        var lifetime = ReadLinks.Lifetime(hours);
        string? text;
        try
        {
            // An S3 connection may take its keys from its AWS profile, read from the disk on first use.
            text = await Task.Run(() => source.CanCreateReadLink ? ReadLinks.Text(source, files, lifetime) : null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or ArgumentException)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Fc_LinkUnavailable"), LocalizationManager.Format("Fc_LinkFailed", ex.Message));
            return;
        }

        if (text is null)
        {
            await ShowErrorKeyAsync("Fc_LinkUnavailable", "Fc_LinkNoKey");
            return;
        }

        if (TrySetClipboardText(text))
        {
            _shell.ShowNotice(
                LocalizationManager.Format("Clip_LinksCopied", files.Count, (DateTime.Now + lifetime).ToString("g")), NoticeTime);
        }
    }

    /// <summary>Sets clipboard text, swallowing the transient failure when another app holds it open.
    /// False when nothing was written (empty text, or the clipboard was busy).</summary>
    internal static bool TrySetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;   // clipboard busy
        }
    }

    // Reads clipboard text, tolerating a clipboard another app is holding open.
    internal static string? TryGetClipboardText()
    {
        try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
        catch (System.Runtime.InteropServices.ExternalException) { return null; }
    }

    // Ctrl+Num+ — Select entries by wildcard mask (TC's "Select group")
    [RelayCommand]
    private void SelectByMask() => ShowSelectByMaskDialog(select: true);

    // Ctrl+Num- — Unselect entries by wildcard mask (TC's "Unselect group")
    [RelayCommand]
    private void UnselectByMask() => ShowSelectByMaskDialog(select: false);

    private void ShowSelectByMaskDialog(bool select)
    {
        var vm = new SelectByMaskDialogViewModel(select);
        var dlg = new SelectByMaskDialog(vm) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true)
            ActivePanel.FileList.SelectByMask(vm.Mask, select);
    }

    // Ctrl+C — Copy selected files to clipboard
    [RelayCommand]
    private Task CopyToClipboard() => SetFileClipboardAsync(cut: false);

    // Ctrl+X — Cut selected files to clipboard
    [RelayCommand]
    private Task CutToClipboard() => SetFileClipboardAsync(cut: true);

    private async Task SetFileClipboardAsync(bool cut, IReadOnlyList<IFileEntry>? items = null)
    {
        var selected = items ?? GetSelectedOrCurrent();
        if (selected.Count == 0) return;
        if (await RejectIfRemoteAsync()) return;

        var isInArchive = ActivePanel.FileList.IsInsideArchive;
        var archiveVfs = ActivePanel.FileList.ActiveVfs as ArchiveFileSystem;
        var fullArchivePath = archiveVfs?.ArchivePath;

        var entries = selected.Select(e => new ClipboardEntry(
            SourcePath: isInArchive
                ? e.Path.Value.TrimStart('/')          // archive entry path e.g. "folder/file.txt"
                : FileSystem.WindowsFileSystem.ToWindowsPath(e.Path),
            DisplayName: e.Name,
            IsDirectory: e.IsDirectory,
            Size: e.Size,
            LastModified: e.LastModified,
            ArchivePath: isInArchive ? fullArchivePath : null
        )).ToList();

        if (cut) _clipboard.SetCut(entries);
        else _clipboard.SetCopy(entries);

        // Also set Windows clipboard for compatibility with Explorer (only for regular files)
        if (!isInArchive)
        {
            var paths = new System.Collections.Specialized.StringCollection();
            foreach (var entry in selected)
            {
                var winPath = FileSystem.WindowsFileSystem.ToWindowsPath(entry.Path);
                if (!string.IsNullOrEmpty(winPath)) { paths.Add(winPath); }
            }

            if (paths.Count > 0)
            {
                try
                {
                    // "Preferred DropEffect" is how Explorer tells copy from cut: without it a file cut
                    // here and pasted there would be copied and the original would stay.
                    var data = new DataObject();
                    data.SetFileDropList(paths);
                    data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
                    Clipboard.SetDataObject(data, copy: true);
                }
                catch (System.Runtime.InteropServices.ExternalException ex)
                {
                    _logger.LogWarning(ex, "The Windows clipboard was busy; the file list stays in the manager only");
                }
            }
        }

        // Remember which clipboard state this buffer belongs to. If another program copies
        // something later, the number moves and the buffer is discarded instead of pasted.
        _clipboard.Stamp = ClipboardApi.SequenceNumber();

        _shell.ShowNotice(
            LocalizationManager.Format(cut ? "Clip_FilesCut" : "Clip_FilesCopied", entries.Count),
            NoticeTime);
    }

    // Ctrl+V — Paste files from clipboard into active panel
    [RelayCommand]
    private Task PasteFromClipboard() => PasteAsync(intoFolder: null);

    /// <summary>
    /// Whether <see cref="PasteAsync"/> has anything to paste: the manager's own buffer, still current,
    /// or a list of file paths on the Windows clipboard. A busy clipboard counts as yes — the paste
    /// itself then says nothing came.
    /// </summary>
    private bool ClipboardHasFilesToPaste()
    {
        if (_clipboard.HasEntries && !_clipboard.IsStale(ClipboardApi.SequenceNumber()))
        {
            return true;
        }

        try
        {
            return Clipboard.ContainsFileDropList();
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return true;
        }
    }

    /// <summary>
    /// Pastes the clipboard into the active panel's folder, or into <paramref name="intoFolder"/> on the
    /// disk — the folder whose shell menu Paste was picked from.
    /// </summary>
    private async Task PasteAsync(string? intoFolder)
    {
        if (await RejectIfRemoteAsync()) return;

        // Our own buffer wins — unless another program has used the clipboard since it was filled.
        // Then it is stale: pasting it would bring back files the user copied minutes ago (and, for
        // a cut, take them from where they are).
        if (_clipboard.HasEntries && _clipboard.IsStale(ClipboardApi.SequenceNumber()))
            _clipboard.Clear();

        if (_clipboard.HasEntries)
        {
            await PasteFromInternalBuffer(intoFolder);
            return;
        }

        // Fallback to Windows clipboard. Another program may be holding it open: then there is
        // nothing to paste this time, not an exception out of the command.
        string[] paths;
        try
        {
            if (!Clipboard.ContainsFileDropList()) return;
            paths = Clipboard.GetFileDropList().Cast<string?>().Where(p => p is not null).Select(p => p!).ToArray();
        }
        catch (System.Runtime.InteropServices.ExternalException ex)
        {
            _logger.LogWarning(ex, "The Windows clipboard was busy; nothing pasted");
            return;
        }
        if (paths.Length == 0) return;

        var move = ClipboardWantsMove();

        // A panel showing an archive takes the files into the archive, like the internal buffer does.
        if (intoFolder is null && ActivePanel.FileList.IsInsideArchive)
        {
            if (ActivePanel.FileList.ActiveVfs is ArchiveFileSystem archiveVfs)
                await HandleDropToArchiveAsync(paths, archiveVfs, move);
            return;
        }

        // Anything else that is mounted (a server, the search-results list itself) has no disk folder
        // to paste into; its current path is not a place on the disk (INV-07). A hit's folder is one.
        var folder = intoFolder ?? await _shell.DiskFolderOrRejectAsync(LocalizationManager.Get("Fc_PasteUnavailable"));
        if (folder is null)
        {
            return;
        }

        var entries = new List<IFileEntry>();
        foreach (var path in paths)
        {
            var entry = await _fs.GetEntryAsync(new VfsPath(path));
            if (entry is not null)
                entries.Add(entry);
        }

        if (entries.Count == 0) return;

        var op = await RunOnDiskAsync(move, entries, new VfsPath(folder), BuildAskStrategy());

        // Explorer's cut is consumed by the paste.
        if (move && op.State == OperationState.Completed)
        {
            try { Clipboard.Clear(); }
            catch (System.Runtime.InteropServices.ExternalException) { /* busy: harmless */ }
        }

        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
    }

    /// <summary>True when the file list on the Windows clipboard was cut (Explorer's "Preferred DropEffect" is Move).</summary>
    private static bool ClipboardWantsMove()
    {
        try
        {
            if (Clipboard.GetData("Preferred DropEffect") is MemoryStream stream && stream.Length >= 4)
            {
                var bytes = new byte[4];
                stream.Position = 0;
                _ = stream.Read(bytes, 0, 4);
                return (BitConverter.ToInt32(bytes, 0) & 2) != 0;
            }
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Busy clipboard: treat as a plain copy, which cannot lose anything.
        }
        return false;
    }

    /// <summary>
    /// Copies or moves disk entries into a folder on the disk as a queued operation with a progress
    /// window and Cancel, error prompts and the log — the one path every paste goes through.
    /// </summary>
    private async Task<FileOperationBase> RunOnDiskAsync(
        bool isMove, IReadOnlyList<IFileEntry> entries, VfsPath destination, IConflictStrategy strategy)
    {
        var progress = _dialogs.ShowProgressDialog(
            LocalizationManager.Get(isMove ? "Op_Moving" : "Op_Copying"));

        FileOperationBase op = isMove
            ? new MoveOperation(_fs, _fs, entries, destination, strategy, _moveLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
            }
            : new CopyOperation(_fs, _fs, entries, destination, strategy, new CopyOptions(), _copyLogger)
            {
                Progress = progress,
                PauseHandle = progress.PauseHandle,
                // Ctrl+C, Ctrl+V in the same folder makes a copy beside the original, as in Explorer.
                DuplicateInPlace = true,
            };
        Wire(op, progress);

        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress,
                LocalizationManager.Get(isMove ? "Fc_MoveFailed" : "Fc_CopyFailed"));
        }

        return op;
    }

    private async Task PasteFromInternalBuffer(string? intoFolder)
    {
        if (!_clipboard.HasEntries) return;

        // If destination is inside an open archive, add files into it
        if (intoFolder is null && ActivePanel.FileList.IsInsideArchive)
        {
            await PasteToArchiveAsync();
            return;
        }

        // Servers and the search-results list itself show paths that are not a folder on the disk
        // (INV-07). A hit's folder opened from the list is one.
        var folder = intoFolder ?? await _shell.DiskFolderOrRejectAsync(LocalizationManager.Get("Fc_PasteUnavailable"));
        if (folder is null)
        {
            return;
        }

        var destPath = new VfsPath(folder);
        var isCut = _clipboard.Operation == ClipboardOperation.Cut;
        var everythingArrived = true;

        // Group entries by source (archive vs regular)
        var archiveGroups = _clipboard.Entries.GroupBy(e => e.ArchivePath).ToList();

        foreach (var group in archiveGroups)
        {
            if (group.Key is null)
            {
                // Regular files: copy, or for a cut a real move — one operation that leaves the
                // source alone unless the item arrived, instead of copy-then-delete.
                var entries = new List<IFileEntry>();
                foreach (var entry in group)
                {
                    var fileEntry = await _fs.GetEntryAsync(new VfsPath(entry.SourcePath));
                    if (fileEntry is not null)
                    {
                        entries.Add(fileEntry);
                    }
                    else
                    {
                        everythingArrived = false;
                        _logger.LogWarning("Clipboard source no longer exists: {Path}", entry.SourcePath);
                    }
                }

                if (entries.Count == 0) continue;

                var op = await RunOnDiskAsync(isCut, entries, destPath, BuildAskStrategy());
                if (op.State != OperationState.Completed || op.SkippedCount > 0)
                    everythingArrived = false;
            }
            else
            {
                // Files from an archive: extract them here; a cut then removes exactly what landed.
                var items = group.ToList();
                if (await OpenArchiveEngineAsync(group.Key) is not { } opened) { everythingArrived = false; continue; }
                var (engine, archiveEntries) = opened;

                var extractItems = items
                    .Select(e => new ArchiveExtractItem(
                        ArchiveFileSystem.NormalizePath(e.SourcePath),
                        e.DisplayName, e.IsDirectory, e.Size, e.LastModified))
                    .ToList();

                var landed = await ExtractItemsAsync(
                    engine, group.Key, extractItems, destPath.Value, BuildAskStrategy(), archiveEntries);
                if (landed.Count != extractItems.Count) everythingArrived = false;

                if (isCut && landed.Count > 0)
                {
                    if (engine.CanWrite)
                        await DeleteArchiveEntriesAsync(group.Key, landed.Select(i => i.SourcePath).ToList());
                    else
                        await ShowErrorKeyAsync("Fc_ArchiveReadOnly", "Fc_MoveKeptInArchive");
                }
            }
        }

        if (isCut && !everythingArrived)
            await ShowErrorKeyAsync("Fc_MoveIncomplete", "Fc_PasteIncomplete");

        // A cut is used up by the paste; a copy stays for pasting again, as in Explorer.
        if (isCut) _clipboard.Clear();
        await ActivePanel.FileList.LoadAsync(ActivePanel.CurrentPath);
    }

    // Archive paste, extraction and the queued archive operations live in FileCommands.Archive.cs.

    // Alt+Enter (when focus on file list) — Show Windows Properties dialog
    [RelayCommand]
    private async Task ShowWindowsProperties()
    {
        var item = ActivePanel.FileList.SelectedItem ?? ActivePanel.FileList.Files.FirstOrDefault();
        if (item is null || item.IsParentEntry) return;

        // Shell verbs only make sense for real disk paths — which search hits have (TRAP-28)
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get("Fc_PropertiesUnavailable")))
        {
            return;
        }

        var winPath = FileSystem.WindowsFileSystem.ToWindowsPath(item.Entry.Path);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(Application.Current.MainWindow).Handle;
        Shell32.ShowProperties(winPath, hwnd);
    }

    // Alt+Shift+Enter — the size of every folder in the active panel, in its size column (TC). Through the
    // panel's own file system, so it works in an archive and on a server too.
    [RelayCommand]
    private void CalculateFolderSizes() => ActivePanel.FileList.MeasureAllFolderSizes();

    /// <summary>
    /// Alt+F9 as the window dispatches it: unpacking needs the archive and the destination on the
    /// disk, so a mounted panel on either side is declined first (INV-07) — except search results
    /// as the source, whose hits are disk files (TRAP-28), and a hit's folder as the destination.
    /// Without this the opposite panel's "/pub" would be taken as a folder on the current drive.
    /// </summary>
    [RelayCommand]
    private async Task UnpackChecked()
    {
        if (await RejectIfNotOnDiskAsync(LocalizationManager.Get("Fc_UnpackUnavailable")))
        {
            return;
        }

        if (OppositePanel.FileList.DiskFolder is null)
        {
            await ShowErrorKeyAsync("Fc_UnpackUnavailable", "Fc_UnpackDestinationOnDisk");
            return;
        }

        await UnpackFiles();
    }
}
