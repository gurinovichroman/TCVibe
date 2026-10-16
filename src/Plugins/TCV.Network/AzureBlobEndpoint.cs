using System.Net;
using TCV.Core.Domain;

namespace TCV.Network;

/// <summary>
/// Where a VFS path lives in Blob Storage: the container (null at the root of an account whose
/// containers are listed there) and the blob name inside it (empty for the container itself).
/// </summary>
internal readonly record struct AzureBlobLocation(string? Container, string Blob)
{
    /// <summary>The account root, which lists containers.</summary>
    public bool IsServiceRoot => Container is null;

    /// <summary>A container's top level.</summary>
    public bool IsContainerRoot => Container is not null && Blob.Length == 0;

    /// <summary>The name prefix of everything inside this folder: <c>"dir/"</c>, or empty at a container's top.</summary>
    public string Prefix => Blob.Length == 0 ? string.Empty : Blob + "/";
}

/// <summary>
/// The addressing half of Azure Blob Storage: which URL a container or blob has on this connection,
/// and which container and blob a VFS path names. Pure, so the encoding rules are testable.
/// </summary>
/// <remarks>
/// The account normally is the host (<c>account.blob.core.windows.net</c>). The emulator, an IP
/// address or an explicit <see cref="ConnectionInfo.PathStyle"/> put it in the path instead:
/// <c>http://127.0.0.1:10000/devstoreaccount1/container/blob</c>.
/// </remarks>
internal sealed class AzureBlobEndpoint
{
    /// <summary>The public cloud's endpoint suffix; sovereign clouds and private endpoints type their own host.</summary>
    public const string DefaultHostSuffix = ".blob.core.windows.net";

    /// <summary>Scheme, authority and, path-style, the account: everything a container path goes after.</summary>
    private readonly string _root;

    /// <summary>The SAS token's query, appended to every URL; null when the requests are signed or anonymous.</summary>
    private readonly string? _sas;

    public AzureBlobEndpoint(ConnectionInfo connection, string? sasQuery)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Account = connection.Username.Trim();
        var host = connection.Host.Trim();

        // 21 is ConnectionInfo's own default, FTP's: a connection that never chose a port has it, and
        // no Blob endpoint listens there.
        var port = connection.Port == 21 ? 0 : connection.Port;

        // A pasted endpoint ("https://account.blob.core.windows.net/") is read for its host and any
        // explicit port. The scheme stays what UseHttp says: one switch decides it, not two.
        if (host.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(host, UriKind.Absolute, out var pasted))
        {
            host = pasted.Host;
            if (!pasted.IsDefaultPort)
            {
                port = pasted.Port;
            }
        }

        host = host.Trim('/');

        // "127.0.0.1:10000" typed into the host field: the port goes where a port belongs. A bare
        // IPv6 address has several colons and is left alone.
        var colon = host.LastIndexOf(':');
        if (colon > 0 && (host.IndexOf(':') == colon || host.StartsWith('['))
            && int.TryParse(host[(colon + 1)..], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var typedPort))
        {
            port = typedPort;
            host = host[..colon];
        }

        host = host.Trim('[', ']');
        if (host.Length == 0)
        {
            if (Account.Length == 0)
            {
                throw new ArgumentException("Enter the storage account name.", nameof(connection));
            }

            host = Account + DefaultHostSuffix;
        }

        PathStyle = connection.PathStyle || IsAddressOrLocalhost(host);
        if (PathStyle && Account.Length == 0)
        {
            throw new ArgumentException(
                "Enter the storage account name: this endpoint addresses the account in the path.", nameof(connection));
        }

        IsHttps = !connection.UseHttp;
        var defaultPort = IsHttps ? 443 : 80;
        var authority = (host.Contains(':') ? $"[{host}]" : host)
                        + (port > 0 && port != defaultPort ? ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty);
        _root = $"{(IsHttps ? "https" : "http")}://{authority}" + (PathStyle ? "/" + Uri.EscapeDataString(Account) : string.Empty);
        Host = host;

