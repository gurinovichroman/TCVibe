using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Sync;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One row of the comparison grid, with the direction the user chose for it.</summary>
public sealed partial class ComparisonRowViewModel : ObservableObject
{
    private readonly TimeSpan _tolerance;
    private readonly bool _leftTakesCopies;
    private readonly bool _rightTakesCopies;

    /// <param name="row">The verdict.</param>
    /// <param name="tolerance">When two times count as equal.</param>
    /// <param name="leftTakesCopies">False when nothing can be copied to the left side: an archive that takes no change, a read-only connection.</param>
    /// <param name="rightTakesCopies">The same for the right side.</param>
    public ComparisonRowViewModel(ComparisonRow row, TimeSpan? tolerance = null, bool leftTakesCopies = true, bool rightTakesCopies = true)
    {
        Row = row;
        _tolerance = tolerance ?? new ComparisonOptions().TimeTolerance;
        _leftTakesCopies = leftTakesCopies;
        _rightTakesCopies = rightTakesCopies;
        _direction = DefaultDirection;
        _isChecked = _direction != SyncDirection.None;
    }

    /// <summary>What the verdict suggests, unless the side it points at takes no copy.</summary>
    public SyncDirection DefaultDirection => CanPoint(Row.DefaultDirection) ? Row.DefaultDirection : SyncDirection.None;

    /// <summary>
    /// Whether the row can be pointed that way: a source there, and a side that takes it. An arrow that
    /// could only fail is not offered (TRAP-58).
    /// </summary>
    public bool CanPoint(SyncDirection direction) => Row.CanCopy(direction) && direction switch
    {
        SyncDirection.ToRight => _rightTakesCopies,
        SyncDirection.ToLeft => _leftTakesCopies,
        _ => true,
    };

    public ComparisonRow Row { get; }

    public string RelativePath => Row.RelativePath;
    public string Name => Row.Name;
    public string Directory => Row.RelativeDirectory;
    public bool IsDirectory => Row.IsDirectory;
    public ComparisonStatus Status => Row.Status;

    public string LeftSize => Describe(Row.Left);
    public string RightSize => Describe(Row.Right);

    /// <summary>Modification times, so "newer" and the ⚠ can be checked rather than taken on trust.</summary>
    public string LeftModified => DescribeTime(Row.Left);
    public string RightModified => DescribeTime(Row.Right);

    /// <summary>Localised verdict, shown in its own column.</summary>
    public string StatusText => Row.CaseVariant
        ? LocalizationManager.Get("Cmp_CaseVariant")
        : LocalizationManager.Get(StatusKey(Row));

    /// <summary>
    /// The dictionary key of a verdict. "Different" covers three cases the user acts on differently:
    /// a file against a folder, equal sizes with different bytes (content comparison), and different sizes.
    /// </summary>
    internal static string StatusKey(ComparisonRow row) => row.Status switch
    {
        ComparisonStatus.OnlyLeft => "Cmp_OnlyLeft",
        ComparisonStatus.OnlyRight => "Cmp_OnlyRight",
        ComparisonStatus.LeftNewer => "Cmp_LeftNewer",
        ComparisonStatus.RightNewer => "Cmp_RightNewer",
        ComparisonStatus.Different when row.Left?.IsDirectory != row.Right?.IsDirectory => "Cmp_KindDiffers",
        ComparisonStatus.Different when row.Left?.Size == row.Right?.Size => "Cmp_ContentDiffers",
        ComparisonStatus.Different => "Cmp_Different",
        ComparisonStatus.Unreadable => "Cmp_Unreadable",
        _ => "Cmp_Same",
    };

    /// <summary>Arrow shown between the two sides, reflecting <see cref="Direction"/>.</summary>
    public string DirectionText => Direction switch
    {
        SyncDirection.ToRight => "→",
        SyncDirection.ToLeft => "←",
        _ => "=",
    };

