using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class CopyDialog : TcvDialog
{
    public CopyDialog(CopyDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
        Loaded += (_, _) => FocusAndSelect(TargetBox);
    }
}
