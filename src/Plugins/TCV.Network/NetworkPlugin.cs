using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;
using TCV.Network.Cloud;

namespace TCV.Network;

/// <summary>Plugin providing FTP/FTPS/SFTP, WebDAV, S3, Azure Blob and cloud-drive file system access.</summary>
public sealed class NetworkPlugin : IFileSystemPlugin
{
    public string Id => "tcv.network";
    public string Name => "Network";
    public Version Version => new(2, 0, 0);
    public string[] Schemes => ["ftp", "ftps", "sftp", "dav", "davs", "s3", "azblob", "gdrive", "dropbox", "onedrive"];

    public void Initialize(IPluginHost host) { }

    /// <summary>
    /// Creates a network VFS from a connection URI or named connection.
    /// <para>Supported formats:</para>
    /// <list type="bullet">
    ///   <item><c>ftp://user:pass@host:21/path</c></item>
    ///   <item><c>sftp://user@host/path</c> — with options["password"] or options["keyFile"]</item>
    /// </list>
    /// </summary>
    public IVirtualFileSystem Create(string rootPath, IReadOnlyDictionary<string, string>? options = null)
    {
        var conn = ParseConnectionInfo(rootPath, options);
        return CreateFileSystem(conn);
    }

    /// <summary>
    /// Creates the VFS for an already-resolved connection — the path taken by the connection
    /// manager, which has credentials and SSH key settings that a URI cannot carry.
    /// </summary>
    public static IVirtualFileSystem CreateFileSystem(ConnectionInfo conn)
    {
        ArgumentNullException.ThrowIfNull(conn);

        return conn.Protocol switch
        {
            NetworkProtocol.Sftp => new SftpFileSystem(conn),
            NetworkProtocol.WebDav or NetworkProtocol.WebDavs => new WebDavFileSystem(conn),
            NetworkProtocol.S3 => new S3FileSystem(conn),
            NetworkProtocol.AzureBlob => new AzureBlobFileSystem(conn),
            NetworkProtocol.GoogleDrive => new GoogleDriveFileSystem(conn),
            NetworkProtocol.Dropbox => new DropboxFileSystem(conn),
            NetworkProtocol.OneDrive => new OneDriveFileSystem(conn),
            NetworkProtocol.Smb => throw new NotSupportedException(
                "A Windows share is opened as a disk path, not as a file system of its own."),
            _ => new FtpFileSystem(conn),
        };
    }

    private static ConnectionInfo ParseConnectionInfo(
        string rootPath, IReadOnlyDictionary<string, string>? options)
    {
        if (!Uri.TryCreate(rootPath, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid network URI: {rootPath}", nameof(rootPath));

        // s3://bucket/key and its kin name a bucket or an account, not a host: read them as the path bar does.
        if (uri.Scheme.ToLowerInvariant() is "s3" or "azblob" or "gdrive" or "dropbox" or "onedrive"
            && ConnectionUrl.TryParse(rootPath, out var cloud, out var cloudPath))
        {
            cloud.RemotePath = cloudPath;
            return cloud;
        }

        var protocol = uri.Scheme.ToLowerInvariant() switch
        {
            "sftp" => NetworkProtocol.Sftp,
            "ftps" => uri.Port == ConnectionInfo.ImplicitFtpsPort ? NetworkProtocol.FtpsImplicit : NetworkProtocol.Ftps,
            "dav" => NetworkProtocol.WebDav,
            "davs" => NetworkProtocol.WebDavs,
            "s3" => NetworkProtocol.S3,
            "azblob" => NetworkProtocol.AzureBlob,
            // A URI carries no client id or token: such a drive asks for a sign-in, but it is not an FTP server.
            "gdrive" => NetworkProtocol.GoogleDrive,
            "dropbox" => NetworkProtocol.Dropbox,
            "onedrive" => NetworkProtocol.OneDrive,
            _ => NetworkProtocol.Ftp,
        };

        var info = new ConnectionInfo
        {
            Protocol = protocol,
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : new ConnectionInfo { Protocol = protocol }.DefaultPort(),
            Username = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]),
            Password = uri.UserInfo.Contains(':')
                ? Uri.UnescapeDataString(uri.UserInfo[(uri.UserInfo.IndexOf(':') + 1)..])
                : options?.GetValueOrDefault("password"),
            RemotePath = uri.AbsolutePath,
        };

        if (options is not null)
        {
            if (options.TryGetValue("password", out var pwd)) info.Password = pwd;
            if (options.TryGetValue("keyFile", out var key))
            {
                info.AuthMethod = SshAuthMethod.KeyFile;
                info.KeyFilePath = key;
            }
            if (options.TryGetValue("passphrase", out var pp)) info.KeyFilePassphrase = pp;
        }

        return info;
    }

}
