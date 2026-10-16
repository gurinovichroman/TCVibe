using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>The preview of an import from <c>~/.ssh/config</c>, opened from the connection manager (6.82).</summary>
public partial class SshConfigImportDialog : TcvDialog
{
    public SshConfigImportDialog(SshConfigImportDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Closed through the view model (TRAP-39): a Click handler that closed would kill the command.
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.Confirmed);
        Loaded += (_, _) => HostList.Focus();
    }
}
