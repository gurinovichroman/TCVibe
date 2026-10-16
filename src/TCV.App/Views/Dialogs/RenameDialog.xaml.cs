using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class RenameDialog : TcvDialog
{
    public RenameDialog(RenameDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
        // Select the name without the extension (a folder's whole name) for easy renaming
        Loaded += (_, _) => FocusAndSelect(NameBox, viewModel.InitialSelectionLength);
    }
}
