using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.ViewModels;

/// <summary>
/// Mounting a panel on something that is not a local disk: the saved connections (servers, object
/// storage, drives, Windows shares), connecting and disconnecting, and the background transfer
/// queue. One of the four command facades <see cref="MainViewModel"/> was split into by task 6.26.
/// A connection belongs to the tab it was opened in (ADR-0032).
/// </summary>
public sealed partial class MountCommands : ObservableObject
{
    private readonly MainViewModel _shell;
    private readonly IDialogService _dialogs;
    private readonly ILogger _logger;
    private readonly ConnectionBook _connectionBook;
    private readonly TransferQueue _transferQueue;

    /// <summary>The addresses the user has agreed to send a password to unencrypted, in this run.</summary>
    private readonly ClearTextConsents _clearTextConsents = new();

    /// <summary>The connect in progress, if any — one at a time; the chip's cancel stops it.</summary>
    private CancellationTokenSource? _connecting;

    public MountCommands(
        MainViewModel shell,
        IDialogService dialogs,
        ILogger logger,
        ConnectionBook connectionBook,
        TransferQueue transferQueue)
    {
        _shell = shell;
        _dialogs = dialogs;
        _logger = logger;
        _connectionBook = connectionBook;
        _transferQueue = transferQueue;

        foreach (var panel in new[] { shell.LeftPanel, shell.RightPanel })
        {
            // A share is a disk path, not a mount: the panel asks here which saved share it stands in.
            panel.ShareConnectionFor = ShareAt;
            panel.OpenConnectionInActiveTab = (info, path) => OpenInActiveTabAsync(panel, info, path);
            panel.FileList.OpenConnectionAddress = text => OpenAddressAsync(text, panel);
            WatchConnection(panel);
        }

        StartKeepAlive();
    }

    // ── What this facade borrows from the shell ──────────────────────────────

    private PanelViewModel ActivePanel => _shell.ActivePanel;
    private PanelViewModel OppositePanel => _shell.OppositePanel;

    private IReadOnlyList<IFileEntry> GetSelectedOrCurrent() => _shell.GetSelectedOrCurrent();

    /// <summary>True while a queued or running transfer would be cut off by closing the window.</summary>
    public bool HasActiveTransfers => _transferQueue.HasActiveWork;

    // ── Connecting ────────────────────────────────────────────────────────────

    /// <summary>The saved connections, read afresh: a panel's connection drop-down lists them.</summary>
    public IReadOnlyList<ConnectionInfo> SavedConnections => _connectionBook.Connections;

    /// <summary>Ctrl+Shift+F — the saved connections: create, edit, test, open and close them.</summary>
    [RelayCommand]
    private void OpenConnectionManager() => ShowConnectionManager(select: null);

    /// <summary>
    /// The connection manager, on <paramref name="select"/> (the chip's "Edit connection…") or on a new
    /// connection filled from <paramref name="address"/> (a typed address nothing saved matches).
    /// Opening and closing happen after it closes, so an error surfaces against the main window.
    /// </summary>
    public void ShowConnectionManager(ConnectionInfo? select, string? address = null)
    {
        var vm = new ConnectionManagerDialogViewModel(_connectionBook, DescribeWhereOpen, CloseEverywhere, select?.Id, address,
            confirm: (title, message) => _dialogs.Confirm(title, message, isWarning: true),
            showMessage: (title, message) => _dialogs.ShowMessage(title, message, MessageBoxImage.Error),
            showInfo: (title, message) => _dialogs.ShowMessage(title, message, MessageBoxImage.Information),
            showImport: _dialogs.ShowSshConfigImport,
            shareIsWanted: root => !MayCloseAbandonedShare(root, _openShares.Keys, _shareBeingOpened));
        _dialogs.ShowConnectionManager(vm);

        if (vm.ConnectRequest is { } requested)
        {
            _ = ConnectAsync(requested, ActivePanel);
        }
    }

    /// <summary>Where a saved connection is open right now, for the manager's list; null when nowhere.</summary>
    private string? DescribeWhereOpen(ConnectionInfo info)
    {
        var places = new List<string>();
        foreach (var (panel, side) in new[] { (_shell.LeftPanel, "Conn_OpenLeft"), (_shell.RightPanel, "Conn_OpenRight") })
        {
            var tabs = panel.OpenConnections().Count(c => c.Connection.Id == info.Id);
            if (tabs > 0 || (info.IsSmb && ShareOf(panel) is { } share && share.Id == info.Id))
            {
                places.Add(LocalizationManager.Get(side));
            }
        }

        return places.Count == 0 ? null : string.Join(", ", places);
    }

