using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class SettingsDialog : TcvDialog
{
    public SettingsDialog(SettingsDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // «Manage gadgets…» is this dialog opened on its Gadgets tab (6.50), «Command-line shells…» on Shells.
        if (vm.InitialTab == SettingsDialogViewModel.GadgetsTab)
        {
            GadgetsTabItem.IsSelected = true;
        }
        else if (vm.InitialTab == SettingsDialogViewModel.ShellsTab)
        {
            ShellsTabItem.IsSelected = true;
        }

        // The file picker is a window, so it belongs to the view; the view model only asks for a path.
        vm.PickExecutable = PickProgram;

        // PasswordBox cannot be bound: seed it and mirror every keystroke into the view model.
        LlmKeyBox.Password = vm.LlmApiKey;
        LlmKeyBox.PasswordChanged += (_, _) => vm.LlmApiKey = LlmKeyBox.Password;

        // The language picker applies live — undo it unless the user saved
        Closing += (_, _) =>
        {
            if (DialogResult != true)
            {
                vm.RevertUnsavedChanges();
            }
        };

        // A successful save closes the window; a failed one keeps it open with the reason shown.
        vm.CloseRequested += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
