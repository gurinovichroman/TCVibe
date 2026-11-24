using System.ComponentModel;
using System.Windows.Input;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

/// <summary>Waits for F4 edits on their way to the server before the program closes (TRAP-105).</summary>
public partial class RemoteEditFlushDialog : TcvDialog
{
    private readonly RemoteEditFlushDialogViewModel _viewModel;
    private bool _closing;

    public RemoteEditFlushDialog(RemoteEditFlushDialogViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) =>
        {
            if (!_closing)
            {
                _closing = true;
                Close();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                _viewModel.Stop();
            }
        };
    }

    /// <summary>The cross means what the button says: stop waiting, keep the copies.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;
            _viewModel.Stop();
        }

        base.OnClosing(e);
    }
}
