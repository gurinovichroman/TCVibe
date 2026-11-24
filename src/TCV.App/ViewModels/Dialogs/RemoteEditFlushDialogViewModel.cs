using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Services;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// The window that waits for F4 edits to reach the server before the program closes (TRAP-105): what is
/// on its way, how many are left, and a way to stop waiting. Stopping keeps the edits in the local
/// copies, and the exit says where they are.
/// </summary>
public sealed partial class RemoteEditFlushDialogViewModel : ObservableObject, IProgress<RemoteEditFlushProgress>
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Set by the Stop button and by closing the window.</summary>
    public CancellationToken StopToken => _stop.Token;

    /// <summary>The file on its way.</summary>
    [ObservableProperty]
    private string _currentFile = string.Empty;

    /// <summary>"1 / 3" while several go up; empty for one.</summary>
    [ObservableProperty]
    private string _countText = string.Empty;

    /// <summary>The flush ended by itself or was stopped: the window goes.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Shown from the first report on; marshal it (a <see cref="Progress{T}"/> made on the UI thread does).</summary>
    public void Report(RemoteEditFlushProgress value)
    {
        CurrentFile = value.Current;
        CountText = value.Total > 1 ? $"{Math.Min(value.Done + 1, value.Total)} / {value.Total}" : string.Empty;
    }

    /// <summary>
    /// Stop waiting. The upload in flight is cut off; nothing is lost, the copies stay. Cancelled off this
    /// thread: the cancel runs the upload's unwinding inline, and closing its stream is a network wait (INV-06).
    /// </summary>
    [RelayCommand]
    public void Stop()
    {
        _ = Task.Run(_stop.Cancel);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Closes the window when the flush has ended by itself.</summary>
    public void Finish() => CloseRequested?.Invoke(this, EventArgs.Empty);
}
