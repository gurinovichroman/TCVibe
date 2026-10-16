using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Converters;
using TCV.App.FileSystem;
using TCV.App.Localization;

namespace TCV.App.ViewModels;

public partial class StatusBarViewModel : ObservableObject
{
    private readonly DriveManager _driveManager;

    public StatusBarViewModel(DriveManager driveManager)
    {
        _driveManager = driveManager;
    }

    [ObservableProperty]
    private int _selectedCount;

    /// <summary>
    /// Selected entries whose size is known — files, and folders measured with Space or «Folder sizes»:
    /// the ones <see cref="SelectedBytes"/> adds up.
    /// </summary>
    [ObservableProperty]
    private int _selectedSizedCount;

    [ObservableProperty]
    private long _selectedBytes;

    /// <summary>Entries in the listing, the ".." row not counted.</summary>
    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _folderCount;

    [ObservableProperty]
    private int _fileCount;

    [ObservableProperty]
    private long _totalBytes;

    [ObservableProperty]
    private long _freeSpace;

    /// <summary>Size of the volume <see cref="FreeSpace"/> was measured on.</summary>
    [ObservableProperty]
    private long _totalSpace;

    /// <summary>Root of the volume the panel is on: <c>C:\</c>, <c>\\server\share\</c>, or empty (archive, server).</summary>
    [ObservableProperty]
    private string _currentDrive = string.Empty;

    private bool _freeSpaceKnown;
    private int _freeSpaceRequest;

    private DriveSpace Space => new(FreeSpace, TotalSpace);

    /// <summary>True once the panel's volume has been measured: the drive strip shows the space then.</summary>
    public bool HasFreeSpace => _freeSpaceKnown && !string.IsNullOrEmpty(CurrentDrive) && TotalSpace > 0;

    /// <summary>«182 GB free of 512 GB» on the drive strip.</summary>
    public string FreeSpaceText => HasFreeSpace
        ? LocalizationManager.Format("Shell_DiskFree", FileSizeConverter.FormatCompact(FreeSpace), FileSizeConverter.FormatCompact(TotalSpace))
        : string.Empty;

    /// <summary>How full the volume is, 0…1, for the strip's bar.</summary>
    public double UsedFraction => HasFreeSpace ? Space.UsedFraction : 0;

    /// <summary>Less than a tenth left: the bar and the text turn red.</summary>
    public bool IsLowSpace => HasFreeSpace && Space.IsLow;

    /// <summary>The latest free-space measurement, for callers that need the value it produces.</summary>
    public Task FreeSpaceRefreshed { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// "Selected 3 of 40 (1.2 MB)" while something is marked; otherwise what the folder holds
    /// ("5 folders, 35 files (20 MB)"), so the line is never blank on a populated listing.
    /// A folder adds its size once measured; a selection of folders none of which was shows no size at all.
    /// </summary>
    public string SelectedInfo
    {
        get
        {
            if (SelectedCount > 0)
            {
                return SelectedSizedCount > 0
                    ? string.Format(LocalizationManager.Get("Status_Selected"),
                        SelectedCount, TotalCount, FileSizeConverter.Format(SelectedBytes))
                    : string.Format(LocalizationManager.Get("Status_SelectedNoSize"), SelectedCount, TotalCount);
            }

            // «1 folder, 2 files» needs the plural forms, which only LocalizationManager.Format reads.
            return TotalCount > 0
                ? LocalizationManager.Format("Status_Summary",
                    FolderCount, FileCount, FileSizeConverter.Format(TotalBytes))
                : string.Empty;
        }
    }

    /// <summary>The exact numbers, for the tooltip: «182 GB free of 512 GB · 195 421 331 456 bytes free · 64% used».</summary>
    public string FreeSpaceInfo => HasFreeSpace
        ? LocalizationManager.Format(
            "Drive_SpaceTip",
            FileSizeConverter.FormatCompact(FreeSpace),
            FileSizeConverter.FormatCompact(TotalSpace),
            FreeSpace.ToString("N0", System.Globalization.CultureInfo.CurrentCulture),
            (int)Math.Round(Space.UsedFraction * 100))
        : string.Empty;

    /// <summary>
    /// Recounts the selection and the listing. Free space is measured again when the drive changed
    /// or <paramref name="refreshFreeSpace"/> asks for it (a directory load — after a copy or delete
    /// the number has moved even though the drive has not); selection changes alone never re-measure.
    /// </summary>
    public void Update(
        IEnumerable<FileEntryViewModel> selected,
        IEnumerable<FileEntryViewModel> listing,
        string path,
        bool refreshFreeSpace = false,
        bool recountListing = true)
    {
        var sel = selected.Where(f => !f.IsParentEntry).ToList();
        SelectedCount = sel.Count;
        SelectedSizedCount = sel.Count(f => !f.IsDirectory || f.FolderSize is not null);
        SelectedBytes = sel.Sum(f => f.KnownSize);

        // A selection change leaves the listing as it was: recounting it per marked row made
        // select-all quadratic on a big folder.
        if (recountListing)
        {
            var total = 0;
            var folders = 0;
            long bytes = 0;
            foreach (var f in listing)
            {
                if (f.IsParentEntry) continue;
                total++;
                if (f.IsDirectory) folders++;
                else bytes += f.Size;
            }

            TotalCount = total;
            FolderCount = folders;
            FileCount = total - folders;
            TotalBytes = bytes;
        }

        // Archive-internal and server paths ("/docs") have no volume.
        var drive = DriveSpace.RootOf(path);
        if (!string.Equals(drive, CurrentDrive, StringComparison.OrdinalIgnoreCase) || refreshFreeSpace)
        {
            if (!string.Equals(drive, CurrentDrive, StringComparison.OrdinalIgnoreCase))
            {
                _freeSpaceKnown = false;   // the old drive's number must not show under the new one
            }
            CurrentDrive = drive;
            FreeSpaceRefreshed = RefreshFreeSpaceAsync(drive);
            NotifySpaceChanged();
        }

        OnPropertyChanged(nameof(SelectedInfo));
    }

    private void NotifySpaceChanged()
    {
        OnPropertyChanged(nameof(HasFreeSpace));
        OnPropertyChanged(nameof(FreeSpaceText));
        OnPropertyChanged(nameof(UsedFraction));
        OnPropertyChanged(nameof(IsLowSpace));
        OnPropertyChanged(nameof(FreeSpaceInfo));
    }

    /// <summary>
    /// Reads the drive's free space on the thread pool: a sleeping network drive blocks the query
    /// for seconds (INV-06). An older answer arriving after a newer request is dropped.
    /// </summary>
    private async Task RefreshFreeSpaceAsync(string drive)
    {
        var request = ++_freeSpaceRequest;
        if (drive.Length == 0)
        {
            FreeSpace = 0;
            TotalSpace = 0;
            _freeSpaceKnown = false;
            NotifySpaceChanged();
            return;
        }

        var space = await Task.Run(() => _driveManager.GetSpace(drive));
        if (request != _freeSpaceRequest) return;

        // Free last: its change is the one the side bar's disk listens to.
        TotalSpace = space?.Total ?? 0;
        _freeSpaceKnown = space is not null;
        FreeSpace = space?.Free ?? 0;
        NotifySpaceChanged();
    }
}
