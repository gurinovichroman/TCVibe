using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace TCV.Network;

/// <summary>
/// Where an S3 request goes: the endpoint of a connection and the two ways of naming a bucket in
/// it — virtual-hosted (<c>bucket.host/key</c>, what AWS wants) or path-style (<c>host/bucket/key</c>,
/// what a local MinIO or an IP address can only do).
/// </summary>
internal sealed partial class S3Endpoint
{
    public S3Endpoint(string scheme, string host, int port, bool forcePathStyle)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        ForcePathStyle = forcePathStyle;
    }

    public string Scheme { get; }

    /// <summary>The endpoint host without a port, lower case (IPv6 in brackets).</summary>
    public string Host { get; }

    public int Port { get; }

    public bool ForcePathStyle { get; }

    private bool IsDefaultPort => Port == (Scheme == "http" ? 80 : 443);

    /// <summary>
    /// Reads the endpoint of <paramref name="connection"/>. A host pasted with its scheme or port
    /// (<c>http://localhost:9000</c>, as provider pages print it) is taken apart rather than refused.
    /// </summary>
    public static S3Endpoint From(ConnectionInfo connection)
    {
        var scheme = connection.UseHttp ? "http" : "https";
        var text = connection.Host.Trim().TrimEnd('/');
        if (text.Length == 0)
        {
            throw new ArgumentException("The S3 connection has no endpoint host.", nameof(connection));
        }

        var typedScheme = text.Contains("://", StringComparison.Ordinal);
        if (!Uri.TryCreate(typedScheme ? text : $"{scheme}://{text}", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException($"\"{connection.Host}\" is not an endpoint host name.", nameof(connection));
        }

        if (typedScheme)
        {
            scheme = uri.Scheme;
        }

        // A port typed into the host wins over the port field; a typed scheme brings its own default.
        var port = !uri.IsDefaultPort ? uri.Port
            : typedScheme || connection.Port <= 0 ? uri.Port
            : connection.Port;
        return new S3Endpoint(scheme, uri.Host, port, connection.PathStyle);
    }

    /// <summary>
    /// True when <paramref name="bucket"/> goes into the path: asked for; an IP address or
    /// <c>localhost</c>, which have no subdomains; a name DNS cannot carry; or a dotted name over https,
    /// whose <c>a.b.host</c> the endpoint's wildcard certificate (<c>*.host</c>) does not cover.
    /// </summary>
    public bool UsesPathStyle(string bucket) =>
        ForcePathStyle
        || IsAddressOrLocal(Host)
        || (Scheme == "https" && bucket.Contains('.', StringComparison.Ordinal))
        || !DnsCompatibleBucket().IsMatch(bucket)
        || bucket.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// The address of <paramref name="key"/> in <paramref name="bucket"/> (or of the endpoint itself for
    /// a null bucket) at <paramref name="host"/>, which differs from <see cref="Host"/> once a bucket was
    /// found to live in another region. A parameter with an empty value is sent bare (<c>?uploads</c>).
    /// </summary>
    public Uri BuildUri(string? bucket, string key, IReadOnlyList<KeyValuePair<string, string>> query, string host)
    {
        var authority = IsDefaultPort ? host : $"{host}:{Port.ToString(CultureInfo.InvariantCulture)}";
        string path;
        if (bucket is null)
        {
            path = "/";
        }
        else if (UsesPathStyle(bucket))
        {
            path = "/" + S3Signer.UriEncode(bucket) + (key.Length == 0 ? string.Empty : "/" + EncodeKey(key));
        }
        else
        {
            authority = bucket + "." + authority;
            path = "/" + EncodeKey(key);
        }

        var text = $"{Scheme}://{authority}{path}";
        if (query.Count > 0)
        {
            text += "?" + string.Join('&', query.Select(p => p.Value.Length == 0
                ? S3Signer.UriEncode(p.Key)
                : S3Signer.UriEncode(p.Key) + "=" + S3Signer.UriEncode(p.Value)));
        }

        return S3Signer.VerbatimUri(text);
    }

    /// <summary>Encodes every segment of an object key, keeping the slashes that separate them.</summary>
    public static string EncodeKey(string key) => string.Join('/', key.Split('/').Select(S3Signer.UriEncode));

    /// <summary>
    /// The endpoint host for a bucket that turned out to be in <paramref name="newRegion"/>. A regional
    /// AWS endpoint answers a bucket of another region with a redirect, not the data, so the region in
    /// the host name changes with the signing region; the global <c>s3.amazonaws.com</c> becomes
    /// regional. A host that names no region (R2, MinIO) stays as it is: only the signature changes.
    /// </summary>
    public static string RetargetHost(string host, string oldRegion, string newRegion)
    {
        if (host is "s3.amazonaws.com" or "s3-external-1.amazonaws.com")
        {
            return $"s3.{newRegion}.amazonaws.com";
        }

        var labels = host.Split('.');
        var changed = false;
        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i].Equals(oldRegion, StringComparison.OrdinalIgnoreCase))
            {
                labels[i] = newRegion;
                changed = true;
            }
            else if (labels[i].Equals("s3-" + oldRegion, StringComparison.OrdinalIgnoreCase))
            {
                // The legacy dash form (s3-eu-west-1.amazonaws.com) exists only for old regions.
                labels[i] = "s3." + newRegion;
                changed = true;
            }
        }

        return changed ? string.Join('.', labels) : host;
    }

    /// <summary>
    /// Reads where a 307 sends a request: the <c>Location</c> header, else the <c>&lt;Endpoint&gt;</c> of
    /// the error body (a host and nothing more). Null when the answer names no address. Only the host
    /// is taken: the request stays the same request, so the path and query of the new address play no part.
    /// </summary>
    /// <remarks>
    /// A redirect is followed only to the provider the connection is for: same scheme, so https never
    /// falls back to http; same port; same base zone (<see cref="IsSameZone"/>). A signature is made for
    /// one host and a request carries the user's data, so neither goes to an address a server merely
    /// named. The base zone is taken from the connection's own host, so a chain of redirects cannot
    /// walk away from it step by step.
    /// </remarks>
    public S3Redirect? ReadRedirect(string? bucket, Uri? location, string? namedEndpoint)
    {
        var fromHeader = location is { IsAbsoluteUri: true };
        Uri? target = fromHeader ? location : null;
        if (target is null
            && (string.IsNullOrWhiteSpace(namedEndpoint)
                || !Uri.TryCreate($"{Scheme}://{namedEndpoint.Trim()}", UriKind.Absolute, out target)))
        {
            return null;
        }

        var host = target.Host;
        if (bucket is not null && host.StartsWith(bucket + ".", StringComparison.OrdinalIgnoreCase))
        {
            // The virtual-hosted form: BuildUri puts the bucket back, or into the path.
            host = host[(bucket.Length + 1)..];
        }

        var allowed = host.Length > 0
                      && target.Scheme == Scheme
                      && (!fromHeader || target.Port == Port)
                      && IsSameZone(Host, host);
        return new S3Redirect(host, RegionInHost(host), allowed);
    }

    /// <summary>
    /// True when <paramref name="other"/> is <paramref name="host"/> or lies in its base zone. An address
    /// or a local name has no zone, only itself.
    /// </summary>
    public static bool IsSameZone(string host, string other)
    {
        if (host.Equals(other, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return BaseZone(host) is { } zone
               && (other.Equals(zone, StringComparison.OrdinalIgnoreCase)
                   || other.EndsWith("." + zone, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The registered domain of <paramref name="host"/>: <c>amazonaws.com</c> for any AWS endpoint. A
    /// country's own second level (<c>amazonaws.com.cn</c>, <c>example.co.uk</c>) takes one label more, so
    /// the zone is never all of <c>co.uk</c>. Without a list of public suffixes a short domain under a
    /// country code (<c>abc.de</c>) also counts as such a level: a zone too narrow refuses a redirect,
    /// one too wide would follow it to a stranger. Null for an address or a name without a dot.
    /// </summary>
    public static string? BaseZone(string host)
    {
        if (IsAddressOrLocal(host))
        {
            return null;
        }

        var labels = host.Split('.');
        if (labels.Length < 2)
        {
            return null;
        }

        var keep = labels[^1].Length == 2 && labels[^2].Length <= 3 ? 3 : 2;
        return labels.Length <= keep ? host : string.Join('.', labels[^keep..]);
    }

    /// <summary>
    /// The region an S3 host name states: <c>s3.eu-west-1.…</c>, <c>s3-eu-west-1.…</c>,
    /// <c>s3.dualstack.eu-west-1.…</c>. Null when it states none (<c>s3.amazonaws.com</c>,
    /// <c>s3-accelerate.…</c>, a host of another shape): the signature then keeps its region, and a store
    /// that disagrees says so with a 400 that <c>SendAsync</c> already acts on.
    /// </summary>
    public static string? RegionInHost(string host)
    {
        if (BaseZone(host) is not { } zone || host.Length <= zone.Length)
        {
            return null;
        }

        var labels = host[..^(zone.Length + 1)].Split('.');
        if (labels[0] != "s3" && !labels[0].StartsWith("s3-", StringComparison.Ordinal))
        {
            return null;
        }

        var last = labels[^1];
        var region = last.StartsWith("s3-", StringComparison.Ordinal) ? last[3..] : last;
        return RegionName().IsMatch(region) ? region : null;
    }

    private static bool IsAddressOrLocal(string host) =>
        IPAddress.TryParse(host.Trim('[', ']'), out _)
        || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$")]
    private static partial Regex DnsCompatibleBucket();

    /// <summary>Words joined by hyphens with a digit somewhere: <c>eu-west-1</c>, <c>ru-central1</c>; not <c>accelerate</c> or <c>fips</c>.</summary>
    [GeneratedRegex("^(?=.*[0-9])[a-z]+(-[a-z0-9]+)+$")]
    private static partial Regex RegionName();
}

/// <summary>Where a 307 sends a request: the endpoint host, the region it states if any, and whether it may be followed.</summary>
internal sealed record S3Redirect(string Host, string? Region, bool Allowed);
