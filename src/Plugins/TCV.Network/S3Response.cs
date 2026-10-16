using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace TCV.Network;

internal sealed record S3Object(string Key, long Size, DateTime LastModified, string? ETag);

/// <summary>One page of ListObjectsV2: objects, the folders (common prefixes) and how to get the next page.</summary>
internal sealed record S3ListPage(
    IReadOnlyList<S3Object> Objects, IReadOnlyList<string> Prefixes, bool IsTruncated, string? NextContinuationToken);

internal sealed record S3Bucket(string Name, DateTime Created);

/// <summary>The <c>&lt;Error&gt;</c> document of a failed request.</summary>
internal sealed record S3Error(string? Code, string? Message, string? Region);

internal sealed record S3DeleteFailure(string Key, string? Code, string? Message);

/// <summary>
/// Reading and writing the XML of the S3 API, apart from the HTTP so it can be tested on its own.
/// Elements are matched by local name: AWS puts them in its 2006-03-01 namespace, some compatible
/// stores in none.
/// </summary>
internal static class S3Response
{
    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    public static S3ListPage ParseListObjects(string xml)
    {
        var root = Load(xml);

        // Asked for with encoding-type=url so that keys with characters XML 1.0 cannot carry survive;
        // a store that ignores the parameter says so by not echoing it, and its keys are taken as they are.
        var encoded = string.Equals(Value(root, "EncodingType"), "url", StringComparison.OrdinalIgnoreCase);
        string Decode(string text) => encoded ? UrlDecode(text) : text;

        var objects = Children(root, "Contents")
            .Select(c => new S3Object(
                Decode(Value(c, "Key") ?? string.Empty),
                long.TryParse(Value(c, "Size"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : 0,
                ParseTime(Value(c, "LastModified")),
                Value(c, "ETag")))
            .ToList();
        var prefixes = Children(root, "CommonPrefixes")
            .Select(p => Decode(Value(p, "Prefix") ?? string.Empty))
            .Where(p => p.Length > 0)
            .ToList();
        var truncated = bool.TryParse(Value(root, "IsTruncated"), out var t) && t;
        return new S3ListPage(objects, prefixes, truncated, Value(root, "NextContinuationToken"));
    }

    /// <summary>ListBuckets: the buckets and, from a store that pages them, the token of the next page.</summary>
    public static (IReadOnlyList<S3Bucket> Buckets, string? ContinuationToken) ParseBuckets(string xml)
    {
        var root = Load(xml);
        var buckets = root.Elements()
            .Where(e => e.Name.LocalName == "Buckets")
            .SelectMany(e => Children(e, "Bucket"))
            .Select(b => new S3Bucket(Value(b, "Name") ?? string.Empty, ParseTime(Value(b, "CreationDate"))))
            .Where(b => b.Name.Length > 0)
            .ToList();
        return (buckets, Value(root, "ContinuationToken"));
    }

    public static string ParseUploadId(string xml) =>
        Value(Load(xml), "UploadId") is { Length: > 0 } id
            ? id
            : throw new IOException("The S3 server started a multipart upload without naming it (no UploadId).");

    /// <summary>The objects a DeleteObjects request could not delete (quiet mode lists only those).</summary>
    public static IReadOnlyList<S3DeleteFailure> ParseDeleteResult(string xml) =>
        string.IsNullOrWhiteSpace(xml)
            ? []
            : Children(Load(xml), "Error")
                .Select(e => new S3DeleteFailure(Value(e, "Key") ?? string.Empty, Value(e, "Code"), Value(e, "Message")))
                .ToList();

    /// <summary>The error document in <paramref name="body"/>, or null when it is empty or something else.</summary>
    public static S3Error? ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            var root = XDocument.Parse(body).Root;
            return root?.Name.LocalName == "Error"
                ? new S3Error(Value(root, "Code"), Value(root, "Message"), Value(root, "Region"))
                : null;
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static string BuildDeleteBody(IEnumerable<string> keys) =>
        new XElement(Ns + "Delete",
            new XElement(Ns + "Quiet", "true"),
            keys.Select(k => new XElement(Ns + "Object", new XElement(Ns + "Key", k))))
        .ToString(SaveOptions.DisableFormatting);

    public static string BuildCompleteBody(IEnumerable<(int Number, string ETag)> parts) =>
        new XElement(Ns + "CompleteMultipartUpload",
            parts.Select(p => new XElement(Ns + "Part",
                new XElement(Ns + "PartNumber", p.Number.ToString(CultureInfo.InvariantCulture)),
                new XElement(Ns + "ETag", p.ETag))))
        .ToString(SaveOptions.DisableFormatting);

    /// <summary>
    /// Undoes encoding-type=url. S3 encodes like an HTML form — a space becomes <c>+</c> and a real
    /// plus <c>%2B</c> — so the plus is turned back into a space before the escapes are decoded.
    /// </summary>
    public static string UrlDecode(string text) => Uri.UnescapeDataString(text.Replace('+', ' '));

    private static XElement Load(string xml)
    {
        try
        {
            return XDocument.Parse(xml).Root ?? throw new XmlException("No root element.");
        }
        catch (XmlException ex)
        {
            throw new IOException(
                "The server did not answer like an S3 endpoint: check the endpoint host and the port.", ex);
        }
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) =>
        parent.Elements().Where(e => e.Name.LocalName == name);

    private static string? Value(XElement parent, string name) => Children(parent, name).FirstOrDefault()?.Value;

    /// <summary>An ISO 8601 UTC time as local time; <see cref="DateTime.MinValue"/> when missing or unreadable.</summary>
    private static DateTime ParseTime(string? text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc)
            ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime()
            : DateTime.MinValue;
}