    /// <summary>Opens <paramref name="info"/> in the active panel: a panel's connection drop-down, the manager.</summary>
    [RelayCommand]
    private Task Connect(ConnectionInfo? info) => info is null ? Task.CompletedTask : ConnectAsync(info, ActivePanel);

    /// <summary>
    /// Opens a connection in <paramref name="panel"/>. A connection already open in one of its tabs is
    /// shown there rather than opened twice; a different server than the one on screen gets a tab of its
    /// own, so connecting never silently drops another connection (ADR-0032).
    /// </summary>
    /// <param name="startPath">Where to open it; the connection's own folder when null.</param>
    /// <param name="inCurrentTab">Replace whatever the tab on screen shows: Reconnect, a duplicated tab.</param>
    public async Task ConnectAsync(ConnectionInfo info, PanelViewModel panel, string? startPath = null, bool inCurrentTab = false)
    {
        if (_connecting is not null)
        {
            return;   // one at a time: the chip says which and offers to cancel it
        }

        if (!inCurrentTab && startPath is null
            && panel.OpenConnections().FirstOrDefault(c => c.Connection.Id == info.Id) is { Tab: { } openTab } open)
        {
            if (!openTab.IsActive)
            {
                panel.SwitchTabCommand.Execute(openTab);
            }

            // Edited since it was opened (another folder, read-only, a new password): open it again in
            // its tab with what was saved, rather than show the old session as if it were the new one.
            if (!SameSettings(open.Connection, info))
            {
                await ConnectAsync(info, panel, startPath: null, inCurrentTab: true);
            }

            return;
        }

        if (!await ConfirmCredentialsAsync(info))
        {
            return;
        }

        if (info.IsSmb)
        {
            await OpenShareAsync(info, panel, startPath);
            return;
        }

        using var cts = new CancellationTokenSource();
        _connecting = cts;
        panel.ConnectingTo = info;
        info.SecretsChanged = PersistSecrets;
        IVirtualFileSystem? vfs = null;
        try
        {
            Mouse.OverrideCursor = Cursors.AppStarting;

            var root = string.IsNullOrEmpty(startPath ?? info.RemotePath) ? "/" : startPath ?? info.RemotePath;
            vfs = await ConnectWait.UntilCancelledAsync(
                OpenProvenAsync(info, panel, root, cts.Token), cts.Token, opened => opened.Dispose());

            // The previous place is left only now that the new one works: a failed connect must not
            // also cost what was open. Another server keeps its tab, the new one gets the next.
            var fileList = panel.FileList;
            if (!inCurrentTab && fileList.ActiveConnection is not null)
            {
                panel.NewTabCommand.Execute(null);
            }
            else if (fileList.IsMounted)
            {
                fileList.ReturnToDisk();
            }

            fileList.Mount(vfs, root, info, MountKind.Remote);
            vfs = null;   // the panel owns it now
            _logger.LogInformation("Connected to {Address}", info.DescribeAddress(root));
            RememberUse(info);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            _logger.LogInformation("Connecting to {Host} was cancelled", info.Host);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to connect to {Host}", info.Host);
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_Failed"),
                string.Format(LocalizationManager.Get("Conn_FailedMessage"), info.Name, info.DescribeAddress("/"),
                    NetworkErrorText.Describe(ex)));
        }
        finally
        {
            vfs?.Dispose();
            Mouse.OverrideCursor = null;
            panel.IsSigningIn = false;
            panel.ConnectingTo = null;
            _connecting = null;
        }
    }

    /// <summary>
    /// A fresh file system for a connect, proven by one stat. It owns the file system until it returns
    /// it, and a failure closes it here, also long after the user cancelled (<see cref="ConnectWait"/>).
    /// </summary>
    private static async Task<IVirtualFileSystem> OpenProvenAsync(
        ConnectionInfo info, PanelViewModel panel, string root, CancellationToken ct)
    {
        IVirtualFileSystem? vfs = null;
        try
        {
            // Off the UI thread: an SFTP key file is parsed and decrypted here (INV-06).
            vfs = await Task.Run(() => NetworkPlugin.CreateFileSystem(info), ct);
            ct.ThrowIfCancellationRequested();   // nothing waits for this step any more: no browser after a Cancel

            // The first connect of a drive is a sign-in in the browser; nothing else opens one.
            if (vfs is IInteractiveSignIn { NeedsSignIn: true } signIn)
            {
                panel.IsSigningIn = true;
                Mouse.OverrideCursor = null;
                await signIn.SignInAsync(ct);
                panel.IsSigningIn = false;
            }

            if (info.ReadOnly)
            {
                vfs = new ReadOnlyFileSystem(vfs);
            }

            // Fail here rather than leaving a dead panel mounted. One stat round trip is
            // enough — Mount sets CurrentPath, and that load does the actual listing.
            if (await vfs.GetEntryAsync(new VfsPath(root), ct) is null)
            {
                throw new DirectoryNotFoundException(
                    string.Format(LocalizationManager.Get("Conn_DirNotFound"), root));
            }

            return vfs;
        }
        catch
        {
            vfs?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// What has to be settled before connecting: a password that would cross the network readable is
    /// the user's call (asked knowingly, once per address in a run), one this Windows account cannot decrypt is asked for again,
    /// and a drive needs the client id of an application registered with its provider.
    /// </summary>
    private async Task<bool> ConfirmCredentialsAsync(ConnectionInfo info)
    {
        if (info.SendsCredentialsInClear
            && !await _clearTextConsents.AskOnceAsync(info, () => _dialogs.ConfirmAsync(
                LocalizationManager.Get("Conn_ClearTextTitle"),
                string.Format(LocalizationManager.Get("Conn_ClearTextConfirm"), info.Name))))
        {
            return false;
        }

        if (info.IsOAuth && string.IsNullOrWhiteSpace(info.ClientId))
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_Failed"),
                string.Format(LocalizationManager.Get("Conn_NeedsClientId"), info.Name));
            return false;
        }

        // The book holds an encrypted password this Windows account cannot open (the folder was
        // copied from another PC). Ask for it now rather than connect with an empty one.
        return !info.PasswordUnreadable || await AskForUnreadableSecretAsync(info);
    }

    /// <summary>
    /// Writes a secret a file system changed — the refresh token of a sign-in or of a rotation — into
    /// the book. Only the secret: the user may have edited the connection meanwhile.
    /// </summary>
    private void PersistSecrets(ConnectionInfo changed)
    {
        var id = changed.Id;
        var token = changed.RefreshToken;
        var account = changed.Username;
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_connectionBook.Connections.FirstOrDefault(c => c.Id == id) is not { } saved)
            {
                return;   // a typed address that was never saved
            }

            saved.RefreshToken = token;
            if (string.IsNullOrEmpty(saved.Username))
            {
                saved.Username = account;
            }

            try
            {
                _connectionBook.Update(saved);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save the new sign-in of {Name}", saved.Name);
            }
        });
    }

    /// <summary>Prompts for the password (or key passphrase) that could not be decrypted. False = the user backed out.</summary>
    private async Task<bool> AskForUnreadableSecretAsync(ConnectionInfo info)
    {
        var typed = await _dialogs.ShowPasswordDialogAsync(info.Name);
        if (typed is null)
        {
            return false;
        }

        if (info.Protocol == NetworkProtocol.Sftp && info.AuthMethod == SshAuthMethod.KeyFile)
        {
            info.KeyFilePassphrase = typed;
        }
        else if (info.IsOAuth)
        {
            // A drive has no password: what could not be decrypted is the client secret (Google's).
            info.ClientSecret = typed;
        }
        else
        {
            info.Password = typed;
        }

        // For this session only: it is saved when the user edits the connection.
        info.PasswordUnreadable = false;
        return true;
    }

    /// <summary>Stops the connect in progress — a slow server, a sign-in left waiting in the browser.</summary>
    [RelayCommand]
    private void CancelConnect() => _connecting?.Cancel();

    /// <summary>
    /// Opens a connection address typed into the path bar or Quick connect: <c>sftp://me@host/var/log</c>,
    /// <c>s3://bucket/prefix</c>. A saved connection to the same place lends its credentials; a new
    /// object store or drive has nothing to lend and opens in the manager, filled from the address.
    /// False when the text is no connection address.
    /// </summary>
    public async Task<bool> OpenAddressAsync(string text, PanelViewModel panel)
    {
        if (!ConnectionUrl.TryParse(text, out var parsed, out var path))
        {
            return false;
        }

        if (ConnectionUrl.FindSaved(_connectionBook.Connections, parsed, ref path) is { } saved)
        {
            await ConnectAsync(saved, panel, path);
            return true;
        }

        // Keys, a client id, a share's account: an address does not carry them, the editor asks.
        if (parsed.IsObjectStorage || parsed.IsOAuth || parsed.IsSmb)
        {
            ShowConnectionManager(select: null, address: text);
            return true;
        }

        // A user without a password: asked now, and not kept (Save in the chip's menu keeps it).
        if (parsed.Username.Length > 0 && parsed.Password is null)
        {
            parsed.Password = await _dialogs.ShowPasswordDialogAsync(parsed.DescribeAddress("/"));
            if (parsed.Password is null)
            {
                return true;
            }
        }

        await ConnectAsync(parsed, panel, path);
        return true;
    }

    /// <summary>Quick connect — open a server by its address, without saving it first.</summary>
    [RelayCommand]
    private async Task QuickConnect()
    {
        var panel = ActivePanel;
        // Tolerates a clipboard another program holds open: a COMException here would end the command.
        var clip = FileCommands.TryGetClipboardText()?.Trim() ?? string.Empty;
        var typed = await _dialogs.ShowInputAsync(
            LocalizationManager.Get("Conn_QuickTitle"),
            LocalizationManager.Get("Conn_QuickPrompt"),
            ConnectionUrl.TryParse(clip, out _, out _) ? clip : string.Empty);
        if (string.IsNullOrWhiteSpace(typed))
        {
            return;
        }

        if (!await OpenAddressAsync(typed, panel))
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_QuickTitle"),
                string.Format(LocalizationManager.Get("Conn_QuickNotAnAddress"), typed));
        }
    }

    /// <summary>Ctrl+Alt+F — close the connection of the active panel's tab, or stop the connect in progress.</summary>
    [RelayCommand]
    private async Task Disconnect()
    {
        if (ActivePanel.ConnectingTo is not null)
        {
            _connecting?.Cancel();
            return;
        }

        if (ActivePanel.FileList.LeaveConnection())
        {
            return;
        }

        if (ShareOf(ActivePanel) is { } share)
        {
            await CloseShareAsync(share);
            return;
        }

        // With only one server open, there is no doubt which connection is meant.
        if (OppositePanel.FileList.LeaveConnection())
        {
            return;
        }

        // Pressed with no server open, it used to do nothing at all.
        await _dialogs.ShowInfoAsync(
            Controls.MenuHeaderText.Plain(LocalizationManager.Get("Menu_Disconnect")),
            LocalizationManager.Get("Conn_NotConnectedHere"));
    }

    /// <summary>
    /// Opens the active panel's connection again where it stood: after a dropped session, a changed
    /// password, a server that came back. The old one is closed only once the new one works.
    /// </summary>
    [RelayCommand]
    private Task Reconnect()
    {
        var panel = ActivePanel;

        // Already reconnecting by itself (6.82): Reconnect means "now", not a second connect beside it.
        if (RetryReconnectNow(panel))
        {
            return Task.CompletedTask;
        }

        if (panel.FileList.ActiveConnection is { } open)
        {
            // What is saved now: the user may have reconnected precisely because they changed it.
            var info = Saved(open);
            return ConnectAsync(info, panel, panel.FileList.ActiveConnectionPath ?? info.RemotePath, inCurrentTab: true);
        }

        if (ShareOf(panel) is { } share)
        {
            return ConnectAsync(share, panel, startPath: null, inCurrentTab: true);
        }

        return _dialogs.ShowInfoAsync(
            Controls.MenuHeaderText.Plain(LocalizationManager.Get("Menu_Reconnect")),
            LocalizationManager.Get("Conn_NothingToReconnect"));
    }

    /// <summary>Opens a second connection to a server in the tab on screen: duplicating a tab on a server.</summary>
    public Task OpenInActiveTabAsync(PanelViewModel panel, ConnectionInfo info, string path) =>
        ConnectAsync(Saved(info), panel, path, inCurrentTab: true);

    /// <summary>The book's current copy of a connection that is open, or the open one if it was never saved.</summary>
    private ConnectionInfo Saved(ConnectionInfo open) =>
        _connectionBook.Connections.FirstOrDefault(c => c.Id == open.Id) ?? open;

    /// <summary>Whether two copies of a connection would open the same session; its name and colour do not count.</summary>
    public static bool SameSettings(ConnectionInfo a, ConnectionInfo b) =>
        a.Protocol == b.Protocol && a.Host == b.Host && a.Port == b.Port && a.Username == b.Username
        && a.Password == b.Password && a.RemotePath == b.RemotePath && a.AuthMethod == b.AuthMethod
        && a.KeyFilePath == b.KeyFilePath && a.KeyFilePassphrase == b.KeyFilePassphrase
        && a.Region == b.Region && a.Bucket == b.Bucket && a.PathStyle == b.PathStyle && a.UseHttp == b.UseHttp
        && a.Profile == b.Profile && a.ClientId == b.ClientId && a.ClientSecret == b.ClientSecret
        && a.ReadOnly == b.ReadOnly;

    /// <summary>Saves a connection opened from a typed address, so it is one click away next time.</summary>
    public void SaveConnection(ConnectionInfo info)
    {
        if (_connectionBook.Connections.Any(c => c.Id == info.Id))
        {
            ShowConnectionManager(info);
            return;
        }

        var copy = info.Clone();
        if (string.IsNullOrWhiteSpace(copy.Name))
        {
            copy.Name = PanelViewModel.ConnectionTitle(info);
        }

        try
        {
            _connectionBook.Add(copy);
            info.Id = copy.Id;
            info.Name = copy.Name;
            _shell.ShowNotice(string.Format(LocalizationManager.Get("Conn_Saved"), copy.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _ = _dialogs.ShowErrorAsync(LocalizationManager.Get("ConnMgr_Title"),
                string.Format(LocalizationManager.Get("ConnMgr_SaveFailed"), ex.Message));
        }
    }

    /// <summary>True when <paramref name="info"/> is in the address book (else the chip offers to save it).</summary>
    public bool IsSaved(ConnectionInfo info) => _connectionBook.Connections.Any(c => c.Id == info.Id);

    /// <summary>Closes a connection in every tab of both panels that has it: the manager's Disconnect.</summary>
    private void CloseEverywhere(Guid id)
    {
        foreach (var panel in new[] { _shell.LeftPanel, _shell.RightPanel })
        {
            panel.CloseConnection(id);
            if (ShareOf(panel) is { } share && share.Id == id)
            {
                _ = CloseShareAsync(share);
            }
        }
    }

    /// <summary>Closes every connection: the window is closing. Shares opened here are released too.</summary>
    public void CloseAll()
    {
        _connecting?.Cancel();
        StopAllReconnects();
        StopKeepAlive();
        _shell.LeftPanel.CloseAllMounts();
        _shell.RightPanel.CloseAllMounts();
        foreach (var root in _openShares.Keys.ToList())
        {
            try
            {
                FileSystem.Native.Mpr.Disconnect(root);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogDebug(ex, "Share {Root} stays connected", root);
            }
        }

        _openShares.Clear();
    }

    // ── Windows shares (SMB) ──────────────────────────────────────────────────

    /// <summary>Shares this window opened, by their <c>\\host\share</c> root. Not a mount: the panel reads them as disk paths.</summary>
    private readonly Dictionary<string, ConnectionInfo> _openShares = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The <c>\\host\share</c> root of the connect in progress, if it is a share's.</summary>
    private string? _shareBeingOpened;

    /// <summary>How long closing a share waits for the panels that left it to let go: a dead share can hold them up.</summary>
    private static readonly TimeSpan ShareReleaseWait = TimeSpan.FromSeconds(3);

    /// <summary>The saved share the panel stands in, or null.</summary>
    public ConnectionInfo? ShareOf(PanelViewModel panel) => ShareAt(panel.CurrentPath);

    private ConnectionInfo? ShareAt(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal)
            ? _openShares.FirstOrDefault(s => path.StartsWith(s.Key, StringComparison.OrdinalIgnoreCase)
                && (path.Length == s.Key.Length || path[s.Key.Length] == '\\')).Value
            : null;

    /// <summary>
    /// <c>\\host\share</c> and the folder in it a share connection opens: the first segment of its
    /// folder is the share. Null when no share is named.
    /// </summary>
    public static (string Root, string Folder)? SharePaths(ConnectionInfo info, string? startPath)
    {
        var segments = (startPath ?? info.RemotePath).Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || string.IsNullOrWhiteSpace(info.Host))
        {
            return null;
        }

        var root = $@"\\{info.Host.Trim().TrimStart('\\')}\{segments[0]}";
        return (root, segments.Length == 1 ? root : root + @"\" + string.Join('\\', segments.Skip(1)));
    }

    private async Task OpenShareAsync(ConnectionInfo info, PanelViewModel panel, string? startPath)
    {
        if (SharePaths(info, startPath) is not { } paths)
        {
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_Failed"),
                string.Format(LocalizationManager.Get("Conn_NeedsShare"), info.Name));
            return;
        }

        var (root, folder) = paths;

        using var cts = new CancellationTokenSource();
        _connecting = cts;
        _shareBeingOpened = root;
        panel.ConnectingTo = info;
        var wasOpen = _openShares.ContainsKey(root);
        var connected = false;
        var entered = false;
        try
        {
            // Both block for as long as the server takes to answer (INV-06), and Cancel does not wait for
            // them. A share the abandoned connect still opens is closed again, unless it is wanted by then.
            await ConnectWait.UntilCancelledAsync(
                Task.Run(() =>
                {
                    FileSystem.Native.Mpr.Connect(root, info.Username, info.Password);
                    return root;
                }, cts.Token),
                cts.Token,
                opened => Application.Current?.Dispatcher.InvokeAsync(() => CloseAbandonedShare(opened)));
            connected = true;
            _openShares[root] = info;
            if (!await ConnectWait.UntilCancelledAsync(Task.Run(() => Directory.Exists(folder), cts.Token), cts.Token))
            {
                throw new DirectoryNotFoundException(string.Format(LocalizationManager.Get("Conn_DirNotFound"), folder));
            }

            if (panel.FileList.ActiveConnection is not null)
            {
                panel.NewTabCommand.Execute(null);
            }
            else if (panel.FileList.IsMounted)
            {
                panel.FileList.ReturnToDisk();
            }

            panel.CurrentPath = folder;
            entered = true;
            RememberUse(info);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open the share {Root}", root);
            await _dialogs.ShowErrorAsync(LocalizationManager.Get("Conn_Failed"),
                string.Format(LocalizationManager.Get("Conn_FailedMessage"), info.Name, root, ex.Message));
        }
        finally
        {
            panel.ConnectingTo = null;
            _connecting = null;
            _shareBeingOpened = null;

            // Opened and then not entered (Cancel, no such folder): no panel stands in it, so nothing
            // would name it or offer to close it, and it stayed open under the saved account until exit.
            if (connected && !entered && !wasOpen)
            {
                _openShares.Remove(root);
                CloseAbandonedShare(root);
            }

            panel.RefreshConnectionChip();
        }
    }

    /// <summary>
    /// Whether a share that a connect opened and did not use may be closed. Not when it is wanted by now:
    /// after a Cancel the user often asks again at once, and the connect given up on, coming back later,
    /// closed the connection the new one stands on (the panel then lost its account).
    /// </summary>
    public static bool MayCloseAbandonedShare(string root, IEnumerable<string> openShares, string? beingOpened) =>
        !openShares.Contains(root, StringComparer.OrdinalIgnoreCase)
        && !string.Equals(root, beingOpened, StringComparison.OrdinalIgnoreCase);

    /// <summary>Closes a share a connect opened and did not use, off the UI thread (INV-06). Called on the UI thread.</summary>
    private void CloseAbandonedShare(string root)
    {
        if (!MayCloseAbandonedShare(root, _openShares.Keys, _shareBeingOpened))
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                FileSystem.Native.Mpr.Disconnect(root);
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                _logger.LogDebug(ex, "Share {Root} stays connected", root);
            }
        });
    }

    /// <summary>
    /// Releases a share: every panel standing in it goes home first — its folder watcher holds the
    /// share open, and Windows refuses to close a connection that is in use. The watcher closes in the
    /// pool, so the close waits for it, though not for long.
    /// </summary>
    private async Task CloseShareAsync(ConnectionInfo share)
    {
        var root = _openShares.FirstOrDefault(s => s.Value.Id == share.Id).Key;
        if (root is null)
        {
            return;
        }

        foreach (var panel in new[] { _shell.LeftPanel, _shell.RightPanel })
        {
            if (ShareOf(panel)?.Id == share.Id)
            {
                panel.CurrentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        await Task.WhenAny(
            Task.WhenAll(_shell.LeftPanel.FolderWatcherReleased, _shell.RightPanel.FolderWatcherReleased),
            Task.Delay(ShareReleaseWait));

        try
        {
            await Task.Run(() => FileSystem.Native.Mpr.Disconnect(root));
            _openShares.Remove(root);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            await _dialogs.ShowErrorAsync(Controls.MenuHeaderText.Plain(LocalizationManager.Get("Menu_Disconnect")),
                string.Format(LocalizationManager.Get("Conn_ShareInUse"), root, ex.Message));
        }

        _shell.LeftPanel.RefreshConnectionChip();
        _shell.RightPanel.RefreshConnectionChip();
    }

    // ── Background transfer queue ─────────────────────────────────────────────

    /// <summary>
    /// Shift+F5 — queue the selection for a resumable background transfer instead of
    /// copying it inline. One panel must be an open FTP/SFTP/WebDAV connection. A folder goes in with
    /// everything under it (<see cref="TransferPlanner"/>). Concurrent: a second press during a long walk
    /// finds that walk's window, below.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task QueueTransfer()
    {
        var selected = GetSelectedOrCurrent();
        if (selected.Count == 0)
        {
            return;
        }

        var title = LocalizationManager.Get("Transfer_Title");
        var isDownload = ActivePanel.FileList.IsRemote;
        var remotePanel = isDownload ? ActivePanel : OppositePanel;
        var localPanel = isDownload ? OppositePanel : ActivePanel;

        if (remotePanel.FileList.RemoteConnection is not { } connection)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Transfer_NeedRemote"));
            return;
        }

        if (!isDownload && connection.ReadOnly)
        {
            await _dialogs.ShowErrorAsync(title,
                string.Format(LocalizationManager.Get("Conn_ReadOnlyRefused"), PanelViewModel.ConnectionTitle(connection)));
            return;
        }

        // The queue moves bytes between one server and the local disk: it opens its own
        // connection for the remote side and a plain FileStream for the other. A second mount
        // on the local side would be handed to that FileStream as if it were a disk path.
        if (localPanel.FileList.IsMounted)
        {
            await _dialogs.ShowErrorAsync(title, LocalizationManager.Get("Transfer_LocalOnly"));
            return;
        }

        // A second press while the first still walks its folders finds that walk's window: queued
        // twice, the second copy of every file would fail on the first one's result.
        if (_transferPlanning)
        {
            _transferPlanWindow?.BringBack();
            return;
        }

        // All of it taken now: the panels are free while the folders are walked (TRAP-92).
        var targetRoot = OppositePanel.CurrentPath;
        var targetVfs = OppositePanel.FileList.ActiveVfs;
        var sourceVfs = ActivePanel.FileList.ActiveVfs;
        TransferPlan plan;

        _transferPlanning = true;
        try
        {
            using (_shell.LeasePanelFileSystems())
            {
                var planned = await PlanTransferAsync(selected, isDownload, sourceVfs, targetVfs, targetRoot);
                if (planned is null)
                {
                    return;
                }

                plan = planned;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A server that refuses the walk (a changed host key, TRAP-43): say so instead of letting
            // the exception escape the command.
            _logger.LogWarning(ex, "Planning transfers stopped");
            await _dialogs.ShowErrorAsync(title, NetworkErrorText.Describe(ex));
            return;
        }
        finally
        {
            _transferPlanning = false;
            _transferPlanWindow = null;
        }

        if (plan.Stopped)
        {
            return;
        }

        if (plan.Steps.Count > 0)
        {
            _transferQueue.EnqueueRange(plan.Steps.Select(step => new TransferItem
            {
                SourcePath = step.SourcePath,
                DestinationPath = step.DestinationPath,
                TotalBytes = step.IsFolder ? 0 : step.Size,
                Connection = connection,
                Direction = isDownload ? TransferDirection.Download : TransferDirection.Upload,
                OverwriteExisting = step.Overwrite,
                IsFolder = step.IsFolder,
                DisplayPath = step.Display,
            }).ToList());

            _logger.LogInformation("Queued {Count} {Direction} transfer(s)", plan.FileCount,
                isDownload ? "download" : "upload");
            OpenTransferQueue();
        }

        // Whatever did not go in is said, not lost: a folder that looks copied and is not is how files go missing.
        if (plan.Skips.Count > 0)
        {
            await _dialogs.ShowInfoAsync(title, DescribeSkips(plan.Skips));
        }
    }

    private bool _transferPlanning;
    private ProgressDialogViewModel? _transferPlanWindow;

    /// <summary>A walk that takes longer than this gets a window: found so far, Cancel, «In background» (ADR-0038).</summary>
    private static readonly TimeSpan PlanWindowAfter = TimeSpan.FromMilliseconds(400);

    /// <summary>Walks the selection off the UI thread (INV-06, TRAP-66). Null when the user stopped it.</summary>
    private async Task<TransferPlan?> PlanTransferAsync(
        IReadOnlyList<IFileEntry> selected, bool isDownload, IVirtualFileSystem sourceVfs,
        IVirtualFileSystem targetVfs, string targetRoot)
    {
        var planner = new TransferPlanner(
            isDownload, sourceVfs, targetRoot,
            (folder, ct) => ListTargetFolderAsync(targetVfs, folder, ct),
            (source, existing) => _dialogs.ShowConflictDialogAsync(source, existing));

        ProgressDialogViewModel? window = null;
        TransferPlanProgress? last = null;
        using var stop = new CancellationTokenSource();

        // Made here, on the UI thread: its reports come back to it.
        var progress = new Progress<TransferPlanProgress>(report =>
        {
            last = report;
            ShowFound(window, report);
        });

        var walk = Task.Run(() => planner.BuildAsync(selected, progress, stop.Token), stop.Token);
        if (await Task.WhenAny(walk, Task.Delay(PlanWindowAfter)) != walk)
        {
            window = _dialogs.ShowProgressDialog(LocalizationManager.Get("Transfer_Planning"));
            window.CanPause = false;
            window.IsIndeterminate = true;
            window.Cts.Token.Register(stop.Cancel);
            _transferPlanWindow = window;
            if (last is not null)
            {
                ShowFound(window, last);
            }
        }

        try
        {
            var plan = await walk;
            window?.Complete();
            return plan;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            window?.Dismiss();
            return null;
        }
        catch
        {
            window?.Dismiss();
            throw;
        }
    }

    private static void ShowFound(ProgressDialogViewModel? window, TransferPlanProgress report) =>
        window?.ReportCount(report.Folder, LocalizationManager.Format("Transfer_PlanningCount", report.Files));

    /// <summary>
    /// What is in a folder of the target, the disk or the server. A failure means "cannot tell" (SSH.NET and
    /// FluentFTP throw their own types, not IOException): the queue's own exists-check refuses to overwrite
    /// at transfer time. A changed host key is not "cannot tell": it stops the command.
    /// </summary>
    private static async Task<IReadOnlyList<IFileEntry>> ListTargetFolderAsync(
        IVirtualFileSystem targetVfs, string folder, CancellationToken ct)
    {
        try
        {
            return await targetVfs.ListAsync(new VfsPath(folder), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && NetworkErrors.Find<HostKeyChangedException>(ex) is null)
        {
            return [];
        }
    }

    /// <summary>What was left out, and why: the first few by name, the rest counted.</summary>
    private static string DescribeSkips(IReadOnlyList<TransferSkip> skips)
    {
        const int Shown = 8;
        var lines = skips.Take(Shown).Select(skip =>
            string.Format(LocalizationManager.Get("Transfer_Skip_" + skip.Reason), skip.Path, skip.Detail));
        var text = LocalizationManager.Format("Transfer_PlanSkipped", skips.Count)
                   + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines);
        return skips.Count > Shown
            ? text + Environment.NewLine + string.Format(LocalizationManager.Get("Op_MoreWarnings"), skips.Count - Shown)
            : text;
    }

    /// <summary>Shows the transfer queue window (pause / resume / cancel).</summary>
    [RelayCommand]
    private void OpenTransferQueue() => _dialogs.ShowTransferQueue(_transferQueue);
}
