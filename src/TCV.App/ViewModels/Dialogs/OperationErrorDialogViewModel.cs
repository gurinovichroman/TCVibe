using CommunityToolkit.Mvvm.Input;
using TCV.App.Operations;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>Backs the "this item failed: retry, skip or abort?" prompt of a running operation.</summary>
public sealed partial class OperationErrorDialogViewModel
{
    public OperationErrorDialogViewModel(OperationErrorInfo error)
    {
        Path = error.Path;
        Message = error.Message;
    }

    public string Path { get; }
    public string Message { get; }

    // Abort, so closing the window with X never quietly skips (and later loses) an item.
    public OperationErrorResolution Resolution { get; private set; } = OperationErrorResolution.Abort;

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Retry() => Choose(OperationErrorResolution.Retry);

    [RelayCommand]
    private void Skip() => Choose(OperationErrorResolution.Skip);

    [RelayCommand]
    private void SkipAll() => Choose(OperationErrorResolution.SkipAll);

    [RelayCommand]
    private void Abort() => Choose(OperationErrorResolution.Abort);

    private void Choose(OperationErrorResolution resolution)
    {
        Resolution = resolution;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
