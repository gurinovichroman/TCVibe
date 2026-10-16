using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class UnpackDialog : TcvDialog
{
    public UnpackDialog(UnpackDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };

        Loaded += (_, _) => FocusAndSelect(DestBox);
    }
}
