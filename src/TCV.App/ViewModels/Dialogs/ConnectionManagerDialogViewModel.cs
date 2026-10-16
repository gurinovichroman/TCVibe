using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Network;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One saved connection in the manager's list: what it is, where it points, whether it is open.</summary>
public sealed partial class ConnectionRowViewModel : ObservableObject
{
    public ConnectionRowViewModel(ConnectionInfo info, string? whereOpen)
    {
        Info = info;
        WhereOpen = whereOpen;
    }

    public ConnectionInfo Info { get; }

    public string Title => PanelViewModel.ConnectionTitle(Info);

    /// <summary>Kind and address, the second line of the row: "SFTP (SSH) · sftp://deploy@prod.example".</summary>
    public string Subtitle =>
        $"{LocalizationManager.Get("Conn_Protocol_" + Info.Protocol)} · {Info.DescribeAddress(Info.IsObjectStorage ? "/" : Info.RemotePath)}";

    /// <summary>Where the connection is open now ("left panel"), or null: the mock's "Connected / Disconnected".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    private string? _whereOpen;

    public bool IsOpen => WhereOpen is not null;

    public bool PasswordUnreadable => Info.PasswordUnreadable;

    public bool IsReadOnly => Info.ReadOnly;

    public System.Windows.Media.Brush? ColorBrush =>
        string.IsNullOrEmpty(Info.Color) ? null : Converters.WorkspaceColorToBrushConverter.Instance.Convert(
            Info.Color, typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture)
            as System.Windows.Media.Brush;

    /// <summary>The kind of icon the row shows: a server, a cloud, a drive.</summary>
    public string Kind => Info.IsObjectStorage || Info.IsOAuth ? "cloud" : Info.IsSmb ? "share" : "server";

    /// <summary>What a screen reader announces for the row (and what UI Automation names it).</summary>
    public override string ToString() => Title;

    public bool Matches(string filter) =>
        filter.Length == 0
        || Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
        || Subtitle.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// The connection manager: the saved connections on the left (search, what is open where), the
/// selected one's fields on the right — one window, like FileZilla's Site Manager or WinSCP's Login,
/// instead of a list that opened a second modal editor for every change.
/// </summary>
public partial class ConnectionManagerDialogViewModel : ObservableObject
{
    private readonly ConnectionBook _book;
    private readonly Func<ConnectionInfo, string?> _whereOpen;
    private readonly Action<Guid> _disconnect;
    private readonly Func<string, string, bool> _confirm;
    private readonly Action<string, string> _showMessage;
    private readonly Action<string, string> _showInfo;
    private readonly HostTrustPolicy? _trust;
    private readonly Func<SshConfigImportDialogViewModel, bool> _showImport;
    private readonly string _homeDirectory;
    private List<ConnectionRowViewModel> _allRows = [];
    private bool _selecting;

    /// <param name="book">Where connections are kept.</param>
    /// <param name="whereOpen">Where a connection is open now, for the list; null when nowhere.</param>
    /// <param name="disconnect">Closes a connection in every tab that has it.</param>
    /// <param name="select">The connection to show first; the first one when null.</param>
    /// <param name="address">A typed address nothing saved matches: a new connection is filled from it.</param>
    /// <param name="confirm">Yes/no question (title, message); the default shows the app's confirm.</param>
    /// <param name="showMessage">Error display (title, message); the default shows a message box.</param>
    /// <param name="trust">Where "forget server key" acts; defaults to the application-wide policy.</param>
    /// <param name="showInfo">Outcome display (title, message) — no error icon on a success.</param>
    /// <param name="showImport">The preview of an import from <c>~/.ssh/config</c>; true when the user chose Import.</param>
    /// <param name="homeDirectory">The user's folder, where <c>.ssh\config</c> is read; the profile folder by default.</param>
    public ConnectionManagerDialogViewModel(
        ConnectionBook book,
        Func<ConnectionInfo, string?>? whereOpen = null,
        Action<Guid>? disconnect = null,
        Guid? select = null,
        string? address = null,
        Func<string, string, bool>? confirm = null,
        Action<string, string>? showMessage = null,
        HostTrustPolicy? trust = null,
        Action<string, string>? showInfo = null,
        Func<SshConfigImportDialogViewModel, bool>? showImport = null,
        string? homeDirectory = null)
    {
        // The window is the dialog service's (a view model makes none); without one the import shows nothing.
        _showImport = showImport ?? (_ => false);
        _homeDirectory = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _book = book;
        _whereOpen = whereOpen ?? (_ => null);
        _disconnect = disconnect ?? (_ => { });
        // The app's own confirm (UI-language buttons, owned by the active window), not MessageBox.
        _confirm = confirm ?? ((title, message) => Views.Dialogs.ConfirmDialog.Ask(title, message, isWarning: true));
        _showMessage = showMessage ?? ((title, message) => ShowMessageBox(
            message, title, System.Windows.MessageBoxImage.Error));
        _showInfo = showInfo ?? ((title, message) => ShowMessageBox(
            message, title, System.Windows.MessageBoxImage.Information));
        _trust = trust ?? NetworkSecurity.Default;

        Reload();
        if (!string.IsNullOrWhiteSpace(address))
        {
            NewConnection();
            Editor!.Address = address;
            Editor.ApplyAddressCommand.Execute(null);
        }
        else
        {
            SelectedRow = Rows.FirstOrDefault(r => r.Info.Id == select) ?? Rows.FirstOrDefault();
            if (SelectedRow is null)
            {
                NewConnection();
            }
        }
    }

