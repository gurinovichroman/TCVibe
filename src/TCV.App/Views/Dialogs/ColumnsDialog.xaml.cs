using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>The column chooser for the details view (Ctrl+K Ctrl+C, task 6.19).</summary>
public partial class ColumnsDialog : TcvDialog
{
    public ColumnsDialog(ColumnsDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.Confirmed);
    }
}
