using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TCV.App.FileSystem;
using TCV.App.Services;
using TCV.App.ViewModels;

namespace TCV.App.Controls;

/// <summary>
/// The pictures of the Thumbnails view. The pictures, which cost a file read and a decode each, are
/// made only for the tiles in view plus a row on each side, after scrolling settles. A tile scrolled
/// away cancels its pending picture and drops the one it has (<see cref="ThumbnailService"/> still
/// remembers it), and leaving the view or the folder drops them all. Never tie a file read to a tile's
/// container: the panel makes and drops containers as fast as the scroll bar is dragged (TRAP-61).
/// </summary>
public partial class FileListControl
{
    /// <summary>Pictures asked for, per tile: the ones in view, loaded or still on their way.</summary>
    private readonly Dictionary<FileEntryViewModel, CancellationTokenSource> _thumbnailLoads = new();

    /// <summary>Waits for scrolling to settle: a drag of the scroll bar passes hundreds of tiles a second.</summary>
    private DispatcherTimer? _thumbnailTimer;

    private VirtualizingWrapPanel? _thumbnailPanel;

    /// <summary>Asks again, shortly, which pictures the tiles in view need.</summary>
    private void ScheduleThumbnailRefresh()
    {
        if (ThumbnailList.ItemsSource is null)
        {
            return;
        }

        _thumbnailTimer ??= CreateThumbnailTimer();
        _thumbnailTimer.Stop();
        _thumbnailTimer.Start();
    }

    private DispatcherTimer CreateThumbnailTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(80),
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshThumbnails();
        };
        return timer;
    }

    private void RefreshThumbnails()
    {
        var vm = ViewModel;
        if (vm is null || !ReferenceEquals(ThumbnailList.ItemsSource, vm.Files) || vm.Files.Count == 0)
        {
            ReleaseThumbnails();
            return;
        }

        // Not laid out yet: the layout's own ScrollChanged comes back here.
        if (MeasureThumbnailRows() is not { } rows)
        {
            return;
        }

        var (first, last) = WrapGrid.VisibleRange(
            rows.FirstRowTop, rows.ViewportHeight, rows.RowHeight, rows.Columns, vm.Files.Count, marginRows: 1);

        var wanted = new List<FileEntryViewModel>();
        for (var i = first; i <= last; i++)
        {
            var entry = vm.Files[i];
            if (!entry.IsParentEntry
                && ThumbnailPolicy.CanPreview(entry.Extension, entry.IsDirectory, entry.Size, entry.Attributes, onLocalDisk: vm.EntriesOnDisk))
            {
                wanted.Add(entry);
            }
        }

        var keep = wanted.ToHashSet();
        foreach (var (entry, cts) in _thumbnailLoads.Where(kv => !keep.Contains(kv.Key)).ToList())
        {
            cts.Cancel();
            _thumbnailLoads.Remove(entry);
            entry.Thumbnail = null;
        }

        var pixels = ThumbnailPolicy.TargetPixels(ThumbnailPolicy.BoxSize, VisualTreeHelper.GetDpi(this).DpiScaleX);
        foreach (var entry in wanted)
        {
            if (_thumbnailLoads.ContainsKey(entry))
            {
                continue;
            }

            var cts = new CancellationTokenSource();
            _thumbnailLoads[entry] = cts;
            var key = new ThumbnailKey(WindowsFileSystem.ToWindowsPath(entry.Entry.Path), entry.Size, entry.LastModified, pixels);
            _ = LoadThumbnailAsync(entry, key, cts.Token);
        }
    }

    private static async Task LoadThumbnailAsync(FileEntryViewModel entry, ThumbnailKey key, CancellationToken ct)
    {
        try
        {
            var picture = ThumbnailService.TryGetCached(key, out var cached)
                ? cached
                : await ThumbnailService.GetAsync(key, ct);
            if (!ct.IsCancellationRequested)
            {
                entry.Thumbnail = picture;
            }
        }
        catch (OperationCanceledException)
        {
            // Scrolled away, or the view or the folder was left.
        }
    }

    /// <summary>Cancels every picture on its way and drops the ones shown.</summary>
    private void ReleaseThumbnails()
    {
        _thumbnailTimer?.Stop();
        foreach (var (entry, cts) in _thumbnailLoads)
        {
            cts.Cancel();
            entry.Thumbnail = null;
        }

        _thumbnailLoads.Clear();
    }

    /// <summary>How the tiles are laid out now.</summary>
    /// <param name="FirstRowTop">Top of the first row relative to the viewport, negative once scrolled.</param>
    private readonly record struct ThumbnailRows(int Columns, double RowHeight, double FirstRowTop, double ViewportHeight);

    /// <summary>
    /// Tiles per row, row height and scroll position as the panel laid the tiles out (the slot is what
    /// a tile asks for, so the arithmetic follows the template and the screen's scaling). Null before
    /// the first layout or for an empty list.
    /// </summary>
    private ThumbnailRows? MeasureThumbnailRows()
    {
        _thumbnailPanel ??= FindDescendant<VirtualizingWrapPanel>(ThumbnailList);
        return _thumbnailPanel is { Layout: { Count: > 0, LineSize: > 0 } layout } panel
            ? new ThumbnailRows(layout.PerLine, layout.LineSize, -panel.OffsetAlong, layout.ViewportAlong)
            : null;
    }
}
