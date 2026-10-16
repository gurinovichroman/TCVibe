using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace TCV.App.FileSystem;

public sealed class DriveEntry : INotifyPropertyChanged
{
    public DriveEntry(string name, string label, string driveFormat,
        long totalSize, long freeSpace, DriveType driveType)
    {
        Name = name;
        Label = label;
        DriveFormat = driveFormat;
        TotalSize = totalSize;
        FreeSpace = freeSpace;
        DriveType = driveType;
    }

    public string Name { get; }
    public string Label { get; }
    public string DriveFormat { get; }
    public long TotalSize { get; private set; }
    public long FreeSpace { get; private set; }
    public DriveType DriveType { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Takes the size and free space of a fresh read of the same drive. The drive list keeps its entries
    /// while the drive looks the same (<c>DriveBarViewModel.Merge</c>), so without this the tooltip would
    /// say how much room the drive had when the program started. UI thread: bindings are listening.
    /// </summary>
    public void UpdateSpace(DriveEntry fresh)
    {
        if (fresh.TotalSize == TotalSize && fresh.FreeSpace == FreeSpace)
        {
            return;
        }

        TotalSize = fresh.TotalSize;
        FreeSpace = fresh.FreeSpace;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalSize)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FreeSpace)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SpaceText)));
    }

    public string DisplayName =>
        string.IsNullOrEmpty(Label) ? Name : $"{Label} ({Name.TrimEnd('\\')})";

    /// <summary>«C:» — what the drive button says.</summary>
    public string Letter => Name.TrimEnd('\\', '/');

    /// <summary>A mapped network drive: drawn with the network icon.</summary>
    public bool IsNetwork => DriveType == DriveType.Network;

    /// <summary>«182 GB free of 512 GB» under the drive's name in its button's tooltip; the strip reads it on hover.</summary>
    public string SpaceText => TotalSize > 0
        ? Localization.LocalizationManager.Format(
            "Shell_DiskFree",
            Converters.FileSizeConverter.FormatCompact(FreeSpace),
            Converters.FileSizeConverter.FormatCompact(TotalSize))
        : string.Empty;
}

public sealed class DriveManager : IDisposable
{
    /// <summary>Several notifications arrive per device (one per volume, plus media); consumers hear one.</summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private Timer? _pollTimer;
    private Timer? _debounceTimer;
    private string _lastLetters = string.Empty;

    // A poll already in flight, or a WM_DEVICECHANGE during shutdown, must not bring the timers back.
    private bool _disposed;

    /// <summary>
    /// Raised, on a worker thread, when drives may have come or gone: a USB stick or card was put in
    /// or pulled out, a network drive was mapped or disconnected. Listeners re-read the drives off
    /// the UI thread — a sleeping network drive takes seconds to answer (INV-06).
    /// </summary>
    public event EventHandler? DrivesChanged;

    /// <summary>
    /// Starts watching the set of drive letters. Windows announces a volume arriving or leaving
    /// (<see cref="NotifyDevicesChanged"/>, from <c>WM_DEVICECHANGE</c>), but not a drive mapped by
    /// <c>net use</c> or <c>subst</c> in a console, so the letter mask is compared as well. Reading it
    /// is one call that touches no drive, so polling costs nothing measurable.
    /// </summary>
    public void StartMonitoring(TimeSpan pollInterval)
    {
        lock (_gate)
        {
            if (_disposed || _pollTimer is not null)
            {
                return;
            }

            _lastLetters = ReadLetters();
            _pollTimer = new Timer(_ => PollLetters(), null, pollInterval, pollInterval);
        }
    }

    /// <summary>A volume arrived or left. Debounced: a card reader reports each of its slots.</summary>
    public void NotifyDevicesChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer ??= new Timer(_ => DrivesChanged?.Invoke(this, EventArgs.Empty));
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// True for a <c>WM_DEVICECHANGE</c> that says a volume (a drive letter) arrived or was removed —
    /// not a mouse or a phone being plugged in. <paramref name="deviceType"/> is the
    /// <c>dbch_devicetype</c> of the message's header, or null when it carries none.
    /// </summary>
    public static bool IsVolumeEvent(int message, long eventCode, int? deviceType)
    {
        const int WmDeviceChange = 0x0219;
        const long DbtDeviceArrival = 0x8000;
        const long DbtDeviceRemoveComplete = 0x8004;
        const int DbtDevTypVolume = 2;

        return message == WmDeviceChange
            && eventCode is DbtDeviceArrival or DbtDeviceRemoveComplete
            && deviceType == DbtDevTypVolume;
    }

    private void PollLetters()
    {
        string letters;
        try
        {
            letters = ReadLetters();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(letters, _lastLetters, StringComparison.Ordinal);
            _lastLetters = letters;
        }

        if (changed)
        {
            NotifyDevicesChanged();
        }
    }

    private static string ReadLetters() => string.Join(",", Environment.GetLogicalDrives());

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pollTimer?.Dispose();
            _pollTimer = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    public IReadOnlyList<DriveEntry> GetDrives()
    {
        var result = new List<DriveEntry>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                result.Add(new DriveEntry(
                    drive.Name,
                    drive.VolumeLabel,
                    drive.DriveFormat,
                    drive.TotalSize,
                    drive.AvailableFreeSpace,
                    drive.DriveType));
            }
            catch
            {
                // skip inaccessible drives
            }
        }
        return result;
    }

    /// <summary>
    /// Free space available to the current user on one volume and its size, or null when they cannot
    /// be read. <paramref name="root"/> is a drive (<c>"C:\"</c>) or a share (<c>"\\server\share\"</c>,
    /// which <see cref="DriveInfo"/> refuses). Probes only that volume, unlike <see cref="GetDrives"/>,
    /// but still blocks on a sleeping network drive: call it off the UI thread (INV-06).
    /// </summary>
    public DriveSpace? GetSpace(string root)
    {
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        // A card reader with no card must answer "not ready", not put up "insert a disk" (as DriveInfo does).
        SetThreadErrorMode(SemFailCriticalErrors, out var previous);
        try
        {
            return GetDiskFreeSpaceEx(root, out var free, out var total, out _) && total > 0
                ? new DriveSpace((long)Math.Min(free, long.MaxValue), (long)Math.Min(total, long.MaxValue))
                : null;
        }
        finally
        {
            SetThreadErrorMode(previous, out _);
        }
    }

    private const uint SemFailCriticalErrors = 0x0001;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(
        string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetThreadErrorMode(uint newMode, out uint oldMode);
}
