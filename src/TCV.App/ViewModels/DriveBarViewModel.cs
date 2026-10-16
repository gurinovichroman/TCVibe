using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Collections;
using TCV.App.FileSystem;

namespace TCV.App.ViewModels;

public partial class DriveBarViewModel : ObservableObject
{
    private readonly DriveManager _driveManager;

    /// <summary>Last path the panel reported, re-applied once the drive list arrives.</summary>
    private string? _lastPath;

    private bool _refreshing;
    private bool _refreshAgain;

    public DriveBarViewModel(DriveManager driveManager)
    {
        _driveManager = driveManager;
        // Drives come and go while the program runs (a USB stick, a mapped network drive): the list
        // was read once at startup and then went stale.
        _driveManager.DrivesChanged += (_, _) =>
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() => _ = Refresh());
        _ = Refresh();
    }

    public ObservableCollection<DriveEntry> Drives { get; } = new();

    [ObservableProperty]
    private DriveEntry? _selectedDrive;

    /// <summary>
    /// Where the panel is when that is on no listed drive — <c>\\server\share</c>, or a drive that is
    /// not ready — shown in place of a selection. Empty while a drive is selected.
    /// </summary>
    [ObservableProperty]
    private string _locationText = string.Empty;

    public event EventHandler<string>? DriveSelected;

    /// <summary>Raised after a refresh changed the list, so the folder trees can bring their drive roots along.</summary>
    public event EventHandler? DrivesRefreshed;

    partial void OnSelectedDriveChanged(DriveEntry? value)
    {
        if (value is not null)
        {
            DriveSelected?.Invoke(this, value.Name);
        }
    }

    /// <summary>
    /// Re-reads the drive list on the thread pool: <c>DriveInfo.IsReady</c> and the volume label
    /// block for seconds on a disconnected network or optical drive, and this runs while the
    /// window is being built (INV-06). The panel's drive is selected again once the list is in.
    /// A request that arrives while a read is running is folded into one more read after it.
    /// </summary>
    [RelayCommand]
    private async Task Refresh()
    {
        if (_refreshing)
        {
            _refreshAgain = true;
            return;
        }

        _refreshing = true;
        try
        {
            do
            {
                _refreshAgain = false;
                await ReadDrivesAsync();
            }
            while (_refreshAgain);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task ReadDrivesAsync()
    {
        IReadOnlyList<DriveEntry> drives;
        try
        {
            drives = await Task.Run(_driveManager.GetDrives);
        }
        catch (Exception)
        {
            return;   // no drive list is better than a crash at startup
        }

        // Merged in place, not cleared and refilled: emptying the list under the combo's two-way
        // binding would write "no drive" back and flash the selector on every refresh.
        var changed = ListReconciler.Reconcile(Drives, Merge(Drives, drives));

        if (_lastPath is not null)
        {
            SelectDriveForPath(_lastPath);
        }

        if (changed)
        {
            DrivesRefreshed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// The fresh drive list, reusing the entries already shown for drives that look the same (name
    /// and label): the selector keeps its selected object, and only a drive that came, went or was
    /// relabelled changes. Free space is not compared — it moves all the time — but a kept entry takes
    /// the fresh numbers (<see cref="DriveEntry.UpdateSpace"/>), because the drive button's tooltip shows them.
    /// </summary>
    public static IReadOnlyList<DriveEntry> Merge(IEnumerable<DriveEntry> current, IReadOnlyList<DriveEntry> fresh)
    {
        var shown = new Dictionary<string, DriveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in current)
        {
            shown.TryAdd(entry.Name, entry);
        }

        return fresh
            .Select(d =>
            {
                if (!shown.TryGetValue(d.Name, out var kept) || kept.DisplayName != d.DisplayName)
                {
                    return d;
                }

                kept.UpdateSpace(d);
                return kept;
            })
            .ToList();
    }

    public void SelectDriveForPath(string path)
    {
        _lastPath = path;
        var (index, location) = DriveSelection.Resolve(path, Drives.Select(d => d.Name).ToList());

        // A UNC share or a drive missing from the list selects nothing: the previous drive used to
        // stay selected, saying the panel was on a drive it had left.
        SelectedDrive = index >= 0 ? Drives[index] : null;
        LocationText = location;
    }
}