    /// <summary>
    /// True when the chosen direction would replace a <b>newer</b> file with an older one — the arrow
    /// points the wrong way round. Drawn as a warning next to the arrow.
    /// </summary>
    public bool OverwritesNewer
    {
        get
        {
            if (!IsChecked || IsDirectory)
            {
                return false;
            }

            var source = Direction == SyncDirection.ToRight ? Row.Left : Row.Right;
            var target = Direction == SyncDirection.ToRight ? Row.Right : Row.Left;
            return source is not null && target is not null
                && DirectoryComparison.TimeDifference(target.LastModified, source.LastModified) > _tolerance;
        }
    }

    /// <summary>The warning glyph, or empty; a text so the grid needs no converter.</summary>
    public string WarningText => OverwritesNewer ? "⚠" : string.Empty;

    /// <summary>Whether a row can be ticked at all: an unreadable folder has nothing to copy.</summary>
    public bool CanSelect => Status != ComparisonStatus.Unreadable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OverwritesNewer), nameof(WarningText))]
    private bool _isChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionText), nameof(OverwritesNewer), nameof(WarningText))]
    private SyncDirection _direction;

    /// <summary>
    /// Steps this row's direction through what is possible for it (→, ←, =). The default from the
    /// verdict is a suggestion; the user can point any row either way, or leave it alone.
    /// </summary>
    [RelayCommand]
    private void CycleDirection()
    {
        var choices = new List<SyncDirection>(3);
        if (CanPoint(SyncDirection.ToRight))
        {
            choices.Add(SyncDirection.ToRight);
        }

        if (CanPoint(SyncDirection.ToLeft))
        {
            choices.Add(SyncDirection.ToLeft);
        }

        choices.Add(SyncDirection.None);

        var next = choices[(choices.IndexOf(Direction) + 1) % choices.Count];

        Direction = next;
        IsChecked = next != SyncDirection.None;
    }

    private static string Describe(IFileEntry? entry) =>
        entry is null ? string.Empty : entry.IsDirectory ? "<DIR>" : entry.Size.ToString("N0");

    private static string DescribeTime(IFileEntry? entry) =>
        entry is null || entry.IsDirectory || entry.LastModified == DateTime.MinValue
            ? string.Empty
            : (entry.LastModified.Kind == DateTimeKind.Utc ? entry.LastModified.ToLocalTime() : entry.LastModified)
                .ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Backs the directory comparison window (Ctrl+K Ctrl+S, task 6.23): runs the comparison, shows
/// what differs, and hands back the rows the user wants copied. Performing the copies is
/// <c>MainViewModel</c>'s job, through the operation queue (INV-10).
/// </summary>
public sealed partial class CompareDirectoriesDialogViewModel : ObservableObject, IDisposable
{
    /// <summary>How long the UI thread takes rows in one go: the panels stay alive under a comparison with many (6.96).</summary>
    private static readonly TimeSpan RowSliceLength = TimeSpan.FromMilliseconds(30);

    private readonly IVirtualFileSystem _leftFs;
    private readonly IVirtualFileSystem _rightFs;
    private readonly string _leftRoot;
    private readonly string _rightRoot;
    private readonly bool _leftTakesCopies;
    private readonly bool _rightTakesCopies;
    private CancellationTokenSource? _cts;

    /// <summary>
    /// The two sides, fixed for the life of the window: it is not modal, and the panels may move on, close
    /// their tab or reconnect while it is open. Its caller holds the leases on these file systems.
    /// </summary>
    public CompareDirectoriesDialogViewModel(SyncEndpoint left, SyncEndpoint right)
    {
        Left = left;
        Right = right;
        _leftFs = left.Fs;
        _rightFs = right.Fs;
        _leftRoot = left.Root;
        _rightRoot = right.Root;
        _leftTakesCopies = left.Fs.SupportsOperation(VfsOperation.Copy);
        _rightTakesCopies = right.Fs.SupportsOperation(VfsOperation.Copy);
        LeftPath = left.Root;
        RightPath = right.Root;
    }

    public SyncEndpoint Left { get; }

    public SyncEndpoint Right { get; }

    public string LeftPath { get; }
    public string RightPath { get; }

