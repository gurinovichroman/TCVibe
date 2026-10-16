using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class InputDialog : TcvDialog
{
    public InputDialog(InputDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
        Loaded += (_, _) => FocusAndSelect(ValueBox);
    }
}
