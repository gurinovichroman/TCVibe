using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.ViewModels;

/// <summary>
/// Moving files between the disk and archives: extracting, adding, deleting entries and the
/// clipboard and drag-and-drop paths that lead there. Every one of these mutates something, so each
/// runs as a queued, logged operation off the UI thread (INV-06, INV-10) with a progress window
/// and a working Cancel, and none of them removes a source before the copy is known to have landed.
/// </summary>
public sealed partial class FileCommands
{
    // ── Shared plumbing ───────────────────────────────────────────────────────

    /// <summary>The archive a panel lists: browsed, or under the hits of a search in it (6.60).</summary>
    private static ArchiveFileSystem? ArchiveShownIn(PanelViewModel panel) => panel.FileList.ActiveVfs switch
    {
        ArchiveFileSystem archive => archive,
        FileSystem.SearchResultsFileSystem { Inner: ArchiveFileSystem archive } => archive,
        _ => null,
    };

    private PanelViewModel? GetPanelWithArchive(string archivePath)
    {
        if (LeftPanel.FileList.ActiveVfs is ArchiveFileSystem lVfs &&
            string.Equals(lVfs.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase))
            return LeftPanel;
        if (RightPanel.FileList.ActiveVfs is ArchiveFileSystem rVfs &&
            string.Equals(rVfs.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase))
            return RightPanel;
        // A search-results list over the archive is re-read too, so a deleted hit leaves it.
        return new[] { LeftPanel, RightPanel }.FirstOrDefault(p =>
            ArchiveShownIn(p) is { } afs && string.Equals(afs.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase));
    }

