using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TCV.App.Localization;
using TCV.Core.Interfaces;
using TCV.Tools;

namespace TCV.App.ViewModels.Dialogs;

public partial class ChecksumDialogViewModel : ObservableObject, IDisposable
{
    private readonly ChecksumEngine _engine;
    private readonly string _activeDirectory;
    private CancellationTokenSource? _cts;

    public ChecksumDialogViewModel(
        IReadOnlyList<IFileEntry> files,
        string activeDirectory,
        ChecksumEngine engine)
    {
        _engine = engine;
        _activeDirectory = activeDirectory;

        foreach (var f in files.Where(f => !f.IsDirectory))
        {
            var winPath = f.Path.Value.Replace('/', Path.DirectorySeparatorChar);
            Entries.Add(new ChecksumEntryViewModel(f.Name, winPath, f.Size));
        }

        // Folders hold no single digest. Say so instead of quietly hashing nothing.
        SkippedFolders = files.Count(f => f.IsDirectory);
        _statusText = Entries.Count == 0
            ? LocalizationManager.Get("CS_NoFiles")
            : SkippedFolders > 0
                ? string.Format(LocalizationManager.Get("CS_IdleWithFolders"), SkippedFolders)
                : LocalizationManager.Get("CS_Idle");

        // Opened on a .sha256 (or another list the engine reads): the window starts on the Verify tab
        // and checks it. The file still sits on the Calculate tab for whoever wants its own digest.
        ChecksumFileToVerify = VerifyTargetFor(files);
        _verifyMode = ChecksumFileToVerify is not null;
    }

