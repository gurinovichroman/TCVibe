using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace TCV.Network;

/// <summary>The keys an S3 request is signed with; <see cref="SessionToken"/> only for temporary ones.</summary>
internal sealed record S3Credentials(string AccessKeyId, string SecretAccessKey, string? SessionToken = null);

/// <summary>
/// AWS Signature Version 4 for S3, written from the AWS documentation instead of taken from an SDK
/// (no AWS packages, by the owner's decision). Pure: the keys and the time come in, a header or a
/// link goes out — so the example signatures AWS publishes are its tests.
/// </summary>
/// <remarks>
/// S3 differs from the other AWS services where it hurts: the path is encoded once, not twice, and
/// the payload hash travels as <c>x-amz-content-sha256</c> and is signed. The canonical path and
/// query are rebuilt from the decoded URI, so whatever escaping <see cref="Uri"/> chose, the
/// signature covers what the server decodes.
/// </remarks>
internal static class S3Signer
{
    public const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>SHA-256 of an empty body: what GET, HEAD and DELETE sign.</summary>
    public const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>What a presigned link signs instead of a body hash: the body is not known when it is made.</summary>
    public const string UnsignedPayload = "UNSIGNED-PAYLOAD";

    /// <summary>The longest a presigned link may live: SigV4 refuses anything over seven days.</summary>
    public static readonly TimeSpan MaxPresignExpiry = TimeSpan.FromDays(7);

    private const string Service = "s3";
    private const string Terminator = "aws4_request";

    /// <summary>Path and query exactly as built: <see cref="Uri"/> would otherwise fold <c>..</c> segments of a key.</summary>
    private static readonly UriCreationOptions Verbatim = new() { DangerousDisablePathAndQueryCanonicalization = true };

    /// <summary>
    /// Signs <paramref name="request"/> in place: sets <c>x-amz-date</c>, <c>x-amz-content-sha256</c>,
    /// the session token when there is one, and <c>Authorization</c> over <c>host</c>, every
    /// <c>x-amz-*</c> header and <c>Content-MD5</c>. Headers added afterwards are not covered.
    /// </summary>
    public static void Sign(
        HttpRequestMessage request, S3Credentials credentials, string region, DateTimeOffset now, string payloadHash)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no address.", nameof(request));

