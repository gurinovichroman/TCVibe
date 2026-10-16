using System.Windows;
using TCV.App.Localization;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class WorkspaceNotesDialog : TcvDialog
{
    public WorkspaceNotesDialog(WorkspaceNotesDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            DialogResult = viewModel.DialogResult;
            Close();
        };
        // Esc, Cancel and the title-bar X all end here; edited notes are not dropped without asking.
        Closing += (_, e) =>
        {
            if (DialogResult == true || !viewModel.IsDirty)
            {
                return;
            }

            var discard = MessageBox.Show(
                this,
                LocalizationManager.Get("WsNotes_DiscardConfirm"),
                viewModel.WindowTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            e.Cancel = discard != MessageBoxResult.Yes;
        };
        Loaded += (_, _) =>
        {
            // Land the caret at the end of the existing notes — this dialog is opened to append
            // far more often than to rewrite.
            NotesBox.CaretIndex = NotesBox.Text.Length;
            NotesBox.Focus();
        };
    }
}
