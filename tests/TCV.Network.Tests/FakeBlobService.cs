using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;

namespace TCV.Network.Tests;

/// <summary>
/// An in-memory Blob service answering the requests <see cref="AzureBlobFileSystem"/> makes: list
/// (prefix, delimiter, paging), properties, ranged and conditional reads, Put Blob, blocks, Copy Blob
/// (synchronous, or pending for a number of looks), delete, containers and account information. A
/// hierarchical account keeps directory blobs (<c>hdi_isfolder</c>) and refuses to delete a non-empty one.
/// </summary>
/// <remarks>
/// Not a second implementation of the protocol to trust: it is as simple as the tests allow, and the
/// request log is there so tests can say what went on the wire.
/// </remarks>
internal sealed class FakeBlobService : HttpMessageHandler
{
    public static readonly DateTimeOffset Modified = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

    private readonly object _sync = new();
    private readonly string? _accountInPath;
    private int _etag = 0x100;

    public FakeBlobService(string? accountInPath = null) => _accountInPath = accountInPath;

    public sealed class Blob
    {
        public byte[] Data { get; set; } = [];
        public string ETag { get; set; } = "\"0x0\"";
        public Dictionary<string, string> Metadata { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte[]> Staged { get; } = new(StringComparer.Ordinal);
        public string? CopyId { get; set; }
        public string? CopyStatus { get; set; }
        public int PendingLooks { get; set; }
        public bool IsDirectory => Metadata.TryGetValue("hdi_isfolder", out var v) && v == "true";
    }

    public sealed record Call(string Method, string Path, IReadOnlyDictionary<string, string> Query,
        IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
        public string? Q(string name) => Query.TryGetValue(name, out var value) ? value : null;
        public override string ToString() => $"{Method} {Path}?{string.Join('&', Query.Select(q => q.Key + "=" + q.Value))}";
    }

    public Dictionary<string, SortedDictionary<string, Blob>> Containers { get; } = new(StringComparer.Ordinal);
    public bool Hierarchical { get; set; }

    /// <summary>The most items a page holds, whatever the client asks for: makes paging testable.</summary>
    public int PageLimit { get; set; } = int.MaxValue;

    /// <summary>How many looks a Copy Blob reports "pending" before "success"; 0 copies synchronously.</summary>
    public int CopyLooks { get; set; }

    /// <summary>Answers a request before the fake does, when it returns a response.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? Intercept { get; set; }

    public List<Call> Calls { get; } = [];

    public SortedDictionary<string, Blob> Container(string name)
    {
        if (!Containers.TryGetValue(name, out var blobs))
        {
            Containers[name] = blobs = new SortedDictionary<string, Blob>(StringComparer.Ordinal);
        }

        return blobs;
    }

    public FakeBlobService WithContainer(string name)
    {
        lock (_sync)
        {
            Container(name);
        }

        return this;
    }

    public FakeBlobService With(string container, string name, string content = "x")
    {
        lock (_sync)
        {
            Store(container, name, Encoding.UTF8.GetBytes(content), null);
        }

        return this;
    }

    public FakeBlobService WithDirectory(string container, string name)
    {
        lock (_sync)
        {
            Store(container, name, [], new Dictionary<string, string> { ["hdi_isfolder"] = "true" });
        }

        return this;
    }