        Replace(request, "x-amz-date", FormatTime(now));
        Replace(request, "x-amz-content-sha256", payloadHash);
        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            Replace(request, "x-amz-security-token", credentials.SessionToken);
        }

        request.Headers.Remove("Authorization");

        var headers = new List<KeyValuePair<string, string>> { new("host", HostHeader(uri)) };
        foreach (var header in request.Headers)
        {
            if (header.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
            {
                headers.Add(new(header.Key, string.Join(",", header.Value)));
            }
        }

        if (request.Content?.Headers.ContentMD5 is { } md5)
        {
            headers.Add(new("content-md5", Convert.ToBase64String(md5)));
        }

        request.Headers.TryAddWithoutValidation(
            "Authorization", Authorization(request.Method.Method, uri, headers, payloadHash, credentials, region, now));
    }

    /// <summary>The <c>Authorization</c> header value for a request with exactly these signed headers.</summary>
    public static string Authorization(
        string method, Uri uri, IEnumerable<KeyValuePair<string, string>> headers, string payloadHash,
        S3Credentials credentials, string region, DateTimeOffset now)
    {
        var canonical = CanonicalRequest(
            method, CanonicalPath(uri), CanonicalQuery(ParseQuery(uri)), headers, payloadHash, out var signedHeaders);
        var scope = Scope(now, region);
        var signature = Signature(credentials.SecretAccessKey, now, region, StringToSign(now, scope, canonical));
        return $"{Algorithm} Credential={credentials.AccessKeyId}/{scope},SignedHeaders={signedHeaders},Signature={signature}";
    }

    /// <summary>
    /// A link that performs <paramref name="method"/> on <paramref name="uri"/> without credentials until
    /// <paramref name="expires"/> has passed: the signature travels in the query (<c>X-Amz-*</c>).
    /// </summary>
    public static Uri Presign(
        string method, Uri uri, S3Credentials credentials, string region, DateTimeOffset now, TimeSpan expires)
    {
        if (expires < TimeSpan.FromSeconds(1) || expires > MaxPresignExpiry)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expires), expires, "A presigned link lives from one second to seven days.");
        }

        var scope = Scope(now, region);
        var parameters = ParseQuery(uri);
        parameters.Add(new("X-Amz-Algorithm", Algorithm));
        parameters.Add(new("X-Amz-Credential", $"{credentials.AccessKeyId}/{scope}"));
        parameters.Add(new("X-Amz-Date", FormatTime(now)));
        parameters.Add(new("X-Amz-Expires", ((long)Math.Ceiling(expires.TotalSeconds)).ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(credentials.SessionToken))
        {
            // S3 wants the token inside the signed query, not appended after the signature.
            parameters.Add(new("X-Amz-Security-Token", credentials.SessionToken));
        }

        parameters.Add(new("X-Amz-SignedHeaders", "host"));

        var path = CanonicalPath(uri);
        var query = CanonicalQuery(parameters);
        var host = HostHeader(uri);
        var canonical = CanonicalRequest(method, path, query, [new("host", host)], UnsignedPayload, out _);
        var signature = Signature(credentials.SecretAccessKey, now, region, StringToSign(now, scope, canonical));
        return new Uri($"{uri.Scheme}://{host}{path}?{query}&X-Amz-Signature={signature}", Verbatim);
    }

    /// <summary>The canonical request of SigV4 task 1; <paramref name="signedHeaders"/> lists what it covers.</summary>
    public static string CanonicalRequest(
        string method, string canonicalPath, string canonicalQuery,
        IEnumerable<KeyValuePair<string, string>> headers, string payloadHash, out string signedHeaders)
    {
        var sorted = headers
            .GroupBy(h => h.Key.Trim().ToLowerInvariant())
            .Select(g => (Name: g.Key, Value: string.Join(",", g.Select(h => TrimAll(h.Value)))))
            .OrderBy(h => h.Name, StringComparer.Ordinal)
            .ToList();
        signedHeaders = string.Join(";", sorted.Select(h => h.Name));

        var builder = new StringBuilder();
        builder.Append(method).Append('\n')
            .Append(canonicalPath).Append('\n')
            .Append(canonicalQuery).Append('\n');
        foreach (var (name, value) in sorted)
        {
            builder.Append(name).Append(':').Append(value).Append('\n');
        }

        builder.Append('\n').Append(signedHeaders).Append('\n').Append(payloadHash);
        return builder.ToString();
    }

    public static string StringToSign(DateTimeOffset now, string scope, string canonicalRequest) =>
        $"{Algorithm}\n{FormatTime(now)}\n{scope}\n{Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))}";

    public static string Scope(DateTimeOffset now, string region) => $"{FormatDay(now)}/{region}/{Service}/{Terminator}";

    public static string Signature(string secretKey, DateTimeOffset now, string region, string stringToSign) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(SigningKey(secretKey, now, region), Encoding.UTF8.GetBytes(stringToSign)));

    public static byte[] SigningKey(string secretKey, DateTimeOffset now, string region)
    {
        var date = HMACSHA256.HashData(Encoding.UTF8.GetBytes("AWS4" + secretKey), Encoding.UTF8.GetBytes(FormatDay(now)));
        var regional = HMACSHA256.HashData(date, Encoding.UTF8.GetBytes(region));
        var service = HMACSHA256.HashData(regional, Encoding.UTF8.GetBytes(Service));
        return HMACSHA256.HashData(service, Encoding.UTF8.GetBytes(Terminator));
    }

    /// <summary>
    /// The path as S3 signs it: every segment decoded and encoded again by <see cref="UriEncode"/>,
    /// the slashes kept. Never double-encoded — that is the rule of the other services, not S3's.
    /// </summary>
    public static string CanonicalPath(Uri uri)
    {
        var path = uri.AbsolutePath;
        return string.IsNullOrEmpty(path)
            ? "/"
            : string.Join('/', path.Split('/').Select(segment => UriEncode(Uri.UnescapeDataString(segment))));
    }

    /// <summary>Parameters encoded by <see cref="UriEncode"/> and sorted by name, then value; a bare name gets <c>=</c>.</summary>
    public static string CanonicalQuery(IEnumerable<KeyValuePair<string, string>> parameters) =>
        string.Join('&', parameters
            .Select(p => (Name: UriEncode(p.Key), Value: UriEncode(p.Value)))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Value, StringComparer.Ordinal)
            .Select(p => p.Name + "=" + p.Value));

    /// <summary>
    /// RFC 3986 percent-encoding of the UTF-8 bytes, everything but <c>A-Z a-z 0-9 - _ . ~</c>, in
    /// upper-case hex. Unlike <see cref="Uri.EscapeDataString"/> it also encodes <c>!*'()</c>, as SigV4 requires.
    /// </summary>
    public static string UriEncode(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    /// <summary>The <c>Host</c> header .NET sends for <paramref name="uri"/>: the port only when it is not the scheme's default.</summary>
    public static string HostHeader(Uri uri)
    {
        var host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host : uri.IdnHost;
        return uri.IsDefaultPort ? host : $"{host}:{uri.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Builds a request address that keeps its path and query exactly as given.</summary>
    public static Uri VerbatimUri(string text) => new(text, Verbatim);

    private static List<KeyValuePair<string, string>> ParseQuery(Uri uri)
    {
        var parameters = new List<KeyValuePair<string, string>>();
        var query = uri.Query;
        if (query.Length <= 1)
        {
            return parameters;
        }

        foreach (var part in query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            var name = equals < 0 ? part : part[..equals];
            var value = equals < 0 ? string.Empty : part[(equals + 1)..];
            parameters.Add(new(Uri.UnescapeDataString(name), Uri.UnescapeDataString(value)));
        }

        return parameters;
    }

    /// <summary>Header values are trimmed and inner runs of spaces folded to one before signing.</summary>
    private static string TrimAll(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.Contains("  ", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var builder = new StringBuilder(trimmed.Length);
        foreach (var c in trimmed)
        {
            if (c != ' ' || builder.Length == 0 || builder[^1] != ' ')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static void Replace(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string FormatTime(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string FormatDay(DateTimeOffset now) =>
        now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
}
