using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class SavedOperationsDialog : TcvDialog
{
    public SavedOperationsDialog(SavedOperationsDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Opened with a fresh draft (nothing saved yet): the name is the first thing to type.
        if (vm.SelectedItem is { Original: null })
        {
            Loaded += (_, _) => FocusAndSelect(NameBox);
        }

        vm.CloseRequested += (_, saved) => CloseWithResult(saved);
    }
}