        var container = connection.Bucket?.Trim().Trim('/');
        FixedContainer = string.IsNullOrEmpty(container) ? null : container;
        _sas = string.IsNullOrEmpty(sasQuery) ? null : sasQuery;
    }

    /// <summary>The storage account name, as signed requests and read links name it.</summary>
    public string Account { get; }

    /// <summary>The endpoint host, without scheme or port.</summary>
    public string Host { get; }

    /// <summary>The container the connection is limited to, or null when the root lists them all.</summary>
    public string? FixedContainer { get; }

    /// <summary>True when the account is the first path segment rather than part of the host.</summary>
    public bool PathStyle { get; }

    public bool IsHttps { get; }

    /// <summary>Which container and blob <paramref name="path"/> names.</summary>
    public AzureBlobLocation Locate(VfsPath path)
    {
        var trimmed = path.Value.Trim('/');
        if (FixedContainer is not null)
        {
            return new AzureBlobLocation(FixedContainer, trimmed);
        }

        if (trimmed.Length == 0)
        {
            return new AzureBlobLocation(null, string.Empty);
        }

        var slash = trimmed.IndexOf('/');
        return slash < 0
            ? new AzureBlobLocation(trimmed, string.Empty)
            : new AzureBlobLocation(trimmed[..slash], trimmed[(slash + 1)..]);
    }

    /// <summary>The account-level URL (list containers, account information).</summary>
    public Uri Service(string? query) =>
        Build(PathStyle ? _root : _root + "/", query, includeSas: true);

    /// <summary>The container's URL; container requests add <c>restype=container</c> themselves.</summary>
    public Uri Container(string container, string? query) =>
        Build(_root + "/" + EscapeContainer(container), query, includeSas: true);

    /// <summary>
    /// The blob's URL. <paramref name="includeSas"/> is false only for a read link, which carries its
    /// own signature and must not hand out the connection's token.
    /// </summary>
    public Uri Blob(string container, string blob, string? query, bool includeSas = true) =>
        Build(_root + "/" + EscapeContainer(container) + "/" + EscapeBlobName(blob), query, includeSas);

    /// <summary>
    /// Percent-encodes every segment of a blob name and keeps the slashes. A name may hold anything:
    /// spaces, <c>+</c>, <c>%</c>, <c>#</c>, <c>?</c>, any script — and each must reach the service as
    /// itself, not as an escape it decodes into something else.
    /// </summary>
    public static string EscapeBlobName(string name) =>
        string.Join('/', name.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// Container names are lower-case letters, digits and hyphens, plus the system containers
    /// <c>$root</c>, <c>$logs</c> and <c>$web</c>: their <c>$</c> is legal in a path and stays as is.
    /// </summary>
    private static string EscapeContainer(string container) =>
        Uri.EscapeDataString(container).Replace("%24", "$", StringComparison.Ordinal);

    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    /// <summary>
    /// Builds the URI verbatim. A canonicalising <see cref="Uri"/> would fold a blob segment "." or
    /// ".." into its parent — a different blob — and the path it sends must be exactly the one signed.
    /// </summary>
    private Uri Build(string path, string? query, bool includeSas)
    {
        var parts = new List<string>(2);
        if (!string.IsNullOrEmpty(query))
        {
            parts.Add(query);
        }

        if (includeSas && _sas is not null)
        {
            parts.Add(_sas);
        }

        var text = parts.Count == 0 ? path : path + "?" + string.Join('&', parts);
        return new Uri(text, in Verbatim);
    }

    /// <summary>
    /// The query of a SAS token as typed: with or without the leading <c>?</c>, or the whole SAS URL
    /// the portal shows next to it.
    /// </summary>
    public static string NormalizeSasToken(string token)
    {
        var text = token.Trim();
        var question = text.IndexOf('?');
        if (question >= 0)
        {
            text = text[(question + 1)..];
        }

        return text.Trim('&');
    }

    /// <summary>An IP address or localhost cannot carry the account in its name, so the path does.</summary>
    private static bool IsAddressOrLocalhost(string host) =>
        IPAddress.TryParse(host, out _)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
}
