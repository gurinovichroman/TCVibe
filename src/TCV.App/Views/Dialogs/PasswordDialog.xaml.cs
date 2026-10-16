using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class PasswordDialog : TcvDialog
{
    private readonly PasswordDialogViewModel _vm;

    public PasswordDialog(PasswordDialogViewModel viewModel)
    {
        InitializeComponent();
        _vm = viewModel;
        DataContext = viewModel;

        // PasswordBox cannot be bound via MVVM; sync via PasswordChanged
        PwdBox.PasswordChanged += (_, _) => _vm.Password = PwdBox.Password;

        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };

        Loaded += (_, _) => PwdBox.Focus();
    }
}
