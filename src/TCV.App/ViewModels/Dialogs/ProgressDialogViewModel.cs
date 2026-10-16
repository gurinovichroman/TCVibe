using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Operations;
using TCV.Core.Domain;

namespace TCV.App.ViewModels.Dialogs;

public partial class ProgressDialogViewModel : ObservableObject, IProgress<FileProgress>, IProgress<ItemProgress>
{
    public ProgressDialogViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    /// <summary>Shared pause handle — assign to the operation before starting it.</summary>
    public PauseHandle PauseHandle { get; } = new();

    [ObservableProperty]
    private string _currentFile = string.Empty;

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private long _bytesCopied;

    [ObservableProperty]
    private long _totalBytes;

    /// <summary>
    /// "12.3 MB / 1.2 GB" — the raw byte counts above are unreadable past a few megabytes. An operation
    /// that counts items instead (a delete) puts "120 / 5,000" here.
    /// </summary>
    [ObservableProperty]
    private string _bytesText = string.Empty;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _elapsedText = string.Empty;

    [ObservableProperty]
    private string _remainingText = string.Empty;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPause))]
    private bool _isCompleted;

    /// <summary>
    /// False for work that has no loop to pause in (packing, extracting through a library, rewriting
    /// an archive): the button turned into "Resume" while the work went on, which promised a pause
    /// that never happened.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPause))]
    private bool _canPause = true;

    /// <summary>The Pause button is shown while the work runs and only when it can pause.</summary>
    public bool ShowPause => CanPause && !IsCompleted;

    public CancellationTokenSource Cts { get; } = new();

    public event EventHandler? CloseRequested;

    void IProgress<FileProgress>.Report(FileProgress value)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CurrentFile = value.CurrentFile;
            BytesCopied = value.BytesCopied;
            TotalBytes = value.TotalBytes;
            Percentage = value.Percentage;
            BytesText = $"{FormatSize(value.BytesCopied)} / {FormatSize(value.TotalBytes)}";

            double speed = 0;
            if (value.Elapsed.TotalSeconds > 0 && value.BytesCopied > 0)
            {
                speed = value.BytesCopied / value.Elapsed.TotalSeconds;
                SpeedText = FormatSpeed(speed);
            }

            ElapsedText = FormatTime(value.Elapsed);

            // Remaining time estimate
            if (speed > 0 && value.TotalBytes > value.BytesCopied)
            {
                var remaining = TimeSpan.FromSeconds((value.TotalBytes - value.BytesCopied) / speed);
                RemainingText = FormatTime(remaining);
            }
            else
            {
                RemainingText = string.Empty;
            }
        });
    }

    void IProgress<ItemProgress>.Report(ItemProgress value)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            CurrentFile = value.CurrentItem;
            Percentage = value.Percentage;
            BytesText = value.CountText;
            SpeedText = string.Empty;
            ElapsedText = FormatTime(value.Elapsed);
            RemainingText = value.Remaining is { } left ? FormatTime(left) : string.Empty;
        });
    }

    public void Complete()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsCompleted = true;
            Percentage = 100;
            RemainingText = string.Empty;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>Closes the dialog without claiming the operation finished (failure/cancellation).</summary>
    public void Dismiss()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
            CloseRequested?.Invoke(this, EventArgs.Empty));
    }

    [RelayCommand]
    private void PauseResume()
    {
        if (IsPaused)
        {
            IsPaused = false;
            PauseHandle.Resume();
        }
        else
        {
            IsPaused = true;
            PauseHandle.Pause();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        // Ensure resume so the operation can see cancellation
        if (IsPaused)
        {
            IsPaused = false;
            PauseHandle.Resume();
        }
        Cts.Cancel();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The window was closed by any route (X, Alt+F4). The operation would otherwise carry on with no
    /// visible progress — or, if paused, stay paused and hold up the whole queue.
    /// </summary>
    public void AbortOnClose()
    {
        if (IsPaused)
        {
            IsPaused = false;
            PauseHandle.Resume();
        }
        Cts.Cancel();
    }

    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    internal static string FormatSize(long bytes) => TCV.App.Converters.FileSizeConverter.Format(bytes);

    private static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1024 => $"{bytesPerSecond:N0} B/s",
        < 1024 * 1024 => $"{bytesPerSecond / 1024:N0} KB/s",
        _ => $"{bytesPerSecond / (1024 * 1024):N1} MB/s",
    };

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss")
            : t.ToString(@"m\:ss");
}
