using System.Globalization;
using System.Xml.Linq;

namespace TCV.Network;

/// <summary>One resource described by a WebDAV <c>PROPFIND</c> response.</summary>
/// <param name="Href">Server-side path of the resource, URL-decoded.</param>
/// <param name="Name">Last path segment, decoded.</param>
/// <param name="IsDirectory">True for a collection.</param>
/// <param name="Size">Content length in bytes; 0 for collections.</param>
/// <param name="LastModified">Last modification time, local.</param>
public sealed record WebDavResource(
    string Href, string Name, bool IsDirectory, long Size, DateTime LastModified);

/// <summary>
/// Parses the multi-status XML a WebDAV server answers <c>PROPFIND</c> with (6.23).
/// </summary>
/// <remarks>
/// Pure and transport-free on purpose: this is where the protocol's real-world messiness lives —
/// namespaces, both RFC 1123 and ISO 8601 dates, collections marked by an empty
/// <c>&lt;collection/&gt;</c> element or by a trailing slash — and all of it is unit-testable
/// without a server.
/// </remarks>
public static class WebDavResponse
{
    private static readonly XNamespace Dav = "DAV:";

    /// <summary>
    /// Reads a multi-status document. <paramref name="basePath"/> is the path that was requested;
    /// the entry describing it is dropped, since a listing must not contain itself.
    /// </summary>
    public static IReadOnlyList<WebDavResource> ParseMultiStatus(string xml, string basePath)
    {
        var resources = new List<WebDavResource>();

        XDocument document;
        try { document = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return resources; }

        var normalizedBase = NormalizePath(basePath);

        foreach (var response in document.Descendants(Dav + "response"))
        {
            var href = (string?)response.Element(Dav + "href");
            if (string.IsNullOrEmpty(href)) continue;

            // Reduce to a path while it is still percent-encoded, decode exactly once, and only then
            // normalise. Doing it the other way round decoded "%2520" twice, and turned an absolute
            // href with a space in it into a path that still carried the escape.
            var path = NormalizeDecoded(DecodePath(ReduceHref(href)));
            // The requested collection describes itself in its own listing; skipping it is what
            // stops every directory from containing a copy of itself.
            if (string.Equals(path, normalizedBase, StringComparison.OrdinalIgnoreCase)) continue;

            var properties = response.Descendants(Dav + "prop").FirstOrDefault();

            var isCollection =
                properties?.Descendants(Dav + "collection").Any() == true ||
                href.EndsWith('/');

            var size = 0L;
            var lengthText = (string?)properties?.Element(Dav + "getcontentlength");
            if (!isCollection && lengthText is not null)
                long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out size);

            var modified = ParseDate((string?)properties?.Element(Dav + "getlastmodified"));

            resources.Add(new WebDavResource(path, LastSegment(path), isCollection, size, modified));
        }

        return resources;
    }

    /// <summary>Last path segment, which is the name shown in the panel.</summary>
    public static string LastSegment(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// A path with exactly one leading slash and no trailing one, so two spellings of the same
    /// resource compare equal. The root stays "/".
    /// </summary>
    public static string NormalizePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return "/";

        // An href may be a full URL rather than a path.
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri)) path = uri.AbsolutePath;

        var value = "/" + path.Trim('/');
        return value;
    }

    /// <summary>An absolute-URL href reduced to its (still encoded) path; a path href is returned as is.</summary>
    private static string ReduceHref(string href) =>
        Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsolutePath
            : href;

    private static string NormalizeDecoded(string decodedPath) =>
        string.IsNullOrEmpty(decodedPath) ? "/" : "/" + decodedPath.Trim('/');

    /// <summary>Percent-decodes an href; leaves it alone when it is not valid encoding.</summary>
    public static string DecodePath(string href)
    {
        try { return Uri.UnescapeDataString(href); }
        catch (UriFormatException) { return href; }
    }

    /// <summary>
    /// Parses <c>getlastmodified</c>. Servers send RFC 1123 ("Mon, 12 Jan 2026 10:00:00 GMT"),
    /// but some send ISO 8601; an unparseable date becomes <see cref="DateTime.MinValue"/> rather
    /// than throwing, because one odd timestamp must not fail a whole listing.
    /// </summary>
    public static DateTime ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DateTime.MinValue;

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            return parsed.ToLocalTime();

        return DateTime.MinValue;
    }
}
