using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Viewer;

/// <summary>
/// Opens the F3 viewer on a file of any size: reads only the first window (the whole file for an
/// image, which has to be decoded as a unit), shows progress with a Cancel button when that takes a
/// while, and hands the viewer a <see cref="IViewerSource"/> so it can move to the tail or any offset.
/// </summary>
/// <remarks>
/// The viewer used to slurp up to 32 MB before showing anything — no progress, no way to stop it, no
/// way to see past the first 4 MB of a log. Now a 20 GB file opens as fast as a 20 KB one.
/// </remarks>
public sealed class ViewerLauncher
{
    /// <summary>Largest image the viewer decodes: it needs the whole file in memory.</summary>
    public const long MaxImageBytes = 32L * 1024 * 1024;

    /// <summary>A first read bigger than this (or from a server) gets a progress window.</summary>
    private const int ProgressThresholdBytes = 1024 * 1024;

    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;

    public ViewerLauncher(IDialogService dialogs, ILogger logger)
    {
        _dialogs = dialogs;
        _logger = logger;
    }

    /// <summary>How many bytes the first read takes for a file of <paramref name="size"/>.</summary>
    public static int InitialReadBytes(string fileName, long size)
    {
        var wholeImage = ViewerContent.HasImageExtension(fileName) && size is > 0 and <= MaxImageBytes;
        return wholeImage ? (int)size : ViewModels.Dialogs.FileViewerDialogViewModel.WindowBytes;
    }

    /// <summary>
    /// Reads the first window of <paramref name="entry"/> and shows the viewer. <paramref name="lease"/>
    /// keeps a mounted file system alive for as long as the viewer window lives (TRAP-05); it is released
    /// when the window closes, or here if the viewer never opens. <paramref name="panelFiles"/> are the
    /// panel's rows as shown, for N/P (6.41); <paramref name="extractOnce"/> says the file system is an
    /// archive, whose entries are unpacked once rather than for every window (6.37).
    /// </summary>
    public async Task OpenAsync(
        IVirtualFileSystem vfs, IFileEntry entry, IDisposable? lease, TCV.Ai.Llm.LlmClient? llm,
        IEnumerable<IFileEntry>? panelFiles = null, bool extractOnce = false)
    {
        var size = entry.Size;
        var count = InitialReadBytes(entry.Name, size);

        // One lease for the window, whichever of the panel's files it shows: the sources hold none.
        var siblings = new ViewerSiblings(
            ViewerFileList.Create(panelFiles ?? [entry], entry), Opener(vfs, extractOnce), lease);
        var source = siblings.Open(entry);

        var remote = !string.Equals(vfs.Scheme, "file", StringComparison.OrdinalIgnoreCase);
        var progress = remote || count > ProgressThresholdBytes
            ? _dialogs.ShowProgressDialog(LocalizationManager.Get("Viewer_Opening"))
            : null;

        byte[] data;
        try
        {
            var clock = Stopwatch.StartNew();
            var reporter = progress is null
                ? null
                : new Progress<long>(done => ((IProgress<FileProgress>)progress).Report(
                    new FileProgress(done, Math.Max(count, done), entry.Name, clock.Elapsed)));

            data = await source.ReadAsync(0, count, reporter, progress?.Cts.Token ?? CancellationToken.None);
            progress?.Complete();
        }
        catch (OperationCanceledException)
        {
            progress?.Dismiss();
            source.Dispose();
            siblings.Dispose();
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read {Name} for the viewer", entry.Name);
            progress?.Dismiss();
            source.Dispose();
            siblings.Dispose();
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Viewer_OpenFailed"), ViewModels.MainViewModel.GetFileErrorMessage(ex));
            return;
        }

        // A size the listing did not know (0 for some servers) means "what we read is all there is".
        var fullSize = size > data.Length ? size : data.Length;
        _dialogs.ShowFileViewer(entry.Name, data, fullSize > data.Length, fullSize, llm, source, siblings);
    }

    /// <summary>
    /// How the viewer reads a file. An archive entry is unpacked once and every window is read from that
    /// copy (6.37); anything else is reopened for each window, so nothing stays open on a disk or a server
    /// between two reads.
    /// </summary>
    public static IViewerSource CreateSource(IVirtualFileSystem vfs, IFileEntry entry, bool extractOnce) =>
        extractOnce
            ? new ExtractedViewerSource(vfs, entry.Path, entry.Size)
            : new VfsViewerSource(vfs, entry.Path, entry.Size);

    /// <summary>
    /// The opener the viewer keeps for N/P, capturing only the file system and the mode. A lambda
    /// written inside <see cref="OpenAsync"/> would share that method's closure and keep its progress
    /// dialog (and whatever holds on to it) alive for as long as the viewer window is open.
    /// </summary>
    private static Func<IFileEntry, IViewerSource> Opener(IVirtualFileSystem vfs, bool extractOnce) =>
        file => CreateSource(vfs, file, extractOnce);
}
