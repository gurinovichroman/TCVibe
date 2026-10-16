using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using TCV.App.Localization;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Views.Dialogs;

public partial class ConnectionManagerDialog : TcvDialog
{
    private ConnectionManagerDialogViewModel Vm => (ConnectionManagerDialogViewModel)DataContext;

    /// <summary>The view model has settled unsaved edits and asked to close: Closing must not ask again.</summary>
    private bool _closeApproved;

    private ConnectionEditorViewModel? _editor;

    public ConnectionManagerDialog(ConnectionManagerDialogViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Closed through the view model (TRAP-39): a Click handler that closed would kill the command.
        viewModel.CloseRequested += (_, _) =>
        {
            _closeApproved = true;
            Close();
        };

        // PasswordBox has no two-way binding: every keystroke goes to the editor on screen.
        PasswordBox.PasswordChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.Password = PasswordBox.Password;
            }
        };
        PassphraseBox.PasswordChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.SshKeyPassphrase = PassphraseBox.Password;
            }
        };
        ClientSecretBox.PasswordChanged += (_, _) =>
        {
            if (_editor is not null)
            {
                _editor.ClientSecret = ClientSecretBox.Password;
            }
        };

        Loaded += (_, _) =>
        {
            if (viewModel.Editor is { IsNew: true })
            {
                AddressBox.Focus();
            }
            else
            {
                ConnectionList.Focus();
            }
        };
    }

    /// <summary>The title-bar X and Esc ask about unsaved edits like Close does.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeApproved && !Vm.CanClose())
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Keyboard.FocusedElement is not ComboBoxItem
            && !(Keyboard.FocusedElement is ComboBox { IsDropDownOpen: true }))
        {
            e.Handled = true;
            Vm.CloseCommand.Execute(null);
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>Another connection on the right: its secrets go into the password boxes.</summary>
    private void OnEditorChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_editor is not null)
        {
            _editor.PasswordTyped -= OnPasswordTyped;
        }

        _editor = e.NewValue as ConnectionEditorViewModel;
        if (_editor is null)
        {
            return;
        }

        _editor.PasswordTyped += OnPasswordTyped;
        var editor = _editor;
        _editor = null;   // seeding the boxes must not write back as an edit
        PasswordBox.Password = editor.Password;
        PassphraseBox.Password = editor.SshKeyPassphrase;
        ClientSecretBox.Password = editor.ClientSecret;
        _editor = editor;
    }

    private void OnPasswordTyped(object? sender, EventArgs e)
    {
        if (_editor is { } editor)
        {
            _editor = null;
            PasswordBox.Password = editor.Password;
            _editor = editor;
        }
    }

    /// <summary>Enter in the address box fills the fields; it is not "Connect" yet.</summary>
    private void OnAddressKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return && _editor is { } editor)
        {
            e.Handled = true;
            editor.ApplyAddressCommand.Execute(null);
            NameBox.Focus();
        }
    }

    private void ConnectionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ConnectionList, source) is ListBoxItem { DataContext: ConnectionRowViewModel row })
        {
            Vm.OnRowDoubleClicked(row);
        }
    }

    private void BrowseSshKey_Click(object sender, RoutedEventArgs e)
    {
        if (_editor is null)
        {
            return;
        }

        var dlg = new OpenFileDialog
        {
            Title = LocalizationManager.Get("Conn_KeyFileTitle"),
            Filter = LocalizationManager.Get("Conn_KeyFileFilter"),
            CheckFileExists = true,
        };

        if (dlg.ShowDialog(this) == true)
        {
            _editor.SshKeyPath = dlg.FileName;
        }
    }
}
