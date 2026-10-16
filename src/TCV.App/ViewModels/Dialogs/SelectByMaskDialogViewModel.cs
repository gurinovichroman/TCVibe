using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>Backs the "select / unselect by mask" dialog (TC's Num+ / Num-).</summary>
public partial class SelectByMaskDialogViewModel : ObservableObject
{
    public SelectByMaskDialogViewModel(bool select)
    {
        WindowTitle = LocalizationManager.Get(select ? "Mask_TitleSelect" : "Mask_TitleUnselect");
        Prompt = LocalizationManager.Get(select ? "Mask_PromptSelect" : "Mask_PromptUnselect");
    }

    public string WindowTitle { get; }
    public string Prompt { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _mask = "*.*";

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

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(Mask);
}
