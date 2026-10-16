using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class ChecksumDialog : TcvDialog
{
    public ChecksumDialog(ChecksumDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Closing the window must stop the hashing too; otherwise it carries on reading the disk unseen.
        Closed += (_, _) => vm.Dispose();

        // Opened on a checksum file: check it once the window is up, so the user watches it happen.
        Loaded += async (_, _) => await vm.StartAsync();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
