using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class UserToolsDialog : TcvDialog
{
    public UserToolsDialog(UserToolsDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // The file picker is a window, so it belongs to the view; the view model only asks for a path.
        vm.PickExecutable = PickProgram;

        vm.CloseRequested += (_, saved) => CloseWithResult(saved);
    }
}
