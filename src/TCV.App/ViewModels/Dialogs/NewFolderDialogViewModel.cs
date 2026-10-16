using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

public partial class NewFolderDialogViewModel : ObservableObject
{
    public NewFolderDialogViewModel()
    {
        _folderName = LocalizationManager.Get("NewFolder_Default");
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(HasValidationMessage))]
    private string _folderName;

    /// <summary>Why OK is disabled, or empty.</summary>
    public string ValidationMessage =>
        FileNameRules.GetErrorKey(FolderName) is { } key ? LocalizationManager.Get(key) : string.Empty;

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

    private bool CanConfirm() => FileNameRules.IsValid(FolderName);
}