    // ── List ──────────────────────────────────────────────────────────────────

    public ObservableCollection<ConnectionRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private string _filter = string.Empty;

    partial void OnFilterChanged(string value) => ApplyFilter();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DuplicateCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForgetHostCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisconnectCommand))]
    private ConnectionRowViewModel? _selectedRow;

    /// <summary>The fields on the right: the selected connection's, or a new one's.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    [NotifyPropertyChangedFor(nameof(EditorTitle))]
    private ConnectionEditorViewModel? _editor;

    /// <summary>Above the fields: the connection's name, or "New connection".</summary>
    public string EditorTitle => Editor is { IsNew: true }
        ? LocalizationManager.Get("ConnMgr_NewTitle")
        : SelectedRow?.Title ?? string.Empty;

    /// <summary>Why the address book came up empty or partial, or empty when it loaded normally.</summary>
    public string LoadWarning =>
        _book.LoadError is { Length: > 0 } error
            ? string.Format(LocalizationManager.Get("ConnMgr_LoadProblem"), error)
            : string.Empty;

    public bool HasLoadWarning => LoadWarning.Length > 0;

    /// <summary>Nothing saved yet: the list shows a hint instead of an empty box.</summary>
    public bool IsEmpty => _allRows.Count == 0;

    // ── Result ────────────────────────────────────────────────────────────────

    /// <summary>The connection to open once the window has closed; null if it was just closed.</summary>
    public ConnectionInfo? ConnectRequest { get; private set; }

    public event EventHandler? CloseRequested;

    // ── Selection ─────────────────────────────────────────────────────────────

    partial void OnSelectedRowChanging(ConnectionRowViewModel? oldValue, ConnectionRowViewModel? newValue)
    {
        if (_selecting || newValue is null || ReferenceEquals(oldValue, newValue))
        {
            return;
        }

        // Moving on from unsaved changes asks first; "no" drops them.
        if (!SettlePendingChanges())
        {
            _selecting = true;
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                SelectedRow = oldValue;
                _selecting = false;
            });
        }
    }

    partial void OnSelectedRowChanged(ConnectionRowViewModel? value)
    {
        if (_selecting)
        {
            return;
        }

        if (value is not null)
        {
            Editor = CreateEditor(new ConnectionEditorViewModel(value.Info));
        }

        OnPropertyChanged(nameof(EditorTitle));
    }

    private ConnectionEditorViewModel CreateEditor(ConnectionEditorViewModel editor)
    {
        if (Editor is { } old)
        {
            old.Changed -= OnEditorChanged;
        }

        editor.Changed += OnEditorChanged;
        return editor;
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        SaveCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Asks what to do with unsaved edits before they would be lost. True when it is fine to move on
    /// (saved, dropped, or nothing to settle); false when saving failed.
    /// </summary>
    private bool SettlePendingChanges()
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        var name = string.IsNullOrWhiteSpace(editor.ConnectionName) ? EditorTitle : editor.ConnectionName;
        if (!_confirm(LocalizationManager.Get("ConnMgr_Title"),
                string.Format(LocalizationManager.Get("ConnMgr_SaveChanges"), name)))
        {
            return true;
        }

        return editor.IsValid ? TrySaveEditor() : ReportInvalid(editor);
    }

    private bool ReportInvalid(ConnectionEditorViewModel editor)
    {
        _showMessage(LocalizationManager.Get("ConnMgr_Title"), editor.ValidationMessage);
        return false;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>A blank connection on the right; nothing is added to the book until it is saved.</summary>
    [RelayCommand]
    private void New()
    {
        if (SettlePendingChanges())
        {
            NewConnection();
        }
    }

    private void NewConnection()
    {
        _selecting = true;
        SelectedRow = null;
        _selecting = false;
        Editor = CreateEditor(new ConnectionEditorViewModel());
        OnPropertyChanged(nameof(EditorTitle));
    }

    /// <summary>A copy of the selected connection as a new one: another server of the same kind.</summary>
    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Duplicate()
    {
        if (SelectedRow is null || !SettlePendingChanges())
        {
            return;
        }

        var copy = SelectedRow.Info.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name = string.Format(LocalizationManager.Get("ConnMgr_CopyName"), copy.Name);
        copy.RefreshToken = null;   // a sign-in belongs to the connection that made it
        _selecting = true;
        SelectedRow = null;
        _selecting = false;
        Editor = CreateEditor(new ConnectionEditorViewModel(copy, isNew: true));
        OnPropertyChanged(nameof(EditorTitle));
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save() => TrySaveEditor();

    private bool CanSave() => Editor is { IsValid: true, IsDirty: true };

    private bool TrySaveEditor()
    {
        if (Editor is not { } editor)
        {
            return false;
        }

        var info = editor.BuildConnectionInfo();
        if (!TryWrite(() =>
            {
                if (editor.IsNew)
                {
                    _book.Add(info);
                }
                else
                {
                    _book.Update(info);
                }
            }))
        {
            return false;
        }

        editor.MarkSaved();
        Reload();
        _selecting = true;
        SelectedRow = Rows.FirstOrDefault(r => r.Info.Id == info.Id);
        _selecting = false;
        OnPropertyChanged(nameof(EditorTitle));
        SaveCommand.NotifyCanExecuteChanged();
        return true;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Delete()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        // The password goes with it and cannot be brought back (INV-11).
        var question = string.Format(LocalizationManager.Get("ConnMgr_DeleteConfirm"), row.Title);
        if (!_confirm(LocalizationManager.Get("ConnMgr_Delete"), question))
        {
            return;
        }

        if (TryWrite(() => _book.Remove(row.Info.Id)))
        {
            Reload();
            if (Rows.FirstOrDefault() is { } first)
            {
                // Not through the selection's "unsaved edits?" question: the edits belong to the connection
                // that has just been deleted, and saving them would write it back (Update adds what is missing).
                _selecting = true;
                SelectedRow = first;
                _selecting = false;
                Editor = CreateEditor(new ConnectionEditorViewModel(first.Info));
                OnPropertyChanged(nameof(EditorTitle));
            }
            else
            {
                NewConnection();
            }
        }
    }

    /// <summary>
    /// Opens the connection on the right in the active panel, after the window closes. A new or edited
    /// connection is saved first: what connects is what the user sees.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private void Connect()
    {
        if (Editor is not { } editor)
        {
            return;
        }

        if (editor.IsDirty && !TrySaveEditor())
        {
            return;
        }

        ConnectRequest = editor.BuildConnectionInfo();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private bool CanConnect() => Editor is { IsValid: true };

    /// <summary>Closes the selected connection in every tab that has it; the window stays open.</summary>
    [RelayCommand(CanExecute = nameof(IsSelectionOpen))]
    private void Disconnect()
    {
        if (SelectedRow is not { } row)
        {
            return;
        }

        _disconnect(row.Info.Id);
        row.WhereOpen = _whereOpen(row.Info);
        DisconnectCommand.NotifyCanExecuteChanged();
    }

    private bool IsSelectionOpen() => SelectedRow is { IsOpen: true };

    /// <summary>
    /// Drops the remembered host key / certificate of the selected server. This is the deliberate
    /// way out of a "server identity changed" refusal once the change is known to be legitimate.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanForgetHost))]
    private void ForgetHost()
    {
        if (SelectedRow?.Info is not { } info || _trust is null)
        {
            return;
        }

        var hostKey = KnownHostsStore.HostKey(info.Host, info.Port);
        var kind = info.Protocol == NetworkProtocol.Sftp ? KnownHostsStore.SshKind : KnownHostsStore.TlsKind;
        var known = _trust.Store.Lookup(kind, hostKey) is not null;
        if (known)
        {
            _trust.Store.Forget(kind, hostKey);
        }

        // An outcome, not an error: it used to arrive wearing the error icon.
        _showInfo(
            LocalizationManager.Get("ConnMgr_ForgetHost"),
            string.Format(LocalizationManager.Get(known ? "ConnMgr_ForgetDone" : "ConnMgr_ForgetNothing"), hostKey));
    }

    /// <summary>Only servers with a pinned identity: SFTP host keys and FTPS certificates.</summary>
    private bool CanForgetHost() =>
        SelectedRow?.Info is { } info && (info.Protocol == NetworkProtocol.Sftp || info.IsFtps);

    /// <summary>
    /// Adds the hosts of <c>~/.ssh/config</c> as SFTP connections (6.82), after a preview of what each
    /// becomes; the ones already saved are offered unticked. The file is only read, never written.
    /// </summary>
    [RelayCommand]
    private void ImportSshConfig()
    {
        if (!SettlePendingChanges())
        {
            return;
        }

        var title = LocalizationManager.Get("SshImport_Title");
        var path = SshConfigFile.DefaultPath(_homeDirectory);
        SshConfigParseResult? parsed;
        try
        {
            parsed = SshConfigFile.Read(path, _homeDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showMessage(title, string.Format(LocalizationManager.Get("ConnMgr_ImportReadFailed"), path, ex.Message));
            return;
        }

        if (parsed is null)
        {
            _showInfo(title, string.Format(LocalizationManager.Get("ConnMgr_ImportNoFile"), path));
            return;
        }

        if (parsed.Hosts.Count == 0)
        {
            _showInfo(title, string.Format(LocalizationManager.Get("ConnMgr_ImportNoHosts"), path));
            return;
        }

        var plan = SshConfigImport.Plan(parsed.Hosts, _book.Connections, _homeDirectory, Environment.UserName, File.Exists);
        var preview = new SshConfigImportDialogViewModel(path, plan, parsed.Warnings);
        if (!_showImport(preview) || preview.Chosen is not { Count: > 0 } chosen)
        {
            return;
        }

        if (!TryWrite(() => _book.AddRange(chosen)))
        {
            return;
        }

        Reload();
        if (Rows.FirstOrDefault(r => r.Info.Id == chosen[0].Id) is { } first)
        {
            SelectedRow = first;
        }
    }

    [RelayCommand]
    private void Close()
    {
        if (SettlePendingChanges())
        {
            ConnectRequest = null;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The window's own close button: the same question about unsaved edits. False keeps it open.</summary>
    public bool CanClose() => SettlePendingChanges();

    // ── Double-click from the View ────────────────────────────────────────────

    public void OnRowDoubleClicked(ConnectionRowViewModel row)
    {
        if (ReferenceEquals(SelectedRow, row))
        {
            ConnectCommand.Execute(null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool HasSelection() => SelectedRow is not null;

    /// <summary>Runs a book write; a failure is shown instead of crashing the window.</summary>
    private bool TryWrite(Action write)
    {
        try
        {
            write();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _showMessage(
                LocalizationManager.Get("ConnMgr_Title"),
                string.Format(LocalizationManager.Get("ConnMgr_SaveFailed"), ex.Message));
            return false;
        }
    }

    private void Reload()
    {
        _allRows = _book.Connections
            .OrderBy(c => PanelViewModel.ConnectionTitle(c), StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ConnectionRowViewModel(c, _whereOpen(c)))
            .ToList();
        ApplyFilter();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LoadWarning));
        OnPropertyChanged(nameof(HasLoadWarning));
    }

    private void ApplyFilter()
    {
        var selectedId = SelectedRow?.Info.Id;
        _selecting = true;
        Rows.Clear();
        foreach (var row in _allRows.Where(r => r.Matches(Filter.Trim())))
        {
            Rows.Add(row);
        }

        SelectedRow = Rows.FirstOrDefault(r => r.Info.Id == selectedId);
        _selecting = false;
    }

    private static void ShowMessageBox(string message, string title, System.Windows.MessageBoxImage image)
    {
        var owner = System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
            ?? System.Windows.Application.Current?.MainWindow;
        if (owner is not null)
        {
            System.Windows.MessageBox.Show(owner, message, title, System.Windows.MessageBoxButton.OK, image);
        }
        else
        {
            System.Windows.MessageBox.Show(message, title, System.Windows.MessageBoxButton.OK, image);
        }
    }
}
