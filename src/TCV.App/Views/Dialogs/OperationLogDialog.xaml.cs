using System.Windows;
using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>The file-operation log window: a read-only history of copy/move/delete/new-folder.</summary>
public partial class OperationLogDialog : Window
{
    private readonly OperationLogDialogViewModel _viewModel;

    public OperationLogDialog(OperationLogDialogViewModel viewModel)
    {
        InitializeComponent();
        Controls.TitleBarTheme.Follow(this);
        DataContext = viewModel;
        _viewModel = viewModel;
        // Stop listening to the log once the window is gone, so the VM doesn't leak on the event.
        Closed += (_, _) => _viewModel.Dispose();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// The log is the one audit trail of what the manager and the agents did, and the agent-changes
    /// dashboard reads its attribution from it. Wiping it is asked about, and "No" is the default (INV-11).
    /// </summary>
    private void OnClear(object sender, RoutedEventArgs e)
    {
        var confirmed = ConfirmDialog.Ask(
            Localization.LocalizationManager.Get("Log_Title"),
            Localization.LocalizationManager.Get("Log_ClearConfirm"),
            Localization.LocalizationManager.Get("Log_ClearButton"),
            Localization.LocalizationManager.Get("Cancel"),
            isWarning: true,
            owner: this);

        if (confirmed)
            _viewModel.ClearCommand.Execute(null);
    }

    /// <summary>
    /// Saves the selected log line as a named, replayable operation (6.22) — "do that again",
    /// available afterwards from Ctrl+K Ctrl+O.
    /// </summary>
    private void OnSaveAsOperation(object sender, RoutedEventArgs e)
    {
        var suggested = _viewModel.Selected?.KindText ?? string.Empty;
        var input = new InputDialogViewModel(
            Localization.LocalizationManager.Get("SavedOp_Title"),
            Localization.LocalizationManager.Get("SavedOp_NamePrompt"),
            suggested);
        var dlg = new InputDialog(input) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var saved = _viewModel.SaveSelectedAsOperation(input.Value);
        MessageBox.Show(this,
            saved is null
                ? Localization.LocalizationManager.Get("Log_SaveFailed")
                : string.Format(Localization.LocalizationManager.Get("Log_Saved"), saved),
            Localization.LocalizationManager.Get("SavedOp_Title"),
            MessageBoxButton.OK,
            saved is null ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F5:
                _viewModel.RefreshCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
