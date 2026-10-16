namespace TCV.Network;

/// <summary>
/// Remote protocols the network plugin speaks. The numeric values are stored in
/// <c>connections.json</c>, so members are only ever appended: <c>FtpsImplicit</c> came after
/// WebDAV, and an older file still reads correctly.
/// </summary>
public enum NetworkProtocol
{
    Ftp, Ftps, Sftp, WebDav, WebDavs, FtpsImplicit,

    /// <summary>Amazon S3 and every S3-compatible store (R2, B2, MinIO, Wasabi…): SigV4 over HTTP.</summary>
    S3,

    /// <summary>Azure Blob Storage: shared key or SAS token over HTTP.</summary>
    AzureBlob,

    /// <summary>Consumer drives signed in through the browser (OAuth 2 with PKCE).</summary>
    GoogleDrive, Dropbox, OneDrive,

    /// <summary>A Windows share (<c>\\host\share</c>) opened with saved credentials: a disk path, not a mount.</summary>
    Smb,
}

public enum SshAuthMethod { Password, KeyFile }

/// <summary>Stores all parameters needed to connect to a server or a cloud storage.</summary>
/// <remarks>
/// One shape for every protocol; each reads the fields it needs. For object storage
/// <see cref="Username"/> is the access key id (S3) or the account name (Azure) and
/// <see cref="Password"/> the secret key, the Azure account key or a SAS token.
/// </remarks>
public sealed class ConnectionInfo
{
    /// <summary>The port implicit FTPS listens on; explicit FTPS shares FTP's port 21.</summary>
    public const int ImplicitFtpsPort = 990;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public NetworkProtocol Protocol { get; set; } = NetworkProtocol.Ftp;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21;
    public string Username { get; set; } = string.Empty;

    // Password stored encrypted (DPAPI) in ConnectionBook; plain here during session
    public string? Password { get; set; }

    /// <summary>
    /// True when the book holds an encrypted password that this Windows account cannot decrypt —
    /// the folder was copied to another PC or user (DPAPI keys are per user). Never persisted.
    /// The password has to be typed again; the stored blob is kept until it is.
    /// </summary>
    public bool PasswordUnreadable { get; set; }

    // SFTP-specific
    public SshAuthMethod AuthMethod { get; set; } = SshAuthMethod.Password;
    public string? KeyFilePath { get; set; }
    public string? KeyFilePassphrase { get; set; }

    public string RemotePath { get; set; } = "/";

    // ── Object storage (S3, Azure Blob) ───────────────────────────────────────

    /// <summary>S3 signing region (<c>us-east-1</c>, <c>auto</c> for R2). Empty means <c>us-east-1</c>.</summary>
    public string? Region { get; set; }

    /// <summary>The S3 bucket or Azure container the connection is limited to; empty lists them all at the root.</summary>
    public string? Bucket { get; set; }

    /// <summary>S3: address the bucket in the path (<c>host/bucket/key</c>) instead of the host name.</summary>
    public bool PathStyle { get; set; }

    /// <summary>S3 and Azure: plain http to the endpoint (a local MinIO or Azurite), not https.</summary>
    public bool UseHttp { get; set; }

    /// <summary>
    /// The preset the editor filled the endpoint from (<c>aws</c>, <c>r2</c>, <c>b2</c>…). Only the
    /// editor reads it, to show the same preset again; the connection itself is the fields.
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>S3: a profile of <c>%USERPROFILE%\.aws\credentials</c> used instead of typed keys.</summary>
    public string? Profile { get; set; }

    // ── Browser sign-in (Google Drive, Dropbox, OneDrive) ─────────────────────

    /// <summary>The OAuth client id of the application registered with the provider.</summary>
    public string? ClientId { get; set; }

    /// <summary>The OAuth client secret, where the provider wants one for a desktop app (Google). Encrypted in the book.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// What the first browser sign-in earned; later connects use it instead of the browser.
    /// Encrypted in the book. A provider that rotates it sets the new one and raises
    /// <see cref="SecretsChanged"/>.
    /// </summary>
    public string? RefreshToken { get; set; }

