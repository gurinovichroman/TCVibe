using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.App.Localization;
using TCV.Core.Domain;
using TCV.Network;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>One entry of the protocol picker.</summary>
public sealed record ProtocolOption(NetworkProtocol Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One entry of the SSH authentication picker.</summary>
public sealed record AuthMethodOption(SshAuthMethod Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The fields of one connection, shown in the right half of the connection manager. One editor for
/// every kind: a server, an object store, a drive signed in through the browser, a Windows share —
/// each shows the fields it needs, labelled in its own terms (an S3 "user" is an access key id).
/// </summary>
public partial class ConnectionEditorViewModel : ObservableObject
{
    private readonly Guid _id;
    private string? _refreshToken;
    private string _fingerprint = string.Empty;
    private bool _loading;

    /// <summary>A blank editor for a new connection.</summary>
    public ConnectionEditorViewModel()
        : this(new ConnectionInfo { Protocol = NetworkProtocol.Sftp, Port = 22 }, isNew: true)
    {
    }

    /// <summary>An editor on <paramref name="existing"/>; nothing changes in the book until it is saved.</summary>
    public ConnectionEditorViewModel(ConnectionInfo existing, bool isNew = false)
    {
        ArgumentNullException.ThrowIfNull(existing);
        IsNew = isNew;
        _id = existing.Id;
        Swatches = Workspaces.WorkspaceColor.Presets.Select(h => new WorkspaceColorSwatch(h)).ToList();
        Load(existing);
    }

    /// <summary>Not in the book yet: Save adds it.</summary>
    public bool IsNew { get; private set; }

    /// <summary>The connection's id: stable from the first save on, so tabs and the book agree.</summary>
    public Guid Id => _id;

    private void Load(ConnectionInfo existing)
    {
        _loading = true;
        ConnectionName = existing.Name;
        SelectedProtocol = ProtocolOptions.FirstOrDefault(p =>
            p.Value == (existing.IsImplicitFtps ? NetworkProtocol.FtpsImplicit : existing.Protocol)) ?? ProtocolOptions[0];
        Host = existing.Host;
        Port = existing.Port;
        Username = existing.Username;
        Password = existing.Password ?? string.Empty;
        PasswordUnreadable = existing.PasswordUnreadable;
        RemotePath = existing.RemotePath;
        SelectedAuthMethod = AuthOptions.First(a => a.Value == existing.AuthMethod);
        SshKeyPath = existing.KeyFilePath ?? string.Empty;
        SshKeyPassphrase = existing.KeyFilePassphrase ?? string.Empty;
        Region = existing.Region ?? string.Empty;
        Bucket = existing.Bucket ?? string.Empty;
        PathStyle = existing.PathStyle;
        UseHttp = existing.UseHttp;
        Profile = existing.Profile ?? string.Empty;
        ClientId = existing.ClientId ?? string.Empty;
        ClientSecret = existing.ClientSecret ?? string.Empty;
        ReadOnly = existing.ReadOnly;
        ColorHex = existing.Color ?? string.Empty;
        _refreshToken = existing.RefreshToken;
        SelectedPreset = S3Presets.Find(existing.Provider);
        _loading = false;
        _fingerprint = Fingerprint();
        RefreshDerived();
    }

    // ── Pickers ───────────────────────────────────────────────────────────────

    /// <summary>Every kind of connection, the ones a developer reaches for most first.</summary>
    public IReadOnlyList<ProtocolOption> ProtocolOptions { get; } =
    [
        Option(NetworkProtocol.Sftp),
        Option(NetworkProtocol.Ftp),
        Option(NetworkProtocol.Ftps),
        Option(NetworkProtocol.FtpsImplicit),
        Option(NetworkProtocol.WebDavs),
        Option(NetworkProtocol.WebDav),
        Option(NetworkProtocol.S3),
        Option(NetworkProtocol.AzureBlob),
        Option(NetworkProtocol.GoogleDrive),
        Option(NetworkProtocol.Dropbox),
        Option(NetworkProtocol.OneDrive),
        Option(NetworkProtocol.Smb),
    ];

    private static ProtocolOption Option(NetworkProtocol p) => new(p, LocalizationManager.Get("Conn_Protocol_" + p));

    public IReadOnlyList<AuthMethodOption> AuthOptions { get; } =
    [
        new(SshAuthMethod.Password, LocalizationManager.Get("Conn_Auth_Password")),
        new(SshAuthMethod.KeyFile, LocalizationManager.Get("Conn_Auth_KeyFile")),
    ];

    /// <summary>S3-compatible providers: picking one fills the endpoint, the region and the addressing.</summary>
    public IReadOnlyList<S3Preset> Presets => S3Presets.All;

    public IReadOnlyList<WorkspaceColorSwatch> Swatches { get; }

    // ── Fields ────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _connectionName = string.Empty;
    [ObservableProperty] private ProtocolOption _selectedProtocol = null!;
    [ObservableProperty] private string _host = string.Empty;
    [ObservableProperty] private int _port = 22;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _remotePath = "/";
    [ObservableProperty] private AuthMethodOption _selectedAuthMethod = null!;
    [ObservableProperty] private string _sshKeyPath = string.Empty;
    [ObservableProperty] private string _sshKeyPassphrase = string.Empty;
    [ObservableProperty] private string _region = string.Empty;
    [ObservableProperty] private string _bucket = string.Empty;
    [ObservableProperty] private bool _pathStyle;
    [ObservableProperty] private bool _useHttp;
    [ObservableProperty] private string _profile = string.Empty;
    [ObservableProperty] private string _clientId = string.Empty;
    [ObservableProperty] private string _clientSecret = string.Empty;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private string _colorHex = string.Empty;
    [ObservableProperty] private S3Preset? _selectedPreset;

    /// <summary>The stored password could not be decrypted on this PC (DPAPI is per user and machine).</summary>
    [ObservableProperty] private bool _passwordUnreadable;

    /// <summary>An address pasted to fill the fields from: <c>sftp://me@host:2222/var/log</c>, <c>s3://bucket</c>.</summary>
    [ObservableProperty] private string _address = string.Empty;

    // ── Test state ────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isConnecting;
    [ObservableProperty] private string _connectionStatus = string.Empty;
    [ObservableProperty] private bool _connectionStatusIsError;

    public NetworkProtocol Protocol => SelectedProtocol.Value;

    public SshAuthMethod AuthMethod => SelectedAuthMethod?.Value ?? SshAuthMethod.Password;

    // ── What the protocol needs ───────────────────────────────────────────────

    private bool IsServer => Protocol is NetworkProtocol.Ftp or NetworkProtocol.Ftps or NetworkProtocol.FtpsImplicit
        or NetworkProtocol.Sftp or NetworkProtocol.WebDav or NetworkProtocol.WebDavs;

    public bool IsSftp => Protocol == NetworkProtocol.Sftp;

    public bool IsS3 => Protocol == NetworkProtocol.S3;

    public bool IsAzure => Protocol == NetworkProtocol.AzureBlob;

    public bool IsObjectStorage => IsS3 || IsAzure;

    public bool IsOAuth => Protocol is NetworkProtocol.GoogleDrive or NetworkProtocol.Dropbox or NetworkProtocol.OneDrive;

    public bool IsSmb => Protocol == NetworkProtocol.Smb;

    /// <summary>Host and port: every kind but a drive, which has one fixed address.</summary>
    public bool ShowHost => !IsOAuth;

    public bool ShowPort => IsServer || IsObjectStorage;

    /// <summary>A user (or a key id, an account name): not for a drive, whose account is the browser's sign-in.</summary>
    public bool ShowUser => !IsOAuth;

    /// <summary>A password box for everything except SFTP with a key file and a drive.</summary>
    public bool ShowPasswordField => !IsOAuth && !(IsSftp && AuthMethod == SshAuthMethod.KeyFile);

    /// <summary>Key file and passphrase apply to SFTP with key authentication only.</summary>
    public bool ShowKeyFields => IsSftp && AuthMethod == SshAuthMethod.KeyFile;

    /// <summary>How to register the application with this provider and which redirect address it needs.</summary>
    public string ClientIdHint => IsOAuth ? LocalizationManager.Get("Conn_ClientIdHint_" + Protocol) : string.Empty;

    /// <summary>Google wants a client secret even from a desktop app; Dropbox and OneDrive do not.</summary>
    public bool ShowClientSecret => Protocol == NetworkProtocol.GoogleDrive;

    /// <summary>Plain FTP and http WebDAV send the password unencrypted; say so next to the field.</summary>
    public bool ClearTextWarningVisible =>
        Protocol is NetworkProtocol.Ftp or NetworkProtocol.WebDav && !PasswordUnreadable;

    /// <summary>The drive has been signed in to: later connects need no browser.</summary>
    public bool IsSignedIn => IsOAuth && !string.IsNullOrEmpty(_refreshToken);

    public string SignInText => IsSignedIn
        ? string.Format(LocalizationManager.Get("Conn_SignedInAs"),
            string.IsNullOrEmpty(Username) ? LocalizationManager.Get("Conn_SignedInUnknown") : Username)
        : LocalizationManager.Get("Conn_SignInHint");

    public string HostLabel => LocalizationManager.Get(
        IsObjectStorage ? "Conn_Endpoint" : IsSmb ? "Conn_SmbServer" : "Conn_HostField");

    public string UserLabel => LocalizationManager.Get(
        IsS3 ? "Conn_AccessKey" : IsAzure ? "Conn_Account" : IsSmb ? "Conn_SmbUser" : "Conn_UserField");

    public string PasswordLabel => LocalizationManager.Get(
        IsS3 ? "Conn_SecretKey" : IsAzure ? "Conn_AzureKey" : "Conn_PasswordField");

    public string RemotePathLabel => LocalizationManager.Get(
        IsObjectStorage ? "Conn_Prefix" : IsSmb ? "Conn_SmbShare" : IsOAuth ? "Conn_DriveFolder" : "Conn_RemotePathField");

    public string BucketLabel => LocalizationManager.Get(IsAzure ? "Conn_Container" : "Conn_Bucket");

    /// <summary>Why Save is not available yet, or empty: a disabled button with no reason reads as broken.</summary>
    public string ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ConnectionName))
            {
                return LocalizationManager.Get("Conn_NeedName");
            }

            if (ShowHost && !IsAzure && string.IsNullOrWhiteSpace(Host))
            {
                return LocalizationManager.Get(IsObjectStorage ? "Conn_NeedEndpoint" : "Conn_NeedHost");
            }

            if (IsS3 && Host.Contains(AccountPlaceholder, StringComparison.Ordinal))
            {
                return LocalizationManager.Get("Conn_NeedAccountId");
            }

            if (ShowPort && Port is < 1 or > 65535)
            {
                return LocalizationManager.Get("Conn_NeedPort");
            }

            if (ShowKeyFields && string.IsNullOrWhiteSpace(SshKeyPath))
            {
                return LocalizationManager.Get("Conn_NeedKeyFile");
            }

            if (IsS3 && string.IsNullOrWhiteSpace(Profile)
                && (string.IsNullOrWhiteSpace(Username) || (string.IsNullOrEmpty(Password) && !PasswordUnreadable)))
            {
                return LocalizationManager.Get("Conn_NeedKeys");
            }

            if (IsAzure && (string.IsNullOrWhiteSpace(Username) || (string.IsNullOrEmpty(Password) && !PasswordUnreadable)))
            {
                return LocalizationManager.Get("Conn_NeedAzureKey");
            }

            if (IsOAuth && string.IsNullOrWhiteSpace(ClientId))
            {
                return LocalizationManager.Get("Conn_NeedClientIdField");
            }

            if (IsSmb && MountShareName() is null)
            {
                return LocalizationManager.Get("Conn_NeedShareField");
            }

            return string.Empty;
        }
    }

    public bool IsValid => ValidationMessage.Length == 0;

    /// <summary>Something differs from what the book holds (or the connection is new and has a name).</summary>
    public bool IsDirty => IsNew ? !string.IsNullOrWhiteSpace(ConnectionName) || !string.IsNullOrWhiteSpace(Host)
        : Fingerprint() != _fingerprint;

    private string? MountShareName() =>
        RemotePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();

    // ── Reactions ─────────────────────────────────────────────────────────────

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is nameof(IsDirty) or nameof(IsValid) or nameof(ValidationMessage)
                or nameof(ConnectionStatus) or nameof(ConnectionStatusIsError) or nameof(IsConnecting) or nameof(Address))
        {
            return;
        }

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised on every edit, so the manager can enable Save and mark the row.</summary>
    public event EventHandler? Changed;

    partial void OnSelectedProtocolChanged(ProtocolOption value)
    {
        // Follow the protocol's usual port, but only while the port is still some protocol's default —
        // a port the user typed on purpose (2222 for SSH) is not overwritten by picking another protocol.
        if (!_loading && IsDefaultPort(Port))
        {
            Port = ConnectionInfo.DefaultPortFor(value.Value);
        }

        if (!_loading && value.Value == NetworkProtocol.S3 && SelectedPreset is null && string.IsNullOrWhiteSpace(Host))
        {
            SelectedPreset = S3Presets.All.FirstOrDefault();
        }

        RefreshDerived();
        TestConnectionCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedAuthMethodChanged(AuthMethodOption value) => RefreshDerived();

    partial void OnPasswordUnreadableChanged(bool value) => OnPropertyChanged(nameof(ClearTextWarningVisible));

    partial void OnUseHttpChanged(bool value)
    {
        if (!_loading && IsObjectStorage && Port is 80 or 443)
        {
            Port = value ? 80 : 443;
        }
    }

    /// <summary>A preset fills the endpoint, region and addressing; an "{account}" in its host is left for the user.</summary>
    partial void OnSelectedPresetChanged(S3Preset? value)
    {
        OnPropertyChanged(nameof(PresetHint));
        if (_loading || value is null)
        {
            return;
        }

        var region = string.IsNullOrWhiteSpace(Region) || S3Presets.All.Any(p => p.DefaultRegion == Region)
            ? value.DefaultRegion
            : Region;
        Region = region;
        Host = value.BuildHost(region, value.NeedsAccount ? AccountPlaceholder : null);
        PathStyle = value.PathStyle;
        UseHttp = value.UseHttp;
        Port = value.Port > 0 ? value.Port : (value.UseHttp ? 80 : 443);
        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            ConnectionName = value.Name;
        }
    }

    partial void OnRegionChanged(string value)
    {
        // The endpoint of a preset carries the region (s3.eu-west-1.amazonaws.com): it follows.
        if (!_loading && SelectedPreset is { RegionInHost: true } preset && value.Length > 0)
        {
            Host = preset.BuildHost(value, account: null);
        }
    }

    /// <summary>What stands for the account id in an endpoint that needs one (Cloudflare R2) until it is typed.</summary>
    public const string AccountPlaceholder = "ACCOUNT_ID";

    /// <summary>Where the provider's keys and region are found, in the user's language.</summary>
    public string PresetHint => SelectedPreset is { } preset
        ? LocalizationManager.Get("S3Hint_" + preset.Id)
        : string.Empty;

    partial void OnColorHexChanged(string value)
    {
        foreach (var swatch in Swatches)
        {
            swatch.IsSelected = string.Equals(swatch.Hex, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    partial void OnUsernameChanged(string value) => OnPropertyChanged(nameof(SignInText));

    private static bool IsDefaultPort(int port) =>
        Enum.GetValues<NetworkProtocol>().Any(p => ConnectionInfo.DefaultPortFor(p) == port)
        || port is 9000 or 10000;

    private void RefreshDerived()
    {
        foreach (var name in new[]
                 {
                     nameof(Protocol), nameof(IsSftp), nameof(IsS3), nameof(IsAzure), nameof(IsObjectStorage),
                     nameof(IsOAuth), nameof(IsSmb), nameof(ShowHost), nameof(ShowPort), nameof(ShowUser),
                     nameof(ShowPasswordField), nameof(ShowKeyFields), nameof(ShowClientSecret),
                     nameof(ClearTextWarningVisible), nameof(IsSignedIn), nameof(SignInText), nameof(HostLabel),
                     nameof(UserLabel), nameof(PasswordLabel), nameof(RemotePathLabel), nameof(BucketLabel), nameof(ClientIdHint),
                 })
        {
            OnPropertyChanged(name);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>Fills the fields from <see cref="Address"/>; the name follows the host when there is none yet.</summary>
    [RelayCommand]
    private void ApplyAddress()
    {
        // An https address of a host with no known pattern is an endpoint when the editor is already on S3.
        var endpoint = false;
        if (!ConnectionUrl.TryParse(Address, out var parsed, out var path))
        {
            endpoint = IsS3 && ConnectionUrl.TryParseS3Endpoint(Address, out parsed, out path);
            if (!endpoint)
            {
                ConnectionStatus = string.Format(LocalizationManager.Get("Conn_QuickNotAnAddress"), Address);
                ConnectionStatusIsError = true;
                return;
            }
        }

        SelectedProtocol = ProtocolOptions.First(p => p.Value == parsed.Protocol);
        if (endpoint)
        {
            // The address says which store this is, so the provider is "other"; picking it names an unnamed
            // connection after the preset, and the name should follow the host instead.
            var unnamed = string.IsNullOrWhiteSpace(ConnectionName);
            SelectedPreset = S3Presets.Find(S3Presets.OtherId);
            if (unnamed)
            {
                ConnectionName = string.Empty;
            }

            UseHttp = parsed.UseHttp;
            PathStyle |= parsed.PathStyle;
        }

        if (parsed.Host.Length > 0)
        {
            Host = parsed.Host;
        }

        if (ShowPort && parsed.Port > 0)
        {
            Port = parsed.Port;
        }

        if (parsed.Username.Length > 0)
        {
            Username = parsed.Username;
        }

        if (parsed.Password is { Length: > 0 } password)
        {
            Password = password;
            PasswordTyped?.Invoke(this, EventArgs.Empty);
        }

        if (!string.IsNullOrEmpty(parsed.Bucket))
        {
            Bucket = parsed.Bucket;
        }

        if (!string.IsNullOrEmpty(parsed.Region))
        {
            Region = parsed.Region;
        }

        RemotePath = path;
        if (string.IsNullOrWhiteSpace(ConnectionName))
        {
            ConnectionName = parsed.Name.Length > 0 ? parsed.Name : parsed.DescribeAddress("/");
        }

        // The typed address stays in its box: it is what the fields were read from, and clearing it left
        // the user wondering whether the button had taken it.
        ConnectionStatus = string.Empty;
    }

    /// <summary>Raised when the password was set from code, so the view refreshes its password box.</summary>
    public event EventHandler? PasswordTyped;

    /// <summary>Forgets the drive's sign-in: the next connect asks the browser again (another account, revoked access).</summary>
    [RelayCommand]
    private void SignOut()
    {
        _refreshToken = null;
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(SignInText));
    }

    [RelayCommand]
    private void PickColor(string? hex) => ColorHex = hex ?? string.Empty;

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnection(CancellationToken ct)
    {
        IsConnecting = true;
        ConnectionStatus = LocalizationManager.Get(IsOAuth && !IsSignedIn ? "Chip_SigningIn" : "Conn_Connecting");
        ConnectionStatusIsError = false;

        try
        {
            await PingAsync(ct);
            ConnectionStatus = LocalizationManager.Get("Conn_TestOk");
            ConnectionStatusIsError = false;
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = LocalizationManager.Get("Conn_TestCancelled");
            ConnectionStatusIsError = true;
        }
        catch (Exception ex)
        {
            ConnectionStatus = string.Format(LocalizationManager.Get("Conn_TestFailed"), Services.NetworkErrorText.Describe(ex));
            ConnectionStatusIsError = true;
        }
        finally
        {
            IsConnecting = false;
        }
    }

    private bool CanTest() => IsValid && !IsConnecting;

    partial void OnIsConnectingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();

    /// <summary>
    /// Does what Connect does — the same file system, the same host-key and certificate checks, the
    /// browser sign-in of a drive, and the same look at the remote folder — then closes again. A test
    /// that only logged in said "works" about a server whose folder the real connect could not find.
    /// </summary>
    private async Task PingAsync(CancellationToken ct)
    {
        var conn = BuildConnectionInfo();
        if (conn.IsSmb)
        {
            if (MountCommands.SharePaths(conn, null) is not { } share)
            {
                throw new System.IO.DirectoryNotFoundException(LocalizationManager.Get("Conn_NeedShareField"));
            }

            await Task.Run(() => FileSystem.Native.Mpr.Connect(share.Root, conn.Username, conn.Password), ct);
            if (!await Task.Run(() => System.IO.Directory.Exists(share.Folder), ct))
            {
                throw new System.IO.DirectoryNotFoundException(
                    string.Format(LocalizationManager.Get("Conn_DirNotFound"), share.Folder));
            }

            return;
        }

        var root = string.IsNullOrEmpty(conn.RemotePath) ? "/" : conn.RemotePath;

        // Off the UI thread: an SFTP key file is parsed and decrypted while it is built (INV-06).
        var fs = await Task.Run(() => NetworkPlugin.CreateFileSystem(conn), ct);
        try
        {
            if (fs is IInteractiveSignIn { NeedsSignIn: true } signIn)
            {
                await signIn.SignInAsync(ct);
                _refreshToken = conn.RefreshToken;
                if (string.IsNullOrEmpty(Username) && !string.IsNullOrEmpty(conn.Username))
                {
                    Username = conn.Username;
                }

                OnPropertyChanged(nameof(IsSignedIn));
                OnPropertyChanged(nameof(SignInText));
            }

            if (await fs.GetEntryAsync(new VfsPath(root), ct) is not { IsDirectory: true })
            {
                throw new System.IO.DirectoryNotFoundException(
                    string.Format(LocalizationManager.Get("Conn_DirNotFound"), root));
            }
        }
        finally
        {
            fs.Dispose();
        }
    }

    // ── Result ────────────────────────────────────────────────────────────────

    /// <summary>Builds the <see cref="ConnectionInfo"/> from the current field values.</summary>
    public ConnectionInfo BuildConnectionInfo() => new()
    {
        Id = _id,
        Name = ConnectionName.Trim(),
        Protocol = Protocol,
        Host = Host.Trim(),
        Port = ShowPort ? Port : ConnectionInfo.DefaultPortFor(Protocol),
        Username = Username.Trim(),
        Password = Password,
        // Typing a new password clears the "unreadable" state; leaving it blank keeps the stored one.
        // A drive has no password box: the secret it keeps is the client secret, and typing that clears it.
        PasswordUnreadable = PasswordUnreadable && string.IsNullOrEmpty(IsOAuth ? ClientSecret : Password),
        RemotePath = string.IsNullOrWhiteSpace(RemotePath) ? "/" : RemotePath.Trim().Replace('\\', '/'),
        AuthMethod = AuthMethod,
        KeyFilePath = string.IsNullOrWhiteSpace(SshKeyPath) ? null : SshKeyPath.Trim(),
        KeyFilePassphrase = string.IsNullOrWhiteSpace(SshKeyPassphrase) ? null : SshKeyPassphrase,
        Region = NullIfBlank(Region),
        Bucket = NullIfBlank(Bucket)?.Trim('/'),
        PathStyle = PathStyle,
        UseHttp = UseHttp,
        Provider = IsS3 ? SelectedPreset?.Id : null,
        Profile = NullIfBlank(Profile),
        ClientId = NullIfBlank(ClientId),
        ClientSecret = NullIfBlank(ClientSecret),
        RefreshToken = _refreshToken,
        ReadOnly = ReadOnly,
        Color = NullIfBlank(ColorHex),
    };

    /// <summary>The book now holds what the editor shows: it is no longer new nor changed.</summary>
    public void MarkSaved()
    {
        IsNew = false;
        _fingerprint = Fingerprint();
        OnPropertyChanged(nameof(IsNew));
        OnPropertyChanged(nameof(IsDirty));
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private string Fingerprint()
    {
        var c = BuildConnectionInfo();
        return string.Join('\u001f', c.Name, c.Protocol, c.Host, c.Port, c.Username, c.Password, c.RemotePath,
            c.AuthMethod, c.KeyFilePath, c.KeyFilePassphrase, c.Region, c.Bucket, c.PathStyle, c.UseHttp,
            c.Provider, c.Profile, c.ClientId, c.ClientSecret, c.RefreshToken, c.ReadOnly, c.Color);
    }
}
