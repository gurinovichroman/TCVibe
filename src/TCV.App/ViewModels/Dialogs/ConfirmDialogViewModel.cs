using CommunityToolkit.Mvvm.Input;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>The button a <see cref="ConfirmDialogViewModel"/> was closed with.</summary>
public enum ConfirmChoice
{
    /// <summary>The way back — also the answer when the window is closed any other way.</summary>
    Cancel,

    /// <summary>The first way to go ahead (<see cref="ConfirmDialogViewModel.ConfirmText"/>).</summary>
    Confirm,

    /// <summary>The second way to go ahead (<see cref="ConfirmDialogViewModel.AlternateText"/>).</summary>
    Alternate,
}

/// <summary>
/// A yes/no question in the application's own window and language. It replaces the Win32 message
/// box, whose buttons follow the Windows display language (a Russian question with "Yes/No") and
/// cannot carry a verb ("Remove", "Clear") for the action being confirmed. With an
/// <see cref="AlternateText"/> it offers two ways to go ahead ("Merge" / "Replace") and still one way
/// back, which stays the default button.
/// </summary>
public sealed partial class ConfirmDialogViewModel
{
    public ConfirmDialogViewModel(
        string title, string message, string confirmText, string cancelText, bool isWarning,
        string? alternateText = null)
    {
        Title = title;
        Message = message;
        ConfirmText = confirmText;
        CancelText = cancelText;
        IsWarning = isWarning;
        AlternateText = alternateText ?? string.Empty;
    }

    public string Title { get; }
    public string Message { get; }

    /// <summary>The verb on the button that goes ahead.</summary>
    public string ConfirmText { get; }

    /// <summary>The verb on a second button that goes ahead differently; empty when there is none.</summary>
    public string AlternateText { get; }

    /// <summary>True when the second way ahead is offered.</summary>
    public bool HasAlternate => AlternateText.Length > 0;

    /// <summary>The button that backs out; it is the default, as "No" was in the message box.</summary>
    public string CancelText { get; }

    /// <summary>Shows the warning mark: the action loses something.</summary>
    public bool IsWarning { get; }

    /// <summary>True when the user went ahead, by either button.</summary>
    public bool DialogResult { get; private set; }

    /// <summary>Which button closed the window.</summary>
    public ConfirmChoice Choice { get; private set; }

    public event EventHandler? CloseRequested;

    [RelayCommand]
    private void Confirm() => Close(ConfirmChoice.Confirm);

    [RelayCommand]
    private void Alternate() => Close(ConfirmChoice.Alternate);

    [RelayCommand]
    private void Cancel() => Close(ConfirmChoice.Cancel);

    private void Close(ConfirmChoice choice)
    {
        Choice = choice;
        DialogResult = choice != ConfirmChoice.Cancel;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