    public string Text(string container, string name) => Encoding.UTF8.GetString(Container(container)[name].Data);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers.NonValidated)
        {
            headers[name] = values.ToString();
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers.NonValidated)
            {
                headers[name] = values.ToString();
            }
        }

        var uri = request.RequestUri!;
        var query = ParseQuery(uri.Query);
        lock (_sync)
        {
            Calls.Add(new Call(request.Method.Method, uri.AbsolutePath, query, headers, body));
        }

        if (Intercept?.Invoke(request) is { } intercepted)
        {
            return intercepted;
        }

        var (container, blob) = Split(uri.AbsolutePath);
        lock (_sync)
        {
            return Handle(request.Method.Method, container, blob, query, headers, body);
        }
    }

    private HttpResponseMessage Handle(
        string method, string? container, string blob, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        if (container is null)
        {
            return method == "GET" && query.GetValueOrDefault("comp") == "list"
                ? ListContainers(query)
                : Error(HttpStatusCode.BadRequest, "UnsupportedHttpVerb");
        }

        if (blob.Length == 0)
        {
            if (query.GetValueOrDefault("restype") == "account")
            {
                var info = new HttpResponseMessage(HttpStatusCode.OK);
                info.Headers.TryAddWithoutValidation("x-ms-is-hns-enabled", Hierarchical ? "true" : "false");
                return info;
            }

            return (method, query.GetValueOrDefault("comp")) switch
            {
                ("GET", "list") => Containers.ContainsKey(container) ? ListBlobs(container, query) : Error(HttpStatusCode.NotFound, "ContainerNotFound"),
                ("PUT", _) => CreateContainer(container),
                ("DELETE", _) => Containers.Remove(container)
                    ? new HttpResponseMessage(HttpStatusCode.Accepted)
                    : Error(HttpStatusCode.NotFound, "ContainerNotFound"),
                _ => Error(HttpStatusCode.BadRequest, "UnsupportedHttpVerb"),
            };
        }

        if (!Containers.TryGetValue(container, out var blobs))
        {
            return Error(HttpStatusCode.NotFound, "ContainerNotFound", method == "HEAD");
        }

        return method switch
        {
            "HEAD" => blobs.TryGetValue(blob, out var found) ? Properties(found) : Error(HttpStatusCode.NotFound, "BlobNotFound", head: true),
            "GET" => Read(blobs, blob, headers),
            "PUT" => Write(container, blobs, blob, query, headers, body),
            "DELETE" => Delete(blobs, blob),
            _ => Error(HttpStatusCode.BadRequest, "UnsupportedHttpVerb"),
        };
    }

    private HttpResponseMessage CreateContainer(string container)
    {
        if (Containers.ContainsKey(container))
        {
            return Error(HttpStatusCode.Conflict, "ContainerAlreadyExists");
        }

        Container(container);
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private HttpResponseMessage ListContainers(IReadOnlyDictionary<string, string> query)
    {
        var names = Containers.Keys.Order(StringComparer.Ordinal).ToList();
        var (page, next) = Page(names, query);
        var xml = new XElement("EnumerationResults",
            new XElement("Containers", page.Select(n => new XElement("Container",
                new XElement("Name", n),
                new XElement("Properties", new XElement("Last-Modified", Modified.ToString("R", CultureInfo.InvariantCulture)))))),
            new XElement("NextMarker", next ?? string.Empty));
        return Xml(xml);
    }

    private HttpResponseMessage ListBlobs(string container, IReadOnlyDictionary<string, string> query)
    {
        var prefix = query.GetValueOrDefault("prefix") ?? string.Empty;
        var delimited = query.GetValueOrDefault("delimiter") == "/";
        var items = new SortedDictionary<string, Blob?>(StringComparer.Ordinal);
        foreach (var (name, blob) in Containers[container])
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var slash = delimited ? name.IndexOf('/', prefix.Length) : -1;
            if (slash >= 0)
            {
                items[name[..(slash + 1)]] = null;
            }
            else
            {
                items[name] = blob;
            }
        }

        var (page, next) = Page(items.Keys.ToList(), query);
        var withMetadata = query.GetValueOrDefault("include")?.Contains("metadata") == true;
        var xml = new XElement("EnumerationResults",
            new XElement("Prefix", prefix),
            new XElement("Blobs", page.Select(name => items[name] is { } blob
                ? new XElement("Blob",
                    new XElement("Name", name),
                    new XElement("Properties",
                        new XElement("Last-Modified", Modified.ToString("R", CultureInfo.InvariantCulture)),
                        new XElement("Etag", blob.ETag),
                        new XElement("Content-Length", blob.Data.Length)),
                    withMetadata ? new XElement("Metadata", blob.Metadata.Select(m => new XElement(m.Key, m.Value))) : null)
                : new XElement("BlobPrefix", new XElement("Name", name)))),
            new XElement("NextMarker", next ?? string.Empty));
        return Xml(xml);
    }

    /// <summary>Pages by name: the marker is the first name of the next page.</summary>
    private (List<string> Page, string? Next) Page(List<string> names, IReadOnlyDictionary<string, string> query)
    {
        var marker = query.GetValueOrDefault("marker");
        var size = Math.Min(PageLimit, int.Parse(query.GetValueOrDefault("maxresults") ?? "5000", CultureInfo.InvariantCulture));
        var rest = names.Where(n => marker is null || string.CompareOrdinal(n, marker) >= 0).ToList();
        return (rest.Take(size).ToList(), rest.Count > size ? rest[size] : null);
    }

    private HttpResponseMessage Properties(Blob blob)
    {
        if (blob.CopyStatus == "pending" && --blob.PendingLooks <= 0)
        {
            blob.CopyStatus = "success";
        }

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
        response.Content.Headers.ContentLength = blob.Data.Length;
        response.Content.Headers.LastModified = Modified;
        response.Headers.ETag = EntityTagHeaderValue.Parse(blob.ETag);
        foreach (var (key, value) in blob.Metadata)
        {
            response.Headers.TryAddWithoutValidation("x-ms-meta-" + key, value);
        }

        if (blob.CopyStatus is not null)
        {
            response.Headers.TryAddWithoutValidation("x-ms-copy-id", blob.CopyId);
            response.Headers.TryAddWithoutValidation("x-ms-copy-status", blob.CopyStatus);
            var done = blob.CopyStatus == "success" ? blob.Data.Length : blob.Data.Length / 2;
            response.Headers.TryAddWithoutValidation("x-ms-copy-progress", $"{done}/{blob.Data.Length}");
        }

        return response;
    }

    private static HttpResponseMessage Read(SortedDictionary<string, Blob> blobs, string name, IReadOnlyDictionary<string, string> headers)
    {
        if (!blobs.TryGetValue(name, out var blob))
        {
            return Error(HttpStatusCode.NotFound, "BlobNotFound");
        }

        if (headers.TryGetValue("If-Match", out var ifMatch) && ifMatch != blob.ETag)
        {
            return Error(HttpStatusCode.PreconditionFailed, "ConditionNotMet");
        }

        long from = 0;
        if (headers.TryGetValue("Range", out var range))
        {
            from = long.Parse(range["bytes=".Length..].TrimEnd('-'), CultureInfo.InvariantCulture);
            if (from >= blob.Data.Length)
            {
                return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange");
            }
        }

        var response = new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(blob.Data[(int)from..]),
        };
        if (from > 0)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, blob.Data.Length - 1, blob.Data.Length);
        }

        response.Headers.ETag = EntityTagHeaderValue.Parse(blob.ETag);
        return response;
    }

    private HttpResponseMessage Write(
        string container, SortedDictionary<string, Blob> blobs, string name, IReadOnlyDictionary<string, string> query,
        IReadOnlyDictionary<string, string> headers, byte[] body)
    {
        var comp = query.GetValueOrDefault("comp");
        if (comp == "block")
        {
            if (!blobs.TryGetValue(name, out var target) && !_uncommitted.TryGetValue((container, name), out target))
            {
                // Staged blocks belong to a blob that does not exist yet; keep them aside.
                target = new Blob { ETag = "\"uncommitted\"" };
                _uncommitted[(container, name)] = target;
            }

            target.Staged[query["blockid"]] = body;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        if (comp == "copy")
        {
            if (blobs.TryGetValue(name, out var copying) && copying.CopyId == query.GetValueOrDefault("copyid"))
            {
                copying.CopyStatus = "aborted";
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        if (headers.GetValueOrDefault("If-None-Match") == "*" && blobs.ContainsKey(name))
        {
            return Error(HttpStatusCode.Conflict, "BlobAlreadyExists");
        }

        if (Hierarchical && name.EndsWith('/'))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidUri");
        }

        if (comp == "blocklist")
        {
            var staged = blobs.TryGetValue(name, out var existing) ? existing.Staged
                : _uncommitted.Remove((container, name), out var pending) ? pending.Staged : [];
            var ids = XElement.Parse(Encoding.UTF8.GetString(body)).Elements("Latest").Select(e => e.Value).ToList();
            if (ids.Any(id => !staged.ContainsKey(id)))
            {
                return Error(HttpStatusCode.BadRequest, "InvalidBlockList");
            }

            Store(container, name, ids.SelectMany(id => staged[id]).ToArray(), null);
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        if (headers.TryGetValue("x-ms-copy-source", out var source))
        {
            var (sourceContainer, sourceName) = Split(new Uri(source).AbsolutePath);
            if (sourceContainer is null || !Containers.TryGetValue(sourceContainer, out var sourceBlobs)
                || !sourceBlobs.TryGetValue(sourceName, out var original))
            {
                return Error(HttpStatusCode.NotFound, "CannotVerifyCopySource");
            }

            var copy = Store(container, name, original.Data, original.Metadata);
            copy.CopyId = Guid.NewGuid().ToString();
            copy.CopyStatus = CopyLooks > 0 ? "pending" : "success";
            copy.PendingLooks = CopyLooks;
            var accepted = new HttpResponseMessage(HttpStatusCode.Accepted);
            accepted.Headers.TryAddWithoutValidation("x-ms-copy-id", copy.CopyId);
            accepted.Headers.TryAddWithoutValidation("x-ms-copy-status", copy.CopyStatus);
            return accepted;
        }

        var metadata = headers.Where(h => h.Key.StartsWith("x-ms-meta-", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key["x-ms-meta-".Length..], h => h.Value);
        Store(container, name, body, metadata);
        return new HttpResponseMessage(HttpStatusCode.Created);
    }

    private readonly Dictionary<(string, string), Blob> _uncommitted = [];

    private HttpResponseMessage Delete(SortedDictionary<string, Blob> blobs, string name)
    {
        if (!blobs.TryGetValue(name, out var blob))
        {
            return Error(HttpStatusCode.NotFound, "BlobNotFound");
        }

        if (Hierarchical && blob.IsDirectory && blobs.Keys.Any(k => k.StartsWith(name + "/", StringComparison.Ordinal)))
        {
            return Error(HttpStatusCode.Conflict, "DirectoryIsNotEmpty");
        }

        blobs.Remove(name);
        return new HttpResponseMessage(HttpStatusCode.Accepted);
    }

    private Blob Store(string container, string name, byte[] data, IReadOnlyDictionary<string, string>? metadata)
    {
        var blobs = Container(container);
        if (Hierarchical)
        {
            // A hierarchical account makes the parent directories of whatever is written.
            for (var slash = name.IndexOf('/'); slash > 0; slash = name.IndexOf('/', slash + 1))
            {
                var parent = name[..slash];
                if (!blobs.ContainsKey(parent))
                {
                    var directory = new Blob { ETag = NextETag() };
                    directory.Metadata["hdi_isfolder"] = "true";
                    blobs[parent] = directory;
                }
            }
        }

        var blob = new Blob { Data = data, ETag = NextETag() };
        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
            {
                blob.Metadata[key] = value;
            }
        }

        blobs[name] = blob;
        return blob;
    }

    private string NextETag() => $"\"0x{++_etag:X}\"";

    /// <summary>Container and blob from a request path, decoded segment by segment.</summary>
    private (string? Container, string Blob) Split(string absolutePath)
    {
        var segments = absolutePath.Split('/').Skip(1).Select(Uri.UnescapeDataString).ToList();
        if (_accountInPath is not null && segments.Count > 0 && segments[0] == _accountInPath)
        {
            segments.RemoveAt(0);
        }

        if (segments.Count == 0 || segments[0].Length == 0)
        {
            return (null, string.Empty);
        }

        return (segments[0], string.Join('/', segments.Skip(1)));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            var name = Uri.UnescapeDataString(equals < 0 ? pair : pair[..equals]);
            result[name] = equals < 0 ? string.Empty : Uri.UnescapeDataString(pair[(equals + 1)..]);
        }

        return result;
    }

    private static HttpResponseMessage Xml(XElement root) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?>" + root, Encoding.UTF8, "application/xml"),
        };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, bool head = false, string? detail = null)
    {
        var response = new HttpResponseMessage(status);
        response.Headers.TryAddWithoutValidation("x-ms-error-code", code);
        if (!head)
        {
            var error = new XElement("Error", new XElement("Code", code), new XElement("Message", $"{code}\nRequestId:0\nTime:0"));
            if (detail is not null)
            {
                error.Add(new XElement("AuthenticationErrorDetail", detail));
            }

            response.Content = new StringContent(error.ToString(), Encoding.UTF8, "application/xml");
        }

        return response;
    }
}