    public ObservableCollection<ComparisonRowViewModel> Rows { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynchronizeCommand))]
    private bool _isComparing;

    [ObservableProperty] private string _statusText = string.Empty;

    /// <summary>True when <see cref="StatusText"/> reports a failure, drawn in the danger colour.</summary>
    [ObservableProperty] private bool _hasError;

    [ObservableProperty] private bool _recursive = true;
    [ObservableProperty] private bool _showSame;

    /// <summary>
    /// Compare equal-size files by content, not only by timestamp — the honest way to compare two
    /// worktrees, whose files all carry their checkout times.
    /// </summary>
    [ObservableProperty] private bool _compareContent;

    /// <summary>True once the user pressed "synchronise"; read by the caller.</summary>
    public bool Confirmed { get; private set; }

    /// <summary>The tolerance used to call two timestamps equal — wider when a server is involved.</summary>
    public TimeSpan TimeTolerance =>
        IsRemote(_leftFs) || IsRemote(_rightFs) ? ComparisonOptions.RemoteTimeTolerance : new ComparisonOptions().TimeTolerance;

    /// <summary>The ticked rows, with the direction each should be copied in.</summary>
    public IReadOnlyList<SyncPlanItem> Plan =>
        Rows.Where(r => r.IsChecked && r.Direction != SyncDirection.None && r.CanSelect)
            .Select(r => new SyncPlanItem(r.Row, r.Direction))
            .ToList();

    public event EventHandler? CloseRequested;

    private static bool IsRemote(IVirtualFileSystem fs) => !string.Equals(fs.Scheme, "file", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task Compare()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        Rows.Clear();
        IsComparing = true;
        HasError = false;
        StatusText = LocalizationManager.Get("Cmp_Running");

        var options = new ComparisonOptions
        {
            Recursive = Recursive,
            IncludeSame = ShowSame,
            CompareContent = CompareContent,
            TimeTolerance = TimeTolerance,
        };
        var comparer = new DirectoryComparer(_leftFs, _rightFs, options)
        {
            // Created here, on the UI thread, so its callbacks come back to it.
            Progress = new Progress<ComparisonProgress>(p =>
            {
                if (IsComparing && !ct.IsCancellationRequested)
                {
                    StatusText = string.Format(LocalizationManager.Get("Cmp_Progress"), Rows.Count, p.Folders, p.FilesCompared);
                }
            }),
        };

        try
        {
            // The rows of a big tree come faster than they can be shown, and an await that completes at
            // once never gives the thread back: the whole program, not just this window, would stand.
            var slice = System.Diagnostics.Stopwatch.StartNew();
            await foreach (var row in comparer.CompareAsync(new VfsPath(_leftRoot), new VfsPath(_rightRoot), ct))
            {
                Rows.Add(new ComparisonRowViewModel(row, options.TimeTolerance, _leftTakesCopies, _rightTakesCopies));
                if (slice.Elapsed >= RowSliceLength)
                {
                    await Task.Delay(1, ct);
                    slice.Restart();
                }
            }

            StatusText = Summarize(comparer.UnreadableFolders);
        }
        catch (OperationCanceledException)
        {
            StatusText = string.Format(LocalizationManager.Get("Cmp_Stopped"), Rows.Count);
        }
        catch (ComparisonFailedException ex)
        {
            // A side that cannot be listed at all: nothing is compared, and nothing is offered for copying.
            Rows.Clear();
            HasError = true;
            // Side is the comparer's own "left"/"right" token and the missing-folder reason is its own
            // English text; both are shown in the UI language.
            var side = LocalizationManager.Get(ex.Side == "left" ? "Cmp_SideLeft" : "Cmp_SideRight");
            var reason = ex.InnerException is System.IO.DirectoryNotFoundException
                ? LocalizationManager.Get("Cmp_FolderMissing")
                : ex.InnerException?.Message;
            StatusText = string.Format(LocalizationManager.Get("Cmp_ListingFailed"), side, ex.Path, reason);
        }
        catch (Exception ex)
        {
            Rows.Clear();
            HasError = true;
            StatusText = string.Format(LocalizationManager.Get("Cmp_Failed"), ex.Message);
        }
        finally
        {
            IsComparing = false;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    /// <summary>Ticks every row that can be copied left to right, unticking the rest.</summary>
    [RelayCommand]
    private void SelectToRight() => SetAll(SyncDirection.ToRight);

    /// <summary>Ticks every row that can be copied right to left.</summary>
    [RelayCommand]
    private void SelectToLeft() => SetAll(SyncDirection.ToLeft);

    /// <summary>Restores the per-row default direction (newer wins, missing gets copied).</summary>
    [RelayCommand]
    private void SelectDefaults()
    {
        foreach (var row in Rows)
        {
            row.Direction = row.DefaultDirection;
            row.IsChecked = row.Direction != SyncDirection.None;
        }
    }

    /// <summary>Available once the comparison has finished — a plan is not final while rows are still arriving.</summary>
    [RelayCommand(CanExecute = nameof(CanSynchronize))]
    private async Task Synchronize()
    {
        // With nothing ticked the window used to close and nothing happened, which looked like
        // a synchronisation that had run. Say what is missing and stay open.
        var plan = Plan;
        if (plan.Count == 0)
        {
            StatusText = LocalizationManager.Get("Cmp_NothingTicked");
            return;
        }

        // The panels were free all this time: what is about to be replaced may not be what was compared.
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsVerifying = true;
        HasError = false;
        StatusText = LocalizationManager.Get("Cmp_Verifying");
        try
        {
            var changed = await Task.Run(() => SyncPlanFreshness.FindChangedAsync(plan, Left, Right, ct), ct);
            if (changed.Count > 0)
            {
                HasError = true;
                StatusText = LocalizationManager.Format("Cmp_ChangedSince", changed.Count, changed[0]);
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // Compare pressed meanwhile is what stopped this, and the status is its own by now.
            if (!IsComparing)
            {
                StatusText = string.Format(LocalizationManager.Get("Cmp_Stopped"), Rows.Count);
            }

            return;
        }
        finally
        {
            IsVerifying = false;
        }

        Confirmed = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSynchronize() => !IsComparing && !IsVerifying;

    /// <summary>The target folders are being listed once more, just before the synchronisation.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SynchronizeCommand))]
    private bool _isVerifying;

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        Confirmed = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops a comparison still running when the window is closed by any route.</summary>
    public void Dispose()
    {
        _cts?.Cancel();
    }

    private void SetAll(SyncDirection direction)
    {
        foreach (var row in Rows)
        {
            // A row can only be copied in a direction where a source actually exists, and never
            // when it is unreadable, differs only by case, or is a file against a folder.
            var possible = row.CanPoint(direction)
                           && (direction == SyncDirection.ToRight ? row.Row.Left is not null : row.Row.Right is not null)
                           && !row.Row.CaseVariant;
            row.Direction = possible ? direction : SyncDirection.None;
            row.IsChecked = possible && row.Status != ComparisonStatus.Same;
        }
    }

    private string Summarize(int unreadable)
    {
        var counts = DirectoryComparison.Summarize(Rows.Select(r => r.Row));
        var text = string.Format(
            LocalizationManager.Get("Cmp_Summary"),
            Rows.Count,
            counts.GetValueOrDefault(ComparisonStatus.OnlyLeft),
            counts.GetValueOrDefault(ComparisonStatus.OnlyRight),
            counts.GetValueOrDefault(ComparisonStatus.LeftNewer) +
            counts.GetValueOrDefault(ComparisonStatus.RightNewer) +
            counts.GetValueOrDefault(ComparisonStatus.Different));

        if (unreadable > 0)
        {
            text += string.Format(LocalizationManager.Get("Cmp_UnreadableFolders"), unreadable);
            HasError = true;
        }

        // Why the arrows only point one way.
        if (!_leftTakesCopies)
        {
            text += " · " + LocalizationManager.Get("Cmp_LeftTakesNoCopies");
        }

        if (!_rightTakesCopies)
        {
            text += " · " + LocalizationManager.Get("Cmp_RightTakesNoCopies");
        }

        return text;
    }
}
