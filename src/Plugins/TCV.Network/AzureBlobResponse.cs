using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>One blob of a List Blobs page.</summary>
/// <param name="Name">The full blob name, decoded.</param>
/// <param name="LastModified">Local time; <see cref="DateTime.MinValue"/> when the service gave none.</param>
/// <param name="IsFolderMarker">
/// A directory of a hierarchical-namespace (ADLS Gen2) account, or a folder another tool marked the
/// same way: metadata <c>hdi_isfolder=true</c>, or <c>ResourceType</c> <c>directory</c>.
/// </param>
internal sealed record AzureListedBlob(string Name, long Size, DateTime LastModified, bool IsFolderMarker);

/// <summary>A page of List Blobs: blobs, folder prefixes (<c>"dir/"</c>) and where the next page starts.</summary>
internal sealed record AzureBlobPage(
    IReadOnlyList<AzureListedBlob> Blobs, IReadOnlyList<string> Prefixes, string? NextMarker);

/// <summary>A page of List Containers.</summary>
internal sealed record AzureContainerPage(
    IReadOnlyList<(string Name, DateTime LastModified)> Containers, string? NextMarker);

/// <summary>The <c>&lt;Error&gt;</c> body of a failed request; every part may be missing.</summary>
internal sealed record AzureErrorBody(string? Code, string? Message, string? AuthenticationDetail);

/// <summary>
/// Reads the XML Azure Blob Storage answers with, and turns a listing into panel entries. Pure and
/// transport-free, like <see cref="WebDavResponse"/>: the folder rules — prefixes, the markers of
/// hierarchical accounts, the marker a folder leaves in its own listing — are tested without a server.
/// </summary>
internal static class AzureBlobResponse
{
    /// <summary>Parses a List Blobs page. A body that is not the expected XML is an error, not an empty folder.</summary>
    public static AzureBlobPage ParseBlobList(string xml)
    {
        var root = Load(xml);
        var blobs = new List<AzureListedBlob>();
        var prefixes = new List<string>();
        var container = root.Element("Blobs");
        if (container is not null)
        {
            foreach (var element in container.Elements())
            {
                var name = ReadName(element.Element("Name"));
                if (name is null)
                {
                    continue;
                }

                if (element.Name.LocalName == "BlobPrefix")
                {
                    prefixes.Add(name);
                    continue;
                }

                if (element.Name.LocalName != "Blob")
                {
                    continue;
                }

                var properties = element.Element("Properties");
                long.TryParse((string?)properties?.Element("Content-Length"), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var size);
                var isFolder =
                    string.Equals((string?)properties?.Element("ResourceType"), "directory", StringComparison.OrdinalIgnoreCase)
                    || element.Element("Metadata")?.Elements().Any(m =>
                        m.Name.LocalName.Equals("hdi_isfolder", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)) == true;

                blobs.Add(new AzureListedBlob(name, isFolder ? 0 : size,
                    ParseDate((string?)properties?.Element("Last-Modified")), isFolder));
            }
        }

        return new AzureBlobPage(blobs, prefixes, NextMarker(root));
    }

    /// <summary>Parses a List Containers page.</summary>
    public static AzureContainerPage ParseContainerList(string xml)
    {
        var root = Load(xml);
        var containers = root.Element("Containers")?.Elements("Container")
            .Select(c => (Name: (string?)c.Element("Name"),
                LastModified: ParseDate((string?)c.Element("Properties")?.Element("Last-Modified"))))
            .Where(c => !string.IsNullOrEmpty(c.Name))
            .Select(c => (c.Name!, c.LastModified))
            .ToList() ?? [];

        return new AzureContainerPage(containers, NextMarker(root));
    }

