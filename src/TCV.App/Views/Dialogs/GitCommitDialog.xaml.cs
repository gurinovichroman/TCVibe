using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class GitCommitDialog : TcvDialog
{
    private readonly GitCommitDialogViewModel _viewModel;

    public GitCommitDialog(GitCommitDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        viewModel.CloseRequested += (_, _) => CloseWithResult(viewModel.DialogResult);

        // Tunnelling, so the message box (AcceptsReturn) never sees it.
        PreviewKeyDown += OnPreviewKeyDown;

        Loaded += async (_, _) =>
        {
            await viewModel.RefreshAsync();
            MessageBox.Focus();
        };
    }

    /// <summary>Ctrl+Enter commits from anywhere in the dialog — the one keyboard route to it while
    /// the cursor is in the message, where plain Enter starts a new line. Exactly Ctrl: Ctrl+Alt is
    /// AltGr in a text box (TRAP-07).</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;

        e.Handled = true;
        if (_viewModel.CommitCommand.CanExecute(null)) _viewModel.CommitCommand.Execute(null);
    }

    private void OnListSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _viewModel.HasUnstagedSelection = UnstagedList.SelectedItems.Count > 0;
        _viewModel.HasStagedSelection = StagedList.SelectedItems.Count > 0;
    }

    // Enter on a list moves its selection across, as double-click does. Left alone it would press
    // the dialog's default button — Commit — which is the opposite of "stage this file".
    private void OnUnstagedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;

        e.Handled = true;
        if (UnstagedList.SelectedItem is not null) _viewModel.StageSelectedCommand.Execute(UnstagedList.SelectedItems);
    }

    private void OnStagedKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;

        e.Handled = true;
        if (StagedList.SelectedItem is not null) _viewModel.UnstageSelectedCommand.Execute(StagedList.SelectedItems);
    }

    // Double-click moves the file(s) under the pointer across, which is the fastest way to stage a handful.
    private void OnUnstagedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (UnstagedList.SelectedItem is null) return;
        _viewModel.StageSelectedCommand.Execute(UnstagedList.SelectedItems);
    }

    private void OnStagedDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (StagedList.SelectedItem is null) return;
        _viewModel.UnstageSelectedCommand.Execute(StagedList.SelectedItems);
    }
}
