using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class AgentLaunchDialog : TcvDialog
{
    public AgentLaunchDialog(AgentLaunchDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);
    }
}