    /// <summary>Reads an <c>&lt;Error&gt;</c> body; anything else (a proxy's HTML page, nothing) gives all nulls.</summary>
    public static AzureErrorBody ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return new AzureErrorBody(null, null, null);
        }

        try
        {
            var root = Load(body);
            return new AzureErrorBody(
                Trimmed((string?)root.Element("Code")),
                FirstLine((string?)root.Element("Message")),
                FirstLine((string?)root.Element("AuthenticationErrorDetail")));
        }
        catch (IOException)
        {
            return new AzureErrorBody(null, null, null);
        }
    }

    /// <summary>
    /// The entries of one folder from what a delimited listing of <paramref name="prefix"/> returned:
    /// prefixes and folder markers become folders — each once, though a hierarchical account reports
    /// a non-empty directory both ways — and the marker a folder may hold for itself (<c>"dir/"</c>)
    /// is not listed inside it.
    /// </summary>
    public static IReadOnlyList<IFileEntry> BuildEntries(
        VfsPath folder, string prefix, IEnumerable<AzureListedBlob> blobs, IEnumerable<string> prefixes)
    {
        // Blob names are case-sensitive: "Docs" and "docs" are two folders.
        var folders = new Dictionary<string, IFileEntry>(StringComparer.Ordinal);
        var files = new List<IFileEntry>();

        foreach (var item in prefixes)
        {
            if (!item.StartsWith(prefix, StringComparison.Ordinal) || !item.EndsWith('/'))
            {
                continue;
            }

            var name = item[prefix.Length..^1];

            // "a//b" has a folder with an empty name between the slashes; a path cannot address it.
            if (name.Length == 0 || name.Contains('/'))
            {
                continue;
            }

            folders.TryAdd(name, new NetworkFileEntry(Child(folder, name), name, true, 0, DateTime.MinValue));
        }

        foreach (var blob in blobs)
        {
            if (!blob.Name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var name = blob.Name[prefix.Length..];
            if (name.Length == 0 || name.Contains('/'))
            {
                continue;
            }

            if (blob.IsFolderMarker)
            {
                // The marker knows when the folder was made; a bare prefix knows nothing.
                folders[name] = new NetworkFileEntry(Child(folder, name), name, true, 0, blob.LastModified);
                continue;
            }

            files.Add(new NetworkFileEntry(Child(folder, name), name, false, blob.Size, blob.LastModified));
        }

        return [.. folders.Values, .. files];
    }

    /// <summary>The containers of an account as folders of its root.</summary>
    public static IReadOnlyList<IFileEntry> BuildContainerEntries(IEnumerable<(string Name, DateTime LastModified)> containers) =>
        containers
            .Select(c => (IFileEntry)new NetworkFileEntry(Child(VfsPath.Root, c.Name), c.Name, true, 0, c.LastModified))
            .ToList();

    /// <summary>
    /// The path of <paramref name="name"/> in <paramref name="folder"/>. Plain concatenation:
    /// <see cref="VfsPath.Combine"/> goes through <see cref="Path.Combine(string, string)"/>, which
    /// would read a blob called <c>C:x</c> as a drive.
    /// </summary>
    public static VfsPath Child(VfsPath folder, string name) =>
        new(folder.Value == "/" ? "/" + name : folder.Value + "/" + name);

    /// <summary>Parses an RFC 1123 date into local time; <see cref="DateTime.MinValue"/> when it cannot.</summary>
    public static DateTime ParseDate(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.LocalDateTime
            : DateTime.MinValue;

    /// <summary>
    /// A <c>Name</c> element's text. A name holding characters XML cannot carry comes percent-encoded,
    /// flagged <c>Encoded="true"</c>.
    /// </summary>
    private static string? ReadName(XElement? element)
    {
        if (element is null)
        {
            return null;
        }

        return string.Equals((string?)element.Attribute("Encoded"), "true", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(element.Value)
            : element.Value;
    }

    private static string? NextMarker(XElement root) => Trimmed((string?)root.Element("NextMarker"));

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>The service appends "RequestId:…" and "Time:…" lines that mean nothing to a user.</summary>
    private static string? FirstLine(string? text)
    {
        var trimmed = Trimmed(text);
        if (trimmed is null)
        {
            return null;
        }

        var newline = trimmed.IndexOfAny(['\r', '\n']);
        return newline < 0 ? trimmed : trimmed[..newline].Trim();
    }

    /// <summary>Loads the document element. No DTDs: nothing the service sends has one.</summary>
    private static XElement Load(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit });
            return XDocument.Load(reader).Root ?? throw new XmlException("The document is empty.");
        }
        catch (XmlException ex)
        {
            throw new IOException("Azure Blob Storage sent an answer that could not be read: " + ex.Message, ex);
        }
    }
}