    /// <summary>
    /// Raised (on any thread) by a file system that changed a secret of this connection — a
    /// refresh token after a sign-in or a rotation — so the owner can write it to the book.
    /// Never persisted.
    /// </summary>
    public Action<ConnectionInfo>? SecretsChanged { get; set; }

    // ── Presentation ──────────────────────────────────────────────────────────

    /// <summary>
    /// Colour tag of the connection (<c>#RRGGBB</c>) shown on its tab and chip: production in red
    /// tells itself apart from the test server at a glance. Empty for none.
    /// </summary>
    public string? Color { get; set; }

    /// <summary>
    /// Opened read-only: the panel can browse and copy from it, but nothing writes, renames or
    /// deletes there — neither the user by mistake nor an agent.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// When the connection was last opened (UTC), or null if never: the command palette offers the
    /// most recent ones (6.82). Kept by <see cref="ConnectionBook.MarkUsed"/>, not by the editor.
    /// </summary>
    public DateTime? LastUsed { get; set; }

    /// <summary>
    /// TLS with the handshake before the first byte (port 990). Connections saved by older
    /// versions have <c>Ftps</c> on port 990, which only ever worked as implicit TLS — those are
    /// recognised here instead of being migrated in the file.
    /// </summary>
    public bool IsImplicitFtps =>
        Protocol == NetworkProtocol.FtpsImplicit || (Protocol == NetworkProtocol.Ftps && Port == ImplicitFtpsPort);

    public bool IsFtps => Protocol is NetworkProtocol.Ftps or NetworkProtocol.FtpsImplicit;

    public bool IsWebDav => Protocol is NetworkProtocol.WebDav or NetworkProtocol.WebDavs;

    /// <summary>S3 or Azure Blob: buckets or containers of objects, addressed by key.</summary>
    public bool IsObjectStorage => Protocol is NetworkProtocol.S3 or NetworkProtocol.AzureBlob;

    /// <summary>A drive whose login is a browser sign-in rather than a typed password.</summary>
    public bool IsOAuth => Protocol is NetworkProtocol.GoogleDrive or NetworkProtocol.Dropbox or NetworkProtocol.OneDrive;

    /// <summary>A Windows share: opened as a disk path, so it is not mounted in a panel.</summary>
    public bool IsSmb => Protocol == NetworkProtocol.Smb;

    /// <summary>
    /// The password crosses the network readable: plain FTP, WebDAV over http, or a SAS token sent
    /// to Azure over http. S3 and an Azure account key only ever send a signature.
    /// </summary>
    public bool SendsCredentialsInClear =>
        (!string.IsNullOrEmpty(Username) && Protocol is NetworkProtocol.Ftp or NetworkProtocol.WebDav)
        || (Protocol == NetworkProtocol.AzureBlob && UseHttp && IsSasToken(Password));

    /// <summary>An Azure credential that is a SAS token (<c>sv=…&amp;sig=…</c>) rather than an account key.</summary>
    public static bool IsSasToken(string? secret) =>
        !string.IsNullOrEmpty(secret) && secret.Contains("sig=", StringComparison.OrdinalIgnoreCase);

    public string GetScheme() => Protocol switch
    {
        NetworkProtocol.Ftp  => "ftp",
        NetworkProtocol.Ftps => "ftps",
        NetworkProtocol.FtpsImplicit => "ftps",
        NetworkProtocol.Sftp => "sftp",
        NetworkProtocol.WebDav => "dav",
        NetworkProtocol.WebDavs => "davs",
        NetworkProtocol.S3 => "s3",
        NetworkProtocol.AzureBlob => "azblob",
        NetworkProtocol.GoogleDrive => "gdrive",
        NetworkProtocol.Dropbox => "dropbox",
        NetworkProtocol.OneDrive => "onedrive",
        NetworkProtocol.Smb => "smb",
        _ => "ftp",
    };

    public int DefaultPort() => Protocol switch
    {
        NetworkProtocol.Sftp => 22,
        NetworkProtocol.FtpsImplicit => ImplicitFtpsPort,
        NetworkProtocol.WebDav => 80,
        NetworkProtocol.WebDavs => 443,
        NetworkProtocol.S3 or NetworkProtocol.AzureBlob => UseHttp ? 80 : 443,
        NetworkProtocol.GoogleDrive or NetworkProtocol.Dropbox or NetworkProtocol.OneDrive => 443,
        NetworkProtocol.Smb => 445,
        _ => 21,
    };

