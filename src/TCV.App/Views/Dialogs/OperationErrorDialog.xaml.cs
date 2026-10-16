using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class OperationErrorDialog : TcvDialog
{
    public OperationErrorDialog(OperationErrorDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => CloseWithResult(true);
    }
}
