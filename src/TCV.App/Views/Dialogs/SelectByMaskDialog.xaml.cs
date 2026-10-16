using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class SelectByMaskDialog : TcvDialog
{
    public SelectByMaskDialog(SelectByMaskDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
        Loaded += (_, _) => FocusAndSelect(MaskBox);
    }
}