    /// <summary>
    /// How a place on this connection is named to people — tooltips, the chip, the operation log:
    /// <c>sftp://user@host[:port]/path</c>, the port only when it is not the protocol's default and
    /// never the password. Object storage is named by its bucket (<c>s3://bucket/key</c>): the access
    /// key id is no user name. A drive by its account, a Windows share by its UNC path.
    /// </summary>
    public string DescribeAddress(string path)
    {
        path = string.IsNullOrEmpty(path) ? "/" : path;
        var tail = path == "/" ? string.Empty : path.StartsWith('/') ? path : "/" + path;
        var bucket = string.IsNullOrWhiteSpace(Bucket) ? string.Empty : "/" + Bucket.Trim('/');

        switch (Protocol)
        {
            case NetworkProtocol.S3:
                // Without a fixed bucket the path's first segment is the bucket: "/b/key" → s3://b/key.
                return bucket.Length + tail.Length == 0 ? "s3://" : "s3:/" + bucket + tail;
            case NetworkProtocol.AzureBlob:
                return $"azblob://{Username}{bucket}{tail}";
            case NetworkProtocol.GoogleDrive or NetworkProtocol.Dropbox or NetworkProtocol.OneDrive:
                return $"{GetScheme()}://{Username}{tail}";
            case NetworkProtocol.Smb:
                return @"\\" + Host + (tail.Length > 0 ? tail.Replace('/', '\\') : string.Empty);
        }

        var host = Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]" : Host;
        var user = string.IsNullOrEmpty(Username) ? string.Empty : Username + "@";
        var port = Port > 0 && Port != DefaultPort() ? $":{Port}" : string.Empty;
        return $"{GetScheme()}://{user}{host}{port}{(tail.Length > 0 ? tail : "/")}";
    }

    /// <summary>The default port for <paramref name="protocol"/> (the dialog switches ports with it).</summary>
    public static int DefaultPortFor(NetworkProtocol protocol) => new ConnectionInfo { Protocol = protocol }.DefaultPort();

    /// <summary>A field-by-field copy, secrets included; <see cref="SecretsChanged"/> is not carried over.</summary>
    public ConnectionInfo Clone()
    {
        var copy = (ConnectionInfo)MemberwiseClone();
        copy.SecretsChanged = null;
        return copy;
    }
}

/// <summary>
/// A file system whose login is an interactive browser sign-in (OAuth 2). The application asks for
/// it explicitly before the first listing; nothing else — the transfer queue, a reload — ever opens
/// a browser, they fail with <see cref="SignInRequiredException"/> instead.
/// </summary>
public interface IInteractiveSignIn
{
    /// <summary>True when there is no usable refresh token and the next request would need the browser.</summary>
    bool NeedsSignIn { get; }

    /// <summary>
    /// Opens the provider's sign-in page in the default browser and waits for it to come back to a
    /// loopback address. On success the refresh token is in the connection and
    /// <see cref="ConnectionInfo.SecretsChanged"/> has been raised.
    /// </summary>
    Task SignInAsync(CancellationToken ct);
}

/// <summary>The request needs a browser sign-in that was not done (or was revoked).</summary>
public sealed class SignInRequiredException(string provider)
    : InvalidOperationException($"Sign in to {provider} first.")
{
    public string Provider { get; } = provider;
}

/// <summary>
/// A remote file that can be read from an offset, for a resumable download. The validator (an ETag
/// or a date) makes sure the rest comes from the same version of the file as the part already here.
/// </summary>
public interface IRangeReader
{
    /// <summary>
    /// Opens <paramref name="path"/> at <paramref name="offset"/>, on condition that the file still is the
    /// one <paramref name="validator"/> describes. Returns the offset the stream really starts at — 0
    /// when the server sent the whole file (it ignored the range, or the file changed) — the full size
    /// when known (0 otherwise) and the validator of this response, or null when the server gave none.
    /// </summary>
    Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        TCV.Core.Domain.VfsPath path, long offset, string? validator, CancellationToken ct);
}