    private void InvalidateOpenArchive(string archivePath)
    {
        foreach (var panel in new[] { LeftPanel, RightPanel })
        {
            if (ArchiveShownIn(panel) is { } afs &&
                string.Equals(afs.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                afs.Invalidate();
            }
        }
    }

    /// <summary>
    /// The engine for reading <paramref name="archivePath"/> and its listing. The password typed when
    /// the archive was opened is reused; when there is none (or it no longer fits) the user is asked,
    /// and asked again after a wrong one. Null = unknown format, unreadable, or the prompt was cancelled.
    /// </summary>
    private async Task<(IArchiveEngine Engine, IReadOnlyList<ArchiveEntry> Entries)?> OpenArchiveEngineAsync(string archivePath)
    {
        if (_archivePlugin.DetectFormat(archivePath) is null)
        {
            await _dialogs.ShowErrorAsync(
                LocalizationManager.Get("Fc_UnknownFormat"),
                string.Format(LocalizationManager.Get("Fc_UnknownFormatBody"), Path.GetFileName(archivePath)));
            return null;
        }

        string? password = null;
        var retry = false;
        while (true)
        {
            var engine = _archivePlugin.CreateEngine(archivePath, password);
            try
            {
                // Off the UI thread: listing a big 7z is seconds of decoding (INV-06).
                var entries = await Task.Run(() => engine.ListAll(archivePath));
                if (password is not null)
                {
                    _archivePlugin.RememberPassword(archivePath, password);
                }

                return (engine, entries);
            }
            catch (ArchivePasswordRequiredException)
            {
                if (password is null)
                {
                    _archivePlugin.ForgetPassword(archivePath);
                }

                var name = retry
                    ? $"{Path.GetFileName(archivePath)} — {LocalizationManager.Get("Password_Wrong")}"
                    : Path.GetFileName(archivePath);
                password = await _dialogs.ShowPasswordDialogAsync(name);
                if (password is null)
                {
                    return null;
                }

                retry = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read archive {Archive}", archivePath);
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_ExtractFailed"), GetFileErrorMessage(ex));
                return null;
            }
        }
    }

    /// <summary>
    /// Runs a change to an archive file as a queued operation: progress window, working Cancel,
    /// error prompts, a line in the log — and the panels showing that archive refreshed afterwards.
    /// </summary>
    private async Task<DelegateOperation> RunArchiveMutationAsync(
        string archivePath, string progressTitleKey, OperationLogDescriptor descriptor,
        Func<DelegateOperation, CancellationToken, Task> work)
    {
        var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get(progressTitleKey));
        // An archive rewrite is one call into the archive library, with nowhere to wait.
        progress.CanPause = false;
        var op = new DelegateOperation(descriptor, work)
        {
            Progress = progress,
            PauseHandle = progress.PauseHandle,
        };
        Wire(op, progress);

        // The archive stays mounted for as long as the rewrite runs (INV-09).
        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, LocalizationManager.Get("Fc_ArchiveFailed"));
        }

        InvalidateOpenArchive(archivePath);
        var panel = GetPanelWithArchive(archivePath);
        if (panel is not null)
            await panel.FileList.LoadAsync(panel.FileList.CurrentPath);

        return op;
    }

    /// <summary>
    /// Deletes the local sources of a completed move into an archive. Through the queue, to the
    /// Recycle Bin when the setting and the drive allow it: the move is only "done" once the files
    /// are safely inside the archive, and this is the last step of it.
    /// </summary>
    private async Task DeleteLocalSourcesAsync(IEnumerable<string> windowsPaths)
    {
        var entries = new List<IFileEntry>();
        foreach (var p in windowsPaths)
        {
            var entry = await _fs.GetEntryAsync(new VfsPath(p), CancellationToken.None);
            if (entry is not null) entries.Add(entry);
        }
        if (entries.Count == 0) return;

        var op = new DeleteOperation(_fs, entries, _settings.Operations.MoveToRecycleBin, _deleteLogger)
        {
            ErrorHandler = _shell.BuildErrorHandler(),
        };
        await _operationQueue.EnqueueAsync(op);
        if (op.HasUnreportedFailure)
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Fc_DeleteFailed"), GetFileErrorMessage(op.FailureException!));
    }

    // ── Adding to an archive ──────────────────────────────────────────────────

    /// <summary>
    /// Asks before adding would replace entries already in the archive folder. True = go ahead
    /// (nothing is replaced, or the user agreed).
    /// </summary>
    private async Task<bool> ConfirmArchiveReplaceAsync(ArchiveFileSystem archiveVfs, string archiveSubDir, IEnumerable<string> names)
    {
        IReadOnlyList<IFileEntry> existing;
        try
        {
            existing = await archiveVfs.ListAsync(new VfsPath("/" + archiveSubDir));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to list {Archive} before adding", archiveVfs.ArchivePath);
            existing = [];
        }

        var taken = existing.Select(e => e.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var clashes = names.Where(taken.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clashes.Count == 0)
        {
            return true;
        }

        var shown = string.Join(", ", clashes.Take(5)) + (clashes.Count > 5 ? ", …" : string.Empty);
        return await _dialogs.ConfirmAsync(
            LocalizationManager.Get("Fc_ArchiveReplaceTitle"),
            LocalizationManager.Format("Fc_ArchiveReplaceMessage", clashes.Count, shown));
    }

    // Drag & Drop — drop regular files INTO an open archive panel (archiving)
    public async Task HandleDropToArchiveAsync(string[] sourcePaths, ArchiveFileSystem archiveVfs, bool isMove)
    {
        if (sourcePaths.Length == 0)
        {
            return;
        }

        if (!archiveVfs.Engine.CanWrite)
        {
            await ShowErrorKeyAsync("Fc_ArchiveReadOnly", "Fc_ArchiveReadOnlyAdd");
            return;
        }

        // Find the panel that has this archive open — it is the drop target, not ActivePanel
        var archivePanel = GetPanelWithArchive(archiveVfs.ArchivePath);
        var archiveSubDir = archivePanel is not null
            ? ArchiveFileSystem.NormalizePath(archivePanel.FileList.CurrentPath)
            : string.Empty;

        // Adding replaces entries of the same name, and a replaced entry is gone (INV-11).
        if (!await ConfirmArchiveReplaceAsync(archiveVfs, archiveSubDir,
                sourcePaths.Select(p => Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar)))))
        {
            return;
        }

        // Confirmed above: what is in the way is replaced.
        await AddToArchiveAsync(sourcePaths, archiveVfs, archiveSubDir, newName: null, isMove,
            new OverwriteStrategy(), onlyIfNewer: false);
    }

    /// <summary>
    /// Adds disk files and folders to <paramref name="archiveVfs"/> under <paramref name="archiveSubDir"/>
    /// in one queued rewrite. A name already in the archive is settled file by file by
    /// <paramref name="conflicts"/> and "only if newer" (<see cref="ArchiveAddPlanner"/>), before the
    /// archive is touched. A move then deletes exactly the sources that went in whole: not one with a
    /// file left out, a link that was not followed, or the archive itself inside it.
    /// </summary>
    /// <param name="newName">The name a single source gets in the archive (F5 to "/docs/new.txt").</param>
    private async Task AddToArchiveAsync(
        IReadOnlyList<string> sourcePaths, ArchiveFileSystem archiveVfs, string archiveSubDir, string? newName,
        bool isMove, IConflictStrategy conflicts, bool onlyIfNewer)
    {
        // The source panel is the one without the archive.
        var archivePanel = GetPanelWithArchive(archiveVfs.ArchivePath);
        PanelViewModel sourcePanel = archivePanel == LeftPanel ? RightPanel : LeftPanel;

        var engine = archiveVfs.Engine;
        var archivePath = archiveVfs.ArchivePath;
        var source = sourcePaths.Count == 1
            ? sourcePaths[0]
            : $"{Path.GetDirectoryName(sourcePaths[0])} ({sourcePaths.Count})";
        IReadOnlySet<int> incomplete = new HashSet<int>();

        var op = await RunArchiveMutationAsync(
            archivePath, "Fc_AddingToArchive",
            new OperationLogDescriptor(OperationKind.Pack, source, archivePath, sourcePaths.Count),
            async (self, ct) =>
            {
                // Walking a big folder and listing a big archive are disk work: here, not on the UI thread.
                var (files, notWhole) = await Task.Run(
                    () => CollectForArchive(sourcePaths, newName, archivePath, archiveSubDir, self, ct), ct);
                var existing = await Task.Run(() => engine.ListAll(archivePath), ct);

                var plan = await ArchiveAddPlanner.PlanAsync(
                    files, existing, conflicts, onlyIfNewer, DescribeLocalFile, ct);
                for (var i = 0; i < plan.Skipped; i++)
                {
                    self.NoteSkipped();   // the user's own choice: counted, not repeated as a warning
                }

                notWhole.UnionWith(plan.IncompleteOwners);
                incomplete = notWhole;

                ct.ThrowIfCancellationRequested();
                if (plan.Files.Count > 0)
                {
                    await Task.Run(() => engine.AddFiles(archivePath, plan.Files, ct), ct);
                }
            });

        // A move takes the sources away only after the archive really has them.
        if (isMove && op.State == OperationState.Completed)
        {
            var whole = sourcePaths.Where((_, i) => !incomplete.Contains(i)).ToList();
            if (whole.Count > 0)
            {
                await DeleteLocalSourcesAsync(whole);
            }

            sourcePanel.FileList.PrepareForRefresh();
            await sourcePanel.FileList.LoadAsync(sourcePanel.CurrentPath);
        }
    }

    /// <summary>
    /// The files below each source as archive entries, and the sources that cannot go in whole
    /// (<see cref="ArchiveAddCollector.CollectSources"/>). Each one left out is reported on <paramref name="op"/>.
    /// </summary>
    private static (List<ArchiveAddFile> Files, HashSet<int> NotWhole) CollectForArchive(
        IReadOnlyList<string> sourcePaths, string? newName, string archivePath, string archiveSubDir,
        DelegateOperation op, CancellationToken ct) =>
        ArchiveAddCollector.CollectSources(sourcePaths, newName, archivePath, archiveSubDir,
            (reason, name) => op.ReportSkipped(string.Format(LocalizationManager.Get(reason switch
            {
                ArchiveAddSkip.ArchiveItself => "Fc_ArchiveNotIntoItself",
                _ => "Fc_LinkNotArchived",
            }), name)),
            ct);

    private static (long Size, DateTime Modified) DescribeLocalFile(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTime) : (0, DateTime.MinValue);
    }

    // ── Extracting from an archive ────────────────────────────────────────────

    // Drag & Drop — drag archive entries OUT of an archive panel (extracting)
    public async Task HandleDropFromArchiveAsync(
        string archivePath, string[] entryPaths, string targetDirectory, bool isMove, bool targetIsMounted = false)
    {
        if (entryPaths.Length == 0 || string.IsNullOrEmpty(targetDirectory))
        {
            return;
        }

        // The engine writes to a disk folder; a mounted panel's "/pub" would be taken as "\pub" on the
        // current drive (INV-07).
        if (targetIsMounted)
        {
            await ShowErrorKeyAsync(isMove ? "Fc_MoveUnavailable" : "Fc_CopyUnavailable", "Fc_ExtractLocalOnly");
            return;
        }

        await ExtractToDiskAsync(archivePath, entryPaths, targetDirectory, newName: null, isMove,
            new CopyOptions(), BuildAskStrategy());
    }

    /// <summary>
    /// Extracts archive entries into a disk folder (F5, a drop), then for a move deletes from the
    /// archive exactly what landed. Names that are taken, "only if newer" and the timestamps follow
    /// <paramref name="options"/> and <paramref name="conflicts"/>, as a copy between disk folders does.
    /// </summary>
    /// <param name="newName">The name a single entry gets on the disk (F5 to «D:\x\new.txt»).</param>
    private async Task ExtractToDiskAsync(
        string archivePath, IReadOnlyList<string> entryPaths, string targetDirectory, string? newName,
        bool isMove, CopyOptions options, IConflictStrategy conflicts)
    {
        if (await OpenArchiveEngineAsync(archivePath) is not { } opened)
        {
            return;
        }

        var (engine, allEntries) = opened;

        var items = entryPaths
            .Select(p => ArchiveFileSystem.NormalizePath(p))
            .Select(p => ToExtractItem(p, allEntries))
            .ToList();
        if (newName is not null && items.Count == 1)
        {
            items[0] = items[0] with { DisplayName = newName };
        }

        var landed = await ExtractItemsAsync(engine, archivePath, items, targetDirectory, conflicts, allEntries, options);

        if (isMove)
        {
            if (!engine.CanWrite)
            {
                // Do not let a move silently degrade into a copy
                await ShowErrorKeyAsync("Fc_ArchiveReadOnly", "Fc_MoveKeptInArchive");
            }
            else if (landed.Count > 0)
            {
                // Only what actually reached the disk leaves the archive. An entry the user skipped
                // at a name conflict, or that failed, is still needed there.
                await DeleteArchiveEntriesAsync(archivePath, landed.Select(i => i.SourcePath).ToList());
            }
        }

        var otherPanel = ActivePanel == LeftPanel ? RightPanel : LeftPanel;
        otherPanel.FileList.PrepareForRefresh();
        await otherPanel.FileList.LoadAsync(otherPanel.CurrentPath);
    }

    private static ArchiveExtractItem ToExtractItem(string entryPath, IReadOnlyList<ArchiveEntry> allEntries)
    {
        // Engines differ on whether FullPath carries a leading slash (ISO/CAB do, ZIP does not)
        var meta = allEntries.FirstOrDefault(e =>
            string.Equals(ArchiveFileSystem.NormalizePath(e.FullPath), entryPath,
                StringComparison.OrdinalIgnoreCase));

        return new ArchiveExtractItem(
            entryPath,
            meta?.Name ?? Path.GetFileName(entryPath),
            meta?.IsDirectory ?? false,
            meta?.Size ?? 0,
            meta?.LastModified ?? DateTime.MinValue);
    }

    /// <summary>
    /// Removes entries from an archive as a queued, logged operation. One rewrite for the whole batch,
    /// all or nothing, so «retry» after an error starts the batch over instead of continuing a half-done one.
    /// </summary>
    private Task<DelegateOperation> DeleteArchiveEntriesAsync(string archivePath, IReadOnlyList<string> entryPaths)
    {
        var format = _archivePlugin.DetectFormat(archivePath);
        var engine = format is null ? null : _archivePlugin.EngineFactory.Create(format);
        var descriptor = new OperationLogDescriptor(
            OperationKind.Delete,
            entryPaths.Count == 1 ? entryPaths[0] : $"{archivePath} ({entryPaths.Count})",
            archivePath, entryPaths.Count);

        return RunArchiveMutationAsync(archivePath, "Fc_DeletingFromArchive", descriptor, (self, ct) => Task.Run(async () =>
        {
            if (engine is null || !engine.CanWrite)
                throw new IOException(LocalizationManager.Get("Fc_ArchiveReadOnlyDelete"));

            await self.GuardAsync(new VfsPath(archivePath), () =>
            {
                engine.DeleteEntries(archivePath, entryPaths, ct);
                return Task.CompletedTask;
            }, ct);
        }, ct));
    }

    /// <summary>
    /// Extracts archive entries into a folder on disk as a queued operation and returns the items
    /// that really landed there — so a move deletes exactly those from the archive and nothing more.
    /// The work itself (every name settled first, then one read of the archive) is
    /// <see cref="ArchiveExtraction"/>.
    /// </summary>
    private async Task<IReadOnlyList<ArchiveExtractItem>> ExtractItemsAsync(
        IArchiveEngine engine, string archivePath,
        IReadOnlyList<ArchiveExtractItem> items, string destDir, IConflictStrategy conflicts,
        IReadOnlyList<ArchiveEntry>? knownEntries = null, CopyOptions? options = null)
    {
        IReadOnlyList<ArchiveExtractItem> landed = [];
        var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Fc_Extracting"));

        var op = new DelegateOperation(
            new OperationLogDescriptor(OperationKind.Extract, archivePath, destDir, items.Count) { ArrivedIn = destDir },
            async (self, ct) =>
            {
                // Off the UI thread: listing and decompressing a big archive is real work (INV-06).
                var entries = knownEntries
                    ?? (items.Any(i => i.IsDirectory)
                        ? await Task.Run(() => engine.ListAll(archivePath), ct)
                        : Array.Empty<ArchiveEntry>());

                var extraction = new ArchiveExtraction(engine, archivePath, entries, self, conflicts, options);
                landed = await extraction.RunAsync(items, destDir, ct);
            })
        {
            Progress = progress,
            PauseHandle = progress.PauseHandle,
        };
        Wire(op, progress);

        using (LeasePanelFileSystems())
        {
            await _operationQueue.EnqueueAsync(op);
            await FinishOperationAsync(op, progress, LocalizationManager.Get("Fc_ExtractFailed"));
        }

        // A cancelled or failed run may have written some entries, but "moved" is only true of a
        // completed one: report nothing as landed rather than delete from the archive on a guess.
        return op.State == OperationState.Completed ? landed : Array.Empty<ArchiveExtractItem>();
    }

    // ── Paste into and out of archives ────────────────────────────────────────

    private async Task PasteToArchiveAsync()
    {
        if (ActivePanel.FileList.ActiveVfs is not ArchiveFileSystem archiveVfs) return;

        if (!archiveVfs.Engine.CanWrite)
        {
            await ShowErrorKeyAsync("Fc_ArchiveReadOnly", "Fc_ArchiveReadOnlyAdd");
            return;
        }

        // Sub-path inside archive where files should land (e.g. "subdir" or "")
        var archiveSubDir = ArchiveFileSystem.NormalizePath(ActivePanel.FileList.CurrentPath);
        var entries = _clipboard.Entries.ToList();
        var isCut = _clipboard.Operation == ClipboardOperation.Cut;
        var engine = archiveVfs.Engine;
        var archivePath = archiveVfs.ArchivePath;

        if (!await ConfirmArchiveReplaceAsync(archiveVfs, archiveSubDir, entries.Select(e => e.DisplayName)))
            return;

        var tempDir = Path.Combine(Path.GetTempPath(), "TCV_paste_" + Guid.NewGuid().ToString("N")[..8]);
        var localSources = entries.Where(e => e.ArchivePath is null).Select(e => e.SourcePath).ToList();
        IReadOnlySet<int> localNotWhole = new HashSet<int>();
        var archiveSourcesWhole = true;

        var op = await RunArchiveMutationAsync(
            archivePath, "Fc_AddingToArchive",
            new OperationLogDescriptor(OperationKind.Pack,
                entries.Count == 1 ? entries[0].SourcePath : $"{entries[0].DisplayName} (+{entries.Count - 1})",
                archivePath, entries.Count),
            (self, ct) => Task.Run(() =>
            {
                Directory.CreateDirectory(tempDir);
                try
                {
                    // Disk sources are collected as F6 collects them: the archive itself, or a folder
                    // holding it, is reported and kept, so a cut does not delete the archive with it.
                    var (local, notWhole) = CollectForArchive(localSources, null, archivePath, archiveSubDir, self, ct);
                    localNotWhole = notWhole;
                    var filesToAdd = local.Select(f => (f.LocalPath, f.EntryName)).ToList();

                    var staged = new List<(string ArchivePath, string EntryPath, string TempFile)>();
                    foreach (var entry in entries.Where(e => e.ArchivePath is not null))
                    {
                        ct.ThrowIfCancellationRequested();
                        // Extracted from the source archive to a temp file first — below, off the UI thread.
                        if (entry.IsDirectory)
                        {
                            self.ReportSkipped(string.Format(LocalizationManager.Get("Fc_ArchiveFolderNotCopied"), entry.DisplayName));
                            archiveSourcesWhole = false;
                            continue;
                        }

                        if (_archivePlugin.DetectFormat(entry.ArchivePath!) is null)
                        {
                            self.ReportSkipped(string.Format(LocalizationManager.Get("Fc_ArchiveSourceUnknown"), entry.DisplayName));
                            archiveSourcesWhole = false;
                            continue;
                        }

                        // Stage each entry in its own folder — entries from different archive
                        // directories can share a name
                        var stageDir = Path.Combine(tempDir, staged.Count.ToString());
                        Directory.CreateDirectory(stageDir);
                        var tempFile = Path.Combine(stageDir, ArchivePathSafety.SanitizeSegment(entry.DisplayName));
                        staged.Add((entry.ArchivePath!, entry.SourcePath, tempFile));
                        filesToAdd.Add((tempFile, ArchiveAddCollector.JoinEntryPath(archiveSubDir, entry.DisplayName)));
                    }

                    // One read per source archive: entry by entry, a solid 7z or RAR is decoded from
                    // the start for every file.
                    foreach (var source in staged.GroupBy(s => s.ArchivePath, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        // With the password the source archive was opened with this session.
                        var srcEngine = _archivePlugin.CreateEngine(source.Key);
                        srcEngine.ExtractEntries(source.Key, source.Select(s => (s.EntryPath, s.TempFile)).ToList(), ct);
                    }

                    ct.ThrowIfCancellationRequested();
                    if (filesToAdd.Count > 0)
                        engine.AddFiles(archivePath, filesToAdd, ct);
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { /* ignore */ }
                }
            }, ct));

        if (!isCut || op.State != OperationState.Completed)
        {
            if (isCut)
            {
                await ShowErrorKeyAsync("Fc_MoveIncomplete", "Fc_PasteArchiveIncomplete");
            }

            return;
        }

        // Cut takes away exactly the originals that are inside the archive whole (TRAP-46): not a
        // folder with a link left out, nor one holding the archive itself.
        var wholeLocal = localSources.Where((_, i) => !localNotWhole.Contains(i)).ToList();
        if (wholeLocal.Count > 0)
        {
            await DeleteLocalSourcesAsync(wholeLocal);
        }

        if (archiveSourcesWhole)
        {
            foreach (var group in entries.Where(e => e.ArchivePath is not null).GroupBy(e => e.ArchivePath!))
            {
                await DeleteArchiveEntriesAsync(group.Key, group.Select(e => e.SourcePath).ToList());
            }
        }

        // A cut is used up by the paste, as on the disk; what stayed behind is said.
        _clipboard.Clear();
        if (localNotWhole.Count > 0 || !archiveSourcesWhole)
        {
            await ShowErrorKeyAsync("Fc_MoveIncomplete", "Fc_PasteArchiveIncomplete");
        }
    }

}
