using System.Windows.Controls;
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

        // The mask is an editable combo box: its text box exists once the template is applied, and the
        // last mask comes up selected so typing replaces it (TRAP-89).
        Loaded += (_, _) =>
        {
            MaskBox.ApplyTemplate();
            if (MaskBox.Template.FindName("PART_EditableTextBox", MaskBox) is TextBox box)
            {
                FocusAndSelect(box);
            }
            else
            {
                MaskBox.Focus();
            }
        };
    }
}
