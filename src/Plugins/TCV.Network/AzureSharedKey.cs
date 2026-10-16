using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace TCV.Network;

/// <summary>
/// Azure Storage "Shared Key" signing: the string-to-sign of a request and of a read-only service
/// SAS, and the HMAC over it. Pure functions of their inputs, so tests pin the exact strings — a
/// signature that is off by one newline only ever comes back as "403 AuthenticationFailed".
/// </summary>
/// <remarks>
/// Follows "Authorize with Shared Key" (Blob service, version 2009-09-19 and later) and "Create a
/// service SAS" (version 2020-12-06 and later) on learn.microsoft.com.
/// </remarks>
internal static class AzureSharedKey
{
    /// <summary>
    /// The REST version every request names (<c>x-ms-version</c>) and the version a read link is signed
    /// with (<c>sv</c>). 2023-11-03 has all that is used here — conditional Put Blob and Copy Blob,
    /// <c>include=metadata</c>, Get Account Information with <c>x-ms-is-hns-enabled</c> — and current
    /// Azurite accepts it.
    /// </summary>
    public const string ApiVersion = "2023-11-03";

    /// <summary>The standard headers the string-to-sign lists, in its order.</summary>
    private static readonly string[] StandardHeaders =
    [
        "Content-Encoding", "Content-Language", "Content-Length", "Content-MD5", "Content-Type", "Date",
        "If-Modified-Since", "If-Match", "If-None-Match", "If-Unmodified-Since", "Range",
    ];

    /// <summary>Adds <c>Authorization: SharedKey account:signature</c> for the request as it will be sent.</summary>
    public static void Authorize(HttpRequestMessage request, string account, byte[] key)
    {
        var signature = ComputeSignature(StringToSign(request, account), key);
        request.Headers.Authorization = new AuthenticationHeaderValue("SharedKey", $"{account}:{signature}");
    }

    /// <summary>The string-to-sign of <paramref name="request"/>, read from the headers it will carry.</summary>
    public static string StringToSign(HttpRequestMessage request, string account)
    {
        ArgumentNullException.ThrowIfNull(request.RequestUri);
        return BuildStringToSign(request.Method.Method, request.RequestUri, HeadersOf(request), account);
    }

    /// <summary>
    /// Builds the string-to-sign from a method, the request URI and its headers (names in any case).
    /// </summary>
    public static string BuildStringToSign(
        string method, Uri uri, IEnumerable<KeyValuePair<string, string>> headers, string account)
    {
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
        {
            byName[name] = value;
        }

        var builder = new StringBuilder();
        builder.Append(method.ToUpperInvariant()).Append('\n');
        foreach (var name in StandardHeaders)
        {
            var value = byName.GetValueOrDefault(name) ?? string.Empty;

            // Since version 2015-02-21 a zero length is signed as an empty string, so a body-less PUT
            // signs the same whether or not the client sent "Content-Length: 0".
            if (name == "Content-Length" && value == "0")
            {
                value = string.Empty;
            }

            builder.Append(value).Append('\n');
        }

        foreach (var (name, value) in byName
                     .Where(h => h.Key.StartsWith("x-ms-", StringComparison.OrdinalIgnoreCase))
                     .Select(h => (Name: h.Key.ToLowerInvariant(), Value: h.Value.Trim()))
                     .OrderBy(h => h.Name, StringComparer.Ordinal))
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append(CanonicalizedResource(uri, account));
        return builder.ToString();
    }

    /// <summary>
    /// <c>/account/path</c>, the path exactly as encoded in the URI, then each query parameter on its
    /// own line: name lower-cased, value decoded, sorted by name, repeated values comma-joined.
    /// </summary>
    /// <remarks>
    /// The account comes first even when the URI already holds it (Azurite's
    /// <c>/devstoreaccount1/container</c>): the service signs <c>/devstoreaccount1/devstoreaccount1/container</c>.
    /// </remarks>
    public static string CanonicalizedResource(Uri uri, string account)
    {
        var builder = new StringBuilder("/").Append(account);
        builder.Append(uri.AbsolutePath.Length > 0 ? uri.AbsolutePath : "/");

        var parameters = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]).ToLowerInvariant();
            var value = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
            if (!parameters.TryGetValue(name, out var values))
            {
                parameters[name] = values = [];
            }

            values.Add(value);
        }

        foreach (var (name, values) in parameters)
        {
            values.Sort(StringComparer.Ordinal);
            builder.Append('\n').Append(name).Append(':').Append(string.Join(',', values));
        }

        return builder.ToString();
    }

    /// <summary>Base64 of HMAC-SHA256 over the UTF-8 string-to-sign, keyed with the decoded account key.</summary>
    public static string ComputeSignature(string stringToSign, byte[] key) =>
        Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(stringToSign)));

    /// <summary>
    /// The string-to-sign of a read-only service SAS for one blob (version 2020-12-06 and later): no
    /// start time (valid as soon as the service sees it, whatever this computer's clock says), no
    /// stored policy, IP range, snapshot, encryption scope or response-header overrides.
    /// </summary>
    /// <param name="canonicalizedResource"><c>/blob/account/container/blob</c>, the blob name unencoded.</param>
    public static string BlobReadSasStringToSign(
        string canonicalizedResource, string expiry, string protocol, string version)
    {
        const string Permissions = "r";
        const string Resource = "b";
        var start = string.Empty;
        var identifier = string.Empty;
        var ipRange = string.Empty;
        var snapshot = string.Empty;
        var encryptionScope = string.Empty;
        var cacheControl = string.Empty;
        var disposition = string.Empty;
        var encoding = string.Empty;
        var language = string.Empty;
        var contentType = string.Empty;

        return string.Join('\n',
            Permissions, start, expiry, canonicalizedResource, identifier, ipRange, protocol, version,
            Resource, snapshot, encryptionScope, cacheControl, disposition, encoding, language, contentType);
    }

    /// <summary>
    /// The query of a read-only link to one blob, valid until <paramref name="expiresOnUtc"/>. Over
    /// https the link is limited to https; an http endpoint (Azurite) allows both.
    /// </summary>
    public static string BlobReadSasQuery(
        string account, byte[] key, string container, string blob, DateTime expiresOnUtc, bool httpsOnly)
    {
        var expiry = expiresOnUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        var protocol = httpsOnly ? "https" : "https,http";
        var stringToSign = BlobReadSasStringToSign($"/blob/{account}/{container}/{blob}", expiry, protocol, ApiVersion);
        var signature = ComputeSignature(stringToSign, key);

        return $"sv={ApiVersion}&spr={Uri.EscapeDataString(protocol)}&se={Uri.EscapeDataString(expiry)}" +
               $"&sr=b&sp=r&sig={Uri.EscapeDataString(signature)}";
    }

    /// <summary>
    /// The request's headers as they go on the wire, content headers included. The length is asked for
    /// explicitly: <see cref="HttpContentHeaders.ContentLength"/> is computed on demand and missing from
    /// the enumeration until it is.
    /// </summary>
    private static List<KeyValuePair<string, string>> HeadersOf(HttpRequestMessage request)
    {
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var (name, values) in request.Headers.NonValidated)
        {
            headers.Add(new(name, values.ToString()));
        }

        if (request.Content is { } content)
        {
            foreach (var (name, values) in content.Headers.NonValidated)
            {
                headers.Add(new(name, values.ToString()));
            }

            var length = content.Headers.ContentLength;
            headers.Add(new("Content-Length", length is > 0 ? length.Value.ToString(CultureInfo.InvariantCulture) : string.Empty));
        }

        return headers;
    }
}
