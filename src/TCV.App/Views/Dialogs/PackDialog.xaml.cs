using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class PackDialog : TcvDialog
{
    private readonly PackDialogViewModel _viewModel;

    public PackDialog(PackDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };

        PasswordBox.PasswordChanged     += (_, _) => viewModel.Password        = PasswordBox.Password;
        PasswordConfirmBox.PasswordChanged += (_, _) => viewModel.PasswordConfirm = PasswordConfirmBox.Password;

        Loaded += (_, _) => FocusAndSelect(NameBox);
    }
}
