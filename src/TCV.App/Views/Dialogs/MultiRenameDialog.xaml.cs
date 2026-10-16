using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class MultiRenameDialog : TcvDialog
{
    public MultiRenameDialog(MultiRenameDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    // The dialog stays open after a rename (so it can be undone). "Закрыть" just dismisses it;
    // the caller reloads the panel unconditionally afterwards, so DialogResult is not needed.
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
