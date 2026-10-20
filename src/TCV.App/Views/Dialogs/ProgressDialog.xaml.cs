using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class ProgressDialog : TcvDialog
{
    public ProgressDialog(ProgressDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        Closed += (_, _) => viewModel.AbortOnClose();

        // «In background» hides the window, not closes it: closing cancels the work (ADR-0038).
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ProgressDialogViewModel.IsInBackground) || IsClosed)
            {
                return;
            }

            if (viewModel.IsInBackground)
            {
                Hide();
                Owner?.Activate();
            }
            else
            {
                Show();
                Activate();
            }
        };
    }
}