    /// <summary>
    /// The checksum file to verify when the dialog is opened on exactly one, as a Windows path;
    /// null when the selection is something to hash. Several files are always a hashing run, even if
    /// some are checksum lists: the user picked them to get their digests.
    /// </summary>
    public static string? VerifyTargetFor(IReadOnlyList<IFileEntry> files)
    {
        if (files.Count != 1 || files[0] is not { IsDirectory: false } file
            || !ChecksumEngine.IsChecksumFileName(file.Name))
        {
            return null;
        }

        return file.Path.Value.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>The checksum file the dialog verifies as soon as it is shown; null for a hashing run.</summary>
    public string? ChecksumFileToVerify { get; }

    /// <summary>Called once the window is up: verifies <see cref="ChecksumFileToVerify"/> when there is one.</summary>
    public Task StartAsync() =>
        ChecksumFileToVerify is { } sums ? VerifyFileAsync(sums) : Task.CompletedTask;

    // ── Observable collections & properties ──────────────────────────────────

    public ObservableCollection<ChecksumEntryViewModel> Entries { get; } = new();

    /// <summary>Selected folders that were left out.</summary>
    public int SkippedFolders { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalculateCommand))]
    private ChecksumAlgorithm _selectedAlgorithm = ChecksumAlgorithm.MD5;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalculateCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCalculationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SaveChecksumFileCommand))]
    private bool _isCalculating;

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private string _statusText;
    [ObservableProperty] private bool _verifyMode;

    // Verify-mode state
    [ObservableProperty] private string _verifyFilePath = string.Empty;
    public ObservableCollection<VerifyEntryViewModel> VerifyResults { get; } = new();

    /// <summary>
    /// The digests on screen belong to the algorithm that produced them, not to whichever radio
    /// button is selected now. Switching algorithm therefore drops them — leaving them would let
    /// an MD5 value be saved in a file labelled SHA256 — and, if a run had finished, starts a new one.
    /// </summary>
    partial void OnSelectedAlgorithmChanged(ChecksumAlgorithm value)
    {
        var hadResults = Entries.Any(e => !string.IsNullOrEmpty(e.Hash));
        _cts?.Cancel();
        foreach (var e in Entries)
        {
            e.Hash = string.Empty;
        }

        SaveChecksumFileCommand.NotifyCanExecuteChanged();
        if (hadResults && Entries.Count > 0)
        {
            _ = CalculateAsync();
        }
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanCalculate))]
    private Task Calculate() => CalculateAsync();

    private bool CanCalculate() => Entries.Count > 0 && !IsCalculating;

    private async Task CalculateAsync()
    {
        if (Entries.Count == 0)
        {
            return;
        }

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;

        IsCalculating = true;
        ProgressValue = 0;
        StatusText = LocalizationManager.Get("CS_Working");

        foreach (var e in Entries)
        {
            e.Hash = string.Empty;
        }

        var algorithm = SelectedAlgorithm;
        var paths = Entries.Select(e => e.FilePath).ToList();
        var totalBytes = Math.Max(1, Entries.Sum(e => e.FileSize));
        var sizeBefore = new long[Entries.Count];
        for (var i = 1; i < sizeBefore.Length; i++)
        {
            sizeBefore[i] = sizeBefore[i - 1] + Entries[i - 1].FileSize;
        }

        var progress = new Progress<ChecksumProgress>(p =>
        {
            if (ct.IsCancellationRequested || p.FileIndex >= Entries.Count)
            {
                return;
            }

            var done = sizeBefore[p.FileIndex] + Math.Min(p.BytesDone, Entries[p.FileIndex].FileSize);
            ProgressValue = (double)done / totalBytes * 100.0;
            StatusText = string.Format(LocalizationManager.Get("CS_FileOf"), p.FileIndex + 1, p.TotalFiles, p.FileName);
        });

        try
        {
            await foreach (var result in _engine.ComputeManyWithProgressAsync(paths, algorithm, progress, ct))
            {
                var entry = Entries.FirstOrDefault(e => e.FilePath == result.FilePath);
                if (entry is not null)
                {
                    entry.Hash = result.Hash;
                }
            }

            ProgressValue = 100;
            StatusText = string.Format(LocalizationManager.Get("CS_Done"), Entries.Count);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationManager.Get("CS_Cancelled");
        }
        finally
        {
            // A run that was replaced by a newer one must not switch the buttons back on under it.
            if (ReferenceEquals(_cts, cts))
            {
                IsCalculating = false;
            }

            SaveChecksumFileCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(IsCalculating))]
    private void CancelCalculation() => _cts?.Cancel();

    /// <summary>Stops any run in progress; the dialog calls it on close so hashing does not carry on unseen.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts = null;
    }

    /// <summary>
    /// Saves the digests in the format that matches the chosen algorithm: an <c>.sfv</c> for CRC32
    /// (the SFV format), a labelled <c>.md5/.sha1/.sha256/.sha512</c> for the rest.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveChecksumFile()
    {
        var results = BuildResults();
        if (results.Count == 0)
        {
            return;
        }

        var ext = ChecksumEngine.Extension(SelectedAlgorithm);   // md5 / sha1 / sha256 / sha512 / sfv
        var dlg = new SaveFileDialog
        {
            Title = LocalizationManager.Get("CS_SaveTitle"),
            Filter = $"{SelectedAlgorithm} (*.{ext})|*.{ext}|{LocalizationManager.Get("CS_AllFiles")} (*.*)|*.*",
            FileName = $"checksums.{ext}",
            InitialDirectory = _activeDirectory,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            // CRC32 is the SFV format ("name hash"); the rest use BSD-labelled lines.
            if (SelectedAlgorithm == ChecksumAlgorithm.CRC32)
            {
                await _engine.WriteSfvFileAsync(dlg.FileName, results);
            }
            else
            {
                await _engine.WriteChecksumFileAsync(dlg.FileName, results);
            }

            StatusText = string.Format(LocalizationManager.Get("CS_Saved"), dlg.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = string.Format(LocalizationManager.Get("CS_SaveFailed"), ex.Message);
        }
    }

    private bool CanSave() => !IsCalculating && Entries.Any(e => IsUsableHash(e.Hash));

    [RelayCommand]
    private async Task LoadChecksumForVerify()
    {
        var dlg = new OpenFileDialog
        {
            Title = LocalizationManager.Get("CS_OpenTitle"),
            Filter = LocalizationManager.Get("CS_OpenFilter"),
            InitialDirectory = _activeDirectory,
        };

        if (dlg.ShowDialog() != true)
        {
            return;
        }

        await VerifyFileAsync(dlg.FileName);
    }

    /// <summary>Verifies the files named in a checksum file; the results land in <see cref="VerifyResults"/>.</summary>
    public async Task VerifyFileAsync(string checksumFile)
    {
        // Cancellable like a hashing run: Stop works, and closing the window stops the reading.
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        VerifyFilePath = checksumFile;
        VerifyMode = true;
        VerifyResults.Clear();
        IsCalculating = true;
        StatusText = LocalizationManager.Get("CS_Verifying");

        try
        {
            var results = await _engine.VerifyChecksumFileAsync(checksumFile, cts.Token);
            foreach (var r in results)
            {
                VerifyResults.Add(new VerifyEntryViewModel(r));
            }

            var ok = results.Count(r => r.IsOk);
            var failed = results.Count(r => r.Status == "FAILED");
            var missing = results.Count(r => r.Status == "Missing");
            StatusText = string.Format(LocalizationManager.Get("CS_VerifyResult"), results.Count, ok, failed, missing);
        }
        catch (OperationCanceledException)
        {
            StatusText = LocalizationManager.Get("CS_Cancelled");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = string.Format(LocalizationManager.Get("CS_VerifyReadError"), ex.Message);
        }
        finally
        {
            // As in CalculateAsync: a run replaced by a newer one leaves the buttons to it.
            if (ReferenceEquals(_cts, cts))
            {
                IsCalculating = false;
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsUsableHash(string hash) =>
        !string.IsNullOrEmpty(hash) && !hash.StartsWith("[ERROR", StringComparison.Ordinal);

    private IReadOnlyList<ChecksumResult> BuildResults()
    {
        return Entries
            .Where(e => IsUsableHash(e.Hash))
            .Select(e => new ChecksumResult
            {
                FilePath = e.FilePath,
                FileName = e.FileName,
                FileSize = e.FileSize,
                Hash = e.Hash,
                Algorithm = SelectedAlgorithm,
            })
            .ToList();
    }
}

// ── Row view-models ───────────────────────────────────────────────────────────

public partial class ChecksumEntryViewModel : ObservableObject
{
    public ChecksumEntryViewModel(string fileName, string filePath, long fileSize)
    {
        FileName = fileName;
        FilePath = filePath;
        FileSize = fileSize;
    }

    public string FileName { get; }
    public string FilePath { get; }
    public long FileSize { get; }

    public string SizeText => FormatSize(FileSize);

    [ObservableProperty] private string _hash = string.Empty;

    private static string FormatSize(long bytes) => TCV.App.Converters.FileSizeConverter.Format(bytes);
}

public sealed class VerifyEntryViewModel
{
    private readonly VerifyResult _result;

    public VerifyEntryViewModel(VerifyResult result) => _result = result;

    public string FileName => _result.FileName;
    public string ExpectedHash => _result.ExpectedHash;
    public string ActualHash => _result.ActualHash;

    /// <summary>The engine's status token (<c>OK</c>, <c>FAILED</c>, <c>Missing</c>, <c>Error</c>); the view colours by it.</summary>
    public string Status => _result.Status;

    /// <summary>The status in the user's language.</summary>
    public string StatusText => LocalizationManager.Get("CS_Status_" + _result.Status);
}
