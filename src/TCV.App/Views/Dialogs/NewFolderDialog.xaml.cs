using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class NewFolderDialog : TcvDialog
{
    public NewFolderDialog(NewFolderDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
        Loaded += (_, _) => FocusAndSelect(NameBox);
    }
}
