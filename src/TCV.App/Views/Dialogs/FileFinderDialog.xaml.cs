using System.Windows.Input;
using TCV.App.Navigation;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// The fuzzy file picker (Ctrl+P). Owns the background index walk — the view model only ranks what
/// it is handed — and reports the chosen entry through
/// <see cref="FileFinderViewModel.SelectedItem"/> when <c>ShowDialog()</c> returns true.
/// </summary>
/// <remarks>
/// Enter opens · Tab narrows to the highlighted directory · Backspace on an empty query steps back
/// out · Esc closes. The walk runs on a worker thread and reports batches through
/// <see cref="Progress{T}"/>, which marshals them back to the UI thread; it is cancelled when the
/// dialog closes, so closing during a long index costs nothing.
/// </remarks>
public partial class FileFinderDialog : TcvDialog
{
    /// <summary>Entries per UI update — large enough to keep re-ranking cheap on a big tree.</summary>
    private const int BatchSize = 500;

    private readonly FileFinderViewModel _vm;
    private readonly IVirtualFileSystem _vfs;
    private readonly VfsPath _root;
    private readonly FileIndexOptions _options;
    private readonly CancellationTokenSource _cts = new();

    public FileFinderDialog(
        FileFinderViewModel viewModel, IVirtualFileSystem vfs, VfsPath root, FileIndexOptions options)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        _vfs = vfs;
        _root = root;
        _options = options;

        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            _ = IndexAsync();
        };
        Closed += (_, _) => _cts.Cancel();
    }

    private async Task IndexAsync()
    {
        var walker = new FileIndexWalker(_vfs, _options);
        IProgress<IReadOnlyList<FileIndexEntry>> progress =
            new Progress<IReadOnlyList<FileIndexEntry>>(_vm.Append);
        var cancelled = false;

        try
        {
            // ConfigureAwait(false) keeps the walk off the UI thread between directories (INV-06);
            // Progress<T> captured this window's context, so Append still lands on it.
            await Task.Run(async () =>
            {
                var batch = new List<FileIndexEntry>(BatchSize);
                var reported = 0;
                await foreach (var entry in walker.EnumerateAsync(_root, _cts.Token).ConfigureAwait(false))
                {
                    batch.Add(entry);

                    // Batches grow with the index: re-ranking costs O(n log n) per update, so a
                    // fixed batch size would turn a 100 000-file tree into quadratic work. This
                    // keeps the whole index pass at a small multiple of one full ranking.
                    if (batch.Count < Math.Max(BatchSize, reported / 4)) continue;

                    reported += batch.Count;
                    progress.Report(batch);
                    batch = new List<FileIndexEntry>(BatchSize);
                }

                if (batch.Count > 0)
                    progress.Report(batch);
            }, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        // The last batches may still be queued behind this continuation; the status line is the
        // only thing at stake, and Append refreshes it as they arrive.
        if (!cancelled && IsLoaded)
            _vm.FinishIndexing(walker.WasTruncated);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                _vm.MoveSelection(1);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.Up:
                _vm.MoveSelection(-1);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.PageDown:
                _vm.MoveSelection(10);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.PageUp:
                _vm.MoveSelection(-10);
                ScrollToSelection();
                e.Handled = true;
                break;
            case Key.Tab:
                // Narrow into the highlighted directory instead of moving focus out of the box.
                if (_vm.DrillDown()) ScrollToSelection();
                e.Handled = true;
                break;
            case Key.Back:
                // Only when there is no text left to delete — otherwise this is ordinary editing.
                if (_vm.Query.Length == 0 && _vm.PopScope()) e.Handled = true;
                break;
            case Key.Enter:
                if (_vm.SelectedItem is not null) DialogResult = true;
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
        }
    }

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_vm.SelectedItem is not null) DialogResult = true;
    }

    private void ScrollToSelection()
    {
        if (_vm.SelectedItem is not null)
            ResultsList.ScrollIntoView(_vm.SelectedItem);
    }
}
