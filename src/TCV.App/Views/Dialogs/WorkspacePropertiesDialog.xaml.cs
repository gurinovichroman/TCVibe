using System.Windows;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class WorkspacePropertiesDialog : TcvDialog
{
    private readonly WorkspacePropertiesDialogViewModel _viewModel;

    public WorkspacePropertiesDialog(WorkspacePropertiesDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;
        // Closed by the view model, not by a Click handler: Click runs before Command (TRAP-39).
        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);
        Loaded += (_, _) =>
        {
            // Renaming is the most common reason to come here (it used to be its own prompt on the
            // same shortcut), so the name is ready to be typed over.
            FocusAndSelect(NameBox);
        };
    }

    /// <summary>Any colour outside the swatches: the standard Windows colour dialog.</summary>
    private void OnPickOtherColor(object sender, RoutedEventArgs e)
    {
        if (SystemColorPicker.Pick(this, _viewModel.NormalizedColor) is { } picked)
        {
            _viewModel.ColorText = picked;
        }
    }
}
