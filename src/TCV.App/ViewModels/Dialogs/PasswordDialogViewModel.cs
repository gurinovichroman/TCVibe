using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace TCV.App.ViewModels.Dialogs;

public partial class PasswordDialogViewModel : ObservableObject
{
    public string ArchiveName { get; }

    /// <summary>The line above the box: what the password is for.</summary>
    public string Prompt => string.Format(TCV.App.Localization.LocalizationManager.Get("Password_Prompt"), ArchiveName);

    /// <summary>Set from code-behind via PasswordBox.PasswordChanged.</summary>
    public string Password { get; set; } = "";

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    public PasswordDialogViewModel(string archiveName)
    {
        ArchiveName = archiveName;
    }

    [RelayCommand]
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
}
