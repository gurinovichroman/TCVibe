using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>The drop-stack window (Ctrl+K Ctrl+D, task 6.24).</summary>
public partial class DropStackDialog : TcvDialog
{
    public DropStackDialog(DropStackDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.Confirmed);
    }
}
