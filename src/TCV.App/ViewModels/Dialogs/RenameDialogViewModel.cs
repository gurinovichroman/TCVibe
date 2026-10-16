using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

public partial class RenameDialogViewModel : ObservableObject
{
    public RenameDialogViewModel(string currentName, bool isDirectory = false)
    {
        CurrentName = currentName;
        IsDirectory = isDirectory;
        _newName = currentName;
    }

    public string CurrentName { get; }

    /// <summary>A folder has no extension: «v1.2» or «my.project» is selected whole.</summary>
    public bool IsDirectory { get; }

    /// <summary>How much of the name the text box selects on open: the name without the extension.</summary>
    public int InitialSelectionLength
    {
        get
        {
            if (IsDirectory) return CurrentName.Length;
            var dot = CurrentName.LastIndexOf('.');
            return dot > 0 ? dot : CurrentName.Length;
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _newName;

    /// <summary>Why OK is disabled, or empty.</summary>
    public string ValidationMessage =>
        FileNameRules.GetErrorKey(NewName) is { } key ? LocalizationManager.Get(key) : string.Empty;

    public bool HasValidationMessage => ValidationMessage.Length > 0;

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        DialogResult = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        DialogResult = false;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanConfirm() =>
        FileNameRules.IsValid(NewName) &&
        NewName != CurrentName;
}
