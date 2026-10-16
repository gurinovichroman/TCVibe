using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>The directory comparison / synchronisation window (Ctrl+K Ctrl+S, task 6.23).</summary>
public partial class CompareDirectoriesDialog : TcvDialog
{
    public CompareDirectoriesDialog(CompareDirectoriesDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.Confirmed);

        // The user asked to compare the two panels: start now instead of showing an empty grid.
        Loaded += (_, _) => viewModel.CompareCommand.Execute(null);

        // However the window closes (X, Esc, buttons), a comparison still walking two trees stops.
        Closed += (_, _) => viewModel.Dispose();
    }
}
