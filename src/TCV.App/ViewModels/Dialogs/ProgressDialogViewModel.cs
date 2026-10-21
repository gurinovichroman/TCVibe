using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.App.Operations;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.ViewModels.Dialogs;

public partial class ProgressDialogViewModel : ObservableObject, IProgress<FileProgress>, IProgress<ItemProgress>
{
    private IFileOperation? _operation;

    public ProgressDialogViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }

    /// <summary>
    /// Ties the window to the operation it reports on: Cancel stops it, and the window and the list of
    /// background operations follow whether it still waits in the queue (<see cref="IsWaiting"/>).
    /// </summary>
    public void Attach(FileOperationBase operation)
    {
        _operation = operation;
        operation.LinkCancellation(Cts.Token);
        WaitingText = LocalizationManager.Get(operation.Lane == OperationLane.Network
            ? "Dlg_Progress_WaitingServer"
            : "Dlg_Progress_WaitingLocal");
        operation.StateChanged += (_, _) => OnUiThread(RefreshWaiting);
        RefreshWaiting();
    }

    /// <summary>
    /// Its operation has not started yet: another one runs ahead of it in the same lane (ADR-0038). The
    /// window says why nothing moves (<see cref="WaitingText"/>), the list of background operations
    /// says «queued»; a bare «0%» looked like a hang.
    /// </summary>
    [ObservableProperty]
    private bool _isWaiting;

    /// <summary>What it waits for: the server lane or the disk lane, each one operation at a time.</summary>
    [ObservableProperty]
    private string _waitingText = string.Empty;

    private void RefreshWaiting() => IsWaiting = _operation is { State: OperationState.Pending };

    /// <summary>Runs inline without a dispatcher (tests) or when already on it.</summary>
    private static void OnUiThread(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.InvokeAsync(action);
        }
    }

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

    /// <summary>
    /// The window is hidden and the work goes on: the status bar counts it, and a click there brings the
    /// window back (ADR-0038). Hiding is not closing — closing the window still cancels the work.
    /// </summary>
    [ObservableProperty]
    private bool _isInBackground;

    [RelayCommand]
    private void SendToBackground() => IsInBackground = true;

    /// <summary>Shows the window again (the status bar's list, the BackgroundOperations command).</summary>
    [RelayCommand]
    public void BringBack() => IsInBackground = false;

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
