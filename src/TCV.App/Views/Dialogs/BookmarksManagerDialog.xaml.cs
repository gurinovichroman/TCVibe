using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class BookmarksManagerDialog : TcvDialog
{
    public BookmarksManagerDialog(BookmarksManagerDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, ok) => DialogResult = ok;
    }
}
