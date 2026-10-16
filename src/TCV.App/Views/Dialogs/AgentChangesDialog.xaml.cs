using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class AgentChangesDialog : TcvDialog
{
    private readonly AgentChangesDialogViewModel _viewModel;

    public AgentChangesDialog(AgentChangesDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // Reading git status and the operation log walks the whole working tree, so it happens after
        // the window is up — same reasoning as the worktree dashboard.
        Loaded += async (_, _) => await viewModel.RefreshAsync();
    }

    /// <summary>Double-click shows the diff: on a review surface that is the thing you want next.</summary>
    private void OnChangesDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.ShowDiffCommand.CanExecute(null))
            _viewModel.ShowDiffCommand.Execute(null);
    }
}
