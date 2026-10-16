using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Services;

namespace TCV.App.ViewModels.Dialogs;

public partial class UnpackDialogViewModel : ObservableObject
{
    /// <param name="archiveNames">One archive is named; several are counted (Alt+F9 on a selection).</param>
    public UnpackDialogViewModel(IReadOnlyList<string> archiveNames, string defaultDestDir, bool canUseWindowsShell)
    {
        Description = archiveNames.Count == 1
            ? string.Format(TCV.App.Localization.LocalizationManager.Get("Unpack_Description"), archiveNames[0])
            : TCV.App.Localization.LocalizationManager.Format("Unpack_DescriptionMany", archiveNames.Count);
        _destinationPath = defaultDestDir;
        WindowsShellOptionVisible = canUseWindowsShell;
        _useWindowsShell = false; // default: use our own engine (reliable, synchronous)
    }

    public string Description { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _destinationPath;

    public bool WindowsShellOptionVisible { get; }

    [ObservableProperty]
    private bool _useWindowsShell;

    // ---- Dialog result ----
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

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(DestinationPath);

    public UnpackDialogResult BuildResult() => new(
        DestinationPath: DestinationPath,
        UseWindowsShell: WindowsShellOptionVisible && UseWindowsShell);
}
