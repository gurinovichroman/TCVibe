using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.ViewModels;

/// <summary>
/// Packing (Alt+F5) and unpacking (Alt+F9). Kept in its own file: the flow around the archive
/// engines — overwrite confirmation, password prompt and retry, cancellation, scratch-file swap —
/// is long, and none of it is about the panel-to-panel file operations in <c>FileCommands.cs</c>.
/// The mechanics are in <see cref="PackJob"/> and <see cref="UnpackPlanner"/>, which tests cover.
/// </summary>
public sealed partial class FileCommands
{
    // Alt+F5 — Pack selected files
    [RelayCommand]
    private async Task PackFiles()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0)
        {
            return;
        }

        var title = LocalizationManager.Get("Pack_Unavailable");

        // Both the BCL engines and 7z.exe take disk paths for the items (search hits have them) and the archive
        if (await RejectIfNotOnDiskAsync(title))
        {
            return;
        }

        // The archive is put into a disk folder: a hit's folder opened from the search results is one.
        if (OppositePanel.FileList.DiskFolder is not { } destFolder)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Pack_LocalOnly"));
            return;
        }

        var sevenZipAvailable = WindowsArchiveService.IsSevenZipAvailable();
        var result = await _dialogs.ShowPackDialogAsync(
            selected,
            defaultDestDir: destFolder,
            sevenZipAvailable);

        if (result is null)
        {
            return;
        }

        // A typed relative folder means one under the folder the dialog offered, not under the
        // process's working directory (next to the exe).
        if (!Path.IsPathRooted(result.ArchivePath))
        {
            result = result with { ArchivePath = Path.GetFullPath(Path.Combine(destFolder, result.ArchivePath)) };
        }

        // Only "add to an existing ZIP" keeps what the archive holds; every other run replaces the
        // target, and a replaced archive is gone — so ask (INV-11).
        var addsInPlace = result.AddToExisting && result.Format == ArchiveFormat.Zip
                          && string.IsNullOrEmpty(result.Password);
        if (File.Exists(result.ArchivePath) && !addsInPlace)
        {
            var replace = await _dialogs.ConfirmAsync(
                LocalizationManager.Get("Pack_OverwriteTitle"),
                string.Format(LocalizationManager.Get("Pack_OverwriteMessage"), Path.GetFileName(result.ArchivePath)));
            if (!replace)
            {
                return;
            }
        }

        var sources = selected
            .Select(e => FileSystem.WindowsFileSystem.ToWindowsPath(e.Path))
            .ToList();

        // Through the queue and the log like every other change to the disk (INV-10).
        var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Pack_Progress"));
        progress.CanPause = false;   // the packer runs in one call, with nowhere to wait
        var request = result;
        var op = new DelegateOperation(
            new OperationLogDescriptor(OperationKind.Pack, OperationLogDescriptor.DescribeEntries(selected),
                request.ArchivePath, selected.Count)
            {
                ArrivedIn = Path.GetDirectoryName(request.ArchivePath),
            },
            (self, ct) => PackJob.RunAsync(
                request, sources, _archivePlugin, sevenZipAvailable,
                new Progress<double>(p => progress.Percentage = p * 100), ct,
                name => self.ReportSkipped(string.Format(LocalizationManager.Get("Fc_LinkNotArchived"), name))));
        op.LinkCancellation(progress.Cts.Token);

        await _operationQueue.EnqueueAsync(op);
        switch (op.State)
        {
            case OperationState.Completed:
                progress.Complete();
                await OppositePanel.FileList.LoadAsync(OppositePanel.CurrentPath);
                _logger.LogInformation("Packed {Count} item(s) to {Archive}", selected.Count, request.ArchivePath);
                // Links left out are said, not silently missing from the archive.
                await _shell.ReportWarningsAsync(op);
                break;
            case OperationState.Failed when op.FailureException is { } ex:
                progress.Dismiss();
                _logger.LogError(ex, "Pack failed");
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Pack_Failed"), GetFileErrorMessage(ex));
                break;
            default:
                progress.Dismiss();
                _logger.LogInformation("Pack cancelled");
                break;
        }
    }


    // Alt+F9 — Unpack the selected archives (or the one under the cursor) to the opposite panel
    [RelayCommand]
    private async Task UnpackFiles()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0)
        {
            return;
        }

        var title = LocalizationManager.Get("Unpack_Title");

        // The archive is read from disk (a search hit is on it) and written to disk (INV-07)
        if (await RejectIfNotOnDiskAsync(title))
        {
            return;
        }

        // Written into a disk folder: a hit's folder opened from the search results is one.
        if (OppositePanel.FileList.DiskFolder is not { } destFolder)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Unpack_LocalOnly"));
            return;
        }

        // As in Total Commander: every selected archive, the one under the cursor when nothing is
        // selected; whatever else is selected is passed over. Telling an archive by its first bytes
        // is a read of every file: off the UI thread (INV-06).
        var archives = await Task.Run(() => UnpackSelection.Pick(
            selected.Select(e => (e.Name, Path: FileSystem.WindowsFileSystem.ToWindowsPath(e.Path), e.IsDirectory)),
            _archivePlugin.IsArchive,
            path => _archivePlugin.DetectFormat(path) == "zip"));

        if (archives.Count == 0)
        {
            // It used to return without a word, which reads as "the key is broken".
            var message = selected.Count == 1
                ? string.Format(LocalizationManager.Get("Unpack_NotAnArchive"), selected[0].Name)
                : LocalizationManager.Get("Unpack_NoArchiveSelected");
            await _dialogs.ShowInfoAsync(title, message);
            return;
        }

        var result = await _dialogs.ShowUnpackDialogAsync(
            archiveNames: archives.Select(a => a.Name).ToList(),
            defaultDestDir: destFolder,
            isZip: archives.All(a => a.IsZip));

        if (result is null)
        {
            return;
        }

        // A relative folder is one under the folder the dialog offered, not under the process's
        // working directory (next to the exe).
        var destination = result.DestinationPath.Trim();
        if (!Path.IsPathRooted(destination))
        {
            destination = Path.GetFullPath(Path.Combine(destFolder, destination));
        }

        // One after another, each with its own password and overwrite question: the second archive
        // asks about the files the first one has just written. Cancel stops the rest.
        foreach (var archive in archives)
        {
            if (!await UnpackArchiveAsync(archive.Name, archive.Path, archive.IsZip, destination, result, title))
            {
                break;
            }
        }

        await OppositePanel.FileList.LoadAsync(OppositePanel.CurrentPath);
    }

    /// <summary>
    /// Unpacks one archive into <paramref name="destination"/>. False when the user cancelled the
    /// extraction, so the archives after it are left alone; a declined question, a wrong password or
    /// a broken archive only leaves this one out.
    /// </summary>
    private async Task<bool> UnpackArchiveAsync(
        string name, string winPath, bool isZip, string destination, UnpackDialogResult result, string title)
    {
        // Listing the archive doubles as the password check: an encrypted one throws here, where
        // the prompt can be shown and retried, instead of failing halfway through the extraction.
        string? password = null;
        var retry = false;
        IArchiveEngine engine;
        IReadOnlyList<ArchiveEntry> entries;
        while (true)
        {
            engine = _archivePlugin.CreateEngine(winPath, password);
            try
            {
                var current = engine;
                entries = await Task.Run(() => current.ListAll(winPath));
                break;
            }
            catch (ArchivePasswordRequiredException)
            {
                if (password is null)
                {
                    _archivePlugin.ForgetPassword(winPath);   // a remembered one that no longer fits
                }

                var prompt = retry ? $"{name} — {LocalizationManager.Get("Password_Wrong")}" : name;
                password = await _dialogs.ShowPasswordDialogAsync(prompt);
                if (password is null)
                {
                    return true;
                }

                retry = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read archive {Archive}", winPath);
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Unpack_Failed"), GetFileErrorMessage(ex));
                return true;
            }
        }

        if (password is not null)
        {
            _archivePlugin.RememberPassword(winPath, password);
        }

        IReadOnlyList<string> overwrites;
        try
        {
            overwrites = UnpackPlanner.FindOverwrites(entries, destination);
        }
        catch (InvalidDataException ex)
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Unpack_Failed"),
                string.Format(LocalizationManager.Get("Unpack_UnsafeEntry"), ex.Message));
            return true;
        }

        if (overwrites.Count > 0)
        {
            var proceed = await _dialogs.ConfirmAsync(title,
                LocalizationManager.Format("Unpack_OverwriteMessage", overwrites.Count, destination));
            if (!proceed)
            {
                return true;
            }
        }

        // Through the queue and the log like every other change to the disk (INV-10).
        var progress = _dialogs.ShowProgressDialog(LocalizationManager.Get("Unpack_Progress"));
        progress.CanPause = false;   // nothing in this path waits on the pause handle
        var useShell = result.UseWindowsShell && password is null && isZip;
        var op = new DelegateOperation(
            new OperationLogDescriptor(OperationKind.Extract, winPath, destination, entries.Count) { ArrivedIn = destination },
            async (_, ct) =>
            {
                // The Windows shell cannot decrypt, so an encrypted archive always uses our engine.
                if (useShell)
                {
                    var extracted = await WindowsArchiveService.ExtractZipSilentAsync(
                        winPath, destination, new Progress<double>(p => progress.Percentage = p * 100), ct);

                    if (!extracted)
                    {
                        throw new InvalidOperationException(LocalizationManager.Get("Unpack_ShellFailed"));
                    }
                }
                else
                {
                    var progressReport = new Progress<double>(p => progress.Percentage = p * 100);
                    await Task.Run(() => engine.ExtractAll(winPath, destination, progressReport, ct), ct);
                }
            });
        op.LinkCancellation(progress.Cts.Token);

        await _operationQueue.EnqueueAsync(op);
        switch (op.State)
        {
            case OperationState.Completed:
                progress.Complete();
                _logger.LogInformation("Extracted {Archive} to {Dest}", winPath, destination);
                return true;
            case OperationState.Failed when op.FailureException is ArchivePasswordRequiredException:
                progress.Dismiss();
                _archivePlugin.ForgetPassword(winPath);
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Unpack_Failed"),
                    LocalizationManager.Get("Password_Wrong"));
                return true;
            case OperationState.Failed when op.FailureException is { } ex:
                progress.Dismiss();
                _logger.LogError(ex, "Extract failed");
                await _dialogs.ShowErrorAsync(LocalizationManager.Get("Unpack_Failed"), GetFileErrorMessage(ex));
                return true;
            default:
                progress.Dismiss();
                _logger.LogInformation("Extract cancelled");
                await _dialogs.ShowInfoAsync(title, LocalizationManager.Get("Unpack_Cancelled"));
                return false;
        }
    }
}
