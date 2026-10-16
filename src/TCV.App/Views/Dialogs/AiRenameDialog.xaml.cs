using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class AiRenameDialog : TcvDialog
{
    public AiRenameDialog(AiRenameDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // The proposal is one network round trip, so it starts once the window is visible rather than
        // in the constructor — otherwise the dialog appears already frozen.
        Loaded += async (_, _) => await viewModel.ProposeAsync();

        // Closing by the title-bar X must also stop a request still waiting on the model.
        Closed += (_, _) => viewModel.Abandon();
    }
}
