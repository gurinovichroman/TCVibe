using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Generic single-line text prompt (e.g. "name this workspace"). OK needs a non-blank value unless
/// the caller says an empty answer is meaningful (<paramref name="allowEmpty"/>) — the prompts that
/// promise "leave empty to clear" would otherwise be impossible to complete.
/// </summary>
public partial class InputDialogViewModel : ObservableObject
{
    public InputDialogViewModel(string windowTitle, string prompt, string initial = "", bool allowEmpty = false)
    {
        WindowTitle = windowTitle;
        Prompt = prompt;
        AllowEmpty = allowEmpty;
        Value = initial;
    }

    public string WindowTitle { get; }
    public string Prompt { get; }
    public bool AllowEmpty { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _value = string.Empty;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    private bool CanConfirm() => AllowEmpty || !string.IsNullOrWhiteSpace(Value);

    // OK is disabled while the field is empty, like every other dialog, instead of silently doing nothing.
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        if (!CanConfirm()) return;
        DialogResult = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
