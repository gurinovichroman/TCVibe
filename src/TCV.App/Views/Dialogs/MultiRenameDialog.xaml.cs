using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class MultiRenameDialog : TcvDialog
{
    public MultiRenameDialog(MultiRenameDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Through the view-model's event, not a Click handler on the button: Click comes before the
        // command and would close the window under it (TRAP-39). The caller reads vm.Accepted.
        vm.CloseRequested += (_, _) => Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
