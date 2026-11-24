using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>The directory comparison / synchronisation window (Ctrl+K Ctrl+S, task 6.23).</summary>
public partial class CompareDirectoriesDialog : TcvDialog
{
    public CompareDirectoriesDialog(CompareDirectoriesDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Not modal (6.96), so no DialogResult: the caller reads viewModel.Confirmed.
        viewModel.CloseRequested += (_, _) => Close();

        // The user asked to compare the two panels: start now instead of showing an empty grid.
        Loaded += (_, _) => viewModel.CompareCommand.Execute(null);

        // However the window closes (X, Esc, buttons), a comparison still walking two trees stops.
        Closed += (_, _) => viewModel.Dispose();
    }
}
