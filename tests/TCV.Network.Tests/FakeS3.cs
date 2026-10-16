using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace TCV.Network.Tests;

/// <summary>
/// An S3 endpoint in memory, as an <see cref="HttpMessageHandler"/>: enough of the API —
/// ListBuckets, ListObjectsV2 with paging, objects with ranges and conditions, CopyObject,
/// DeleteObjects, multipart — to run <see cref="S3FileSystem"/> end to end. It checks what a real
/// store checks and a client gets wrong: the body hash against <c>x-amz-content-sha256</c>, the
/// <c>Content-MD5</c> of DeleteObjects, the ETags of the parts.
/// </summary>
internal sealed class FakeS3 : HttpMessageHandler
{
    public static readonly DateTimeOffset Modified = new(2024, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static readonly XNamespace Ns = "http://s3.amazonaws.com/doc/2006-03-01/";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, SortedDictionary<int, byte[]>> _uploads = new(StringComparer.Ordinal);
    private int _uploadCounter;

    public FakeS3(string endpoint = "s3.example.com")
    {
        Endpoint = endpoint;
    }

    public string Endpoint { get; }

    public SortedDictionary<string, SortedDictionary<string, byte[]>> Buckets { get; } = new(StringComparer.Ordinal);

    public List<Seen> Requests { get; } = [];

    /// <summary>Keys per ListObjectsV2 page, at most.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>False: DeleteObjects answers 501, as Google Cloud Storage's S3 interface does.</summary>
    public bool BulkDeleteSupported { get; set; } = true;

    /// <summary>True: an <c>If-None-Match: *</c> write is refused with 501.</summary>
    public bool RejectsIfNoneMatch { get; set; }

    /// <summary>Answers a request before the store does; null lets the store answer.</summary>
    public Func<Seen, CancellationToken, Task<HttpResponseMessage?>>? Intercept { get; set; }

    public int PendingUploads
    {
        get
        {
            lock (_gate)
            {
                return _uploads.Count;
            }
        }
    }

    public void On(Func<Seen, HttpResponseMessage?> intercept) =>
        Intercept = (seen, _) => Task.FromResult(intercept(seen));

    public void Put(string bucket, string key, string content) => Put(bucket, key, Encoding.UTF8.GetBytes(content));

    public void Put(string bucket, string key, byte[] content)
    {
        lock (_gate)
        {
            BucketOf(bucket)[key] = content;
        }
    }

    public string? Text(string bucket, string key)
    {
        lock (_gate)
        {
            return Buckets.TryGetValue(bucket, out var objects) && objects.TryGetValue(key, out var data)
                ? Encoding.UTF8.GetString(data)
                : null;
        }
    }

    public IReadOnlyList<string> Keys(string bucket)
    {
        lock (_gate)
        {
            return Buckets.TryGetValue(bucket, out var objects) ? objects.Keys.ToList() : [];
        }
    }

    public static string ETagOf(byte[] data) => "\"" + Convert.ToHexStringLower(MD5.HashData(data)) + "\"";

    public static HttpResponseMessage Error(HttpStatusCode status, string code, string message, string? region = null)
    {
        var error = new XElement("Error", new XElement("Code", code), new XElement("Message", message));
        if (region is not null)
        {
            error.Add(new XElement("Region", region));
        }

        error.Add(new XElement("RequestId", "TEST"));
        return Xml(status, error);
    }

    public sealed record Seen(
        HttpMethod Method, Uri Uri, string? Bucket, string Key,
        IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;

        public string? Param(string name) => Query.TryGetValue(name, out var value) ? value : null;

        /// <summary>The signing region named in the Authorization header.</summary>
        public string? SignedRegion => Header("Authorization")?.Split('/') is { Length: > 2 } parts ? parts[2] : null;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
        }

        var uri = request.RequestUri!;
        var (bucket, key) = Route(uri);
        var seen = new Seen(request.Method, uri, bucket, key, ParseQuery(uri.Query), headers, body);
        lock (_gate)
        {
            Requests.Add(seen);
        }

        if (Intercept is not null && await Intercept(seen, ct) is { } scripted)
        {
            return scripted;
        }

        HttpResponseMessage response;
        lock (_gate)
        {
            response = Handle(seen);
        }

        // A HEAD answer has headers only, the error body included.
        if (seen.Method == HttpMethod.Head && !response.IsSuccessStatusCode)
        {
            response.Content = new ByteArrayContent([]);
        }

        return response;
    }

    private (string? Bucket, string Key) Route(Uri uri)
    {
        var path = uri.AbsolutePath;
        if (uri.Host.EndsWith("." + Endpoint, StringComparison.Ordinal))
        {
            return (uri.Host[..^(Endpoint.Length + 1)], Uri.UnescapeDataString(path.Length > 0 ? path[1..] : path));
        }

        var rest = path.TrimStart('/');
        if (rest.Length == 0)
        {
            return (null, string.Empty);
        }

        var slash = rest.IndexOf('/');
        return slash < 0
            ? (Uri.UnescapeDataString(rest), string.Empty)
            : (Uri.UnescapeDataString(rest[..slash]), Uri.UnescapeDataString(rest[(slash + 1)..]));
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            result[Uri.UnescapeDataString(equals < 0 ? part : part[..equals])] =
                equals < 0 ? string.Empty : Uri.UnescapeDataString(part[(equals + 1)..]);
        }

        return result;
    }

    private HttpResponseMessage Handle(Seen r)
    {
        if (r.Body.Length > 0 && r.Header("x-amz-content-sha256") is { } claimed
            && claimed != Convert.ToHexStringLower(SHA256.HashData(r.Body)))
        {
            return Error(HttpStatusCode.BadRequest, "XAmzContentSHA256Mismatch", "The provided 'x-amz-content-sha256' header does not match what was computed.");
        }

        if (r.Bucket is null)
        {
            return r.Method == HttpMethod.Get ? ListBuckets() : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        if (!Buckets.TryGetValue(r.Bucket, out var objects))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchBucket", "The specified bucket does not exist");
        }

        if (r.Key.Length == 0)
        {
            if (r.Method == HttpMethod.Head)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            }

            if (r.Method == HttpMethod.Get && r.Param("list-type") == "2")
            {
                return List(r.Bucket, objects, r);
            }

            if (r.Method == HttpMethod.Post && r.Query.ContainsKey("delete"))
            {
                return BulkDelete(objects, r);
            }

            return Error(HttpStatusCode.BadRequest, "InvalidRequest", "Not supported by the fake.");
        }

        if (r.Method == HttpMethod.Head)
        {
            return objects.TryGetValue(r.Key, out var data)
                ? ObjectResponse(HttpStatusCode.OK, data, [])
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (r.Method == HttpMethod.Get)
        {
            return GetObject(objects, r);
        }

        if (r.Method == HttpMethod.Put)
        {
            if (r.Param("partNumber") is { } number)
            {
                return UploadPart(r, int.Parse(number, CultureInfo.InvariantCulture));
            }

            return r.Header("x-amz-copy-source") is { } source ? CopyObject(objects, r, source) : PutObject(objects, r);
        }

        if (r.Method == HttpMethod.Post)
        {
            if (r.Query.ContainsKey("uploads"))
            {
                var id = "upload-" + (++_uploadCounter).ToString(CultureInfo.InvariantCulture);
                _uploads[id] = [];
                return Xml(HttpStatusCode.OK, new XElement(Ns + "InitiateMultipartUploadResult",
                    new XElement(Ns + "Bucket", r.Bucket), new XElement(Ns + "Key", r.Key), new XElement(Ns + "UploadId", id)));
            }

            if (r.Param("uploadId") is { } uploadId)
            {
                return Complete(objects, r, uploadId);
            }
        }

        if (r.Method == HttpMethod.Delete)
        {
            if (r.Param("uploadId") is { } uploadId)
            {
                _uploads.Remove(uploadId);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            objects.Remove(r.Key);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        return Error(HttpStatusCode.BadRequest, "InvalidRequest", "Not supported by the fake.");
    }

    private HttpResponseMessage ListBuckets() =>
        Xml(HttpStatusCode.OK, new XElement(Ns + "ListAllMyBucketsResult",
            new XElement(Ns + "Buckets", Buckets.Keys.Select(name => new XElement(Ns + "Bucket",
                new XElement(Ns + "Name", name),
                new XElement(Ns + "CreationDate", Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)))))));

    private HttpResponseMessage List(string bucket, SortedDictionary<string, byte[]> objects, Seen r)
    {
        var prefix = r.Param("prefix") ?? string.Empty;
        var delimiter = r.Param("delimiter");
        var encode = r.Param("encoding-type") == "url";
        var max = int.TryParse(r.Param("max-keys"), out var m) ? Math.Min(m, PageSize) : PageSize;

        var entries = new List<(string Name, bool IsPrefix)>();
        foreach (var key in objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var cut = string.IsNullOrEmpty(delimiter) ? -1 : key.IndexOf(delimiter, prefix.Length, StringComparison.Ordinal);
            if (cut >= 0)
            {
                var common = key[..(cut + delimiter!.Length)];
                if (entries.Count == 0 || entries[^1].Name != common)
                {
                    entries.Add((common, true));
                }

                continue;
            }

            entries.Add((key, false));
        }

        var start = 0;
        if (r.Param("continuation-token") is { } token)
        {
            var after = Encoding.UTF8.GetString(Convert.FromBase64String(token));
            start = entries.FindIndex(e => string.CompareOrdinal(e.Name, after) > 0);
            start = start < 0 ? entries.Count : start;
        }

        var page = entries.Skip(start).Take(max).ToList();
        var truncated = start + page.Count < entries.Count;
        string Encode(string text) => encode ? WebUtility.UrlEncode(text) : text;

        var result = new XElement(Ns + "ListBucketResult",
            new XElement(Ns + "Name", bucket),
            new XElement(Ns + "Prefix", Encode(prefix)),
            new XElement(Ns + "KeyCount", page.Count),
            new XElement(Ns + "MaxKeys", max));
        if (encode)
        {
            result.Add(new XElement(Ns + "EncodingType", "url"));
        }

        result.Add(new XElement(Ns + "IsTruncated", truncated ? "true" : "false"));
        if (truncated)
        {
            result.Add(new XElement(Ns + "NextContinuationToken", Convert.ToBase64String(Encoding.UTF8.GetBytes(page[^1].Name))));
        }

        foreach (var (name, isPrefix) in page)
        {
            result.Add(isPrefix
                ? new XElement(Ns + "CommonPrefixes", new XElement(Ns + "Prefix", Encode(name)))
                : new XElement(Ns + "Contents",
                    new XElement(Ns + "Key", Encode(name)),
                    new XElement(Ns + "LastModified", Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                    new XElement(Ns + "ETag", ETagOf(objects[name])),
                    new XElement(Ns + "Size", objects[name].Length),
                    new XElement(Ns + "StorageClass", "STANDARD")));
        }

        return Xml(HttpStatusCode.OK, result);
    }

    private HttpResponseMessage GetObject(SortedDictionary<string, byte[]> objects, Seen r)
    {
        if (!objects.TryGetValue(r.Key, out var data))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");
        }

        if (r.Header("If-Match") is { } match && match != ETagOf(data))
        {
            return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
        }

        if (r.Header("Range") is { } range && range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-'))
        {
            var from = long.Parse(range[6..^1], CultureInfo.InvariantCulture);
            if (from >= data.Length)
            {
                return Error(HttpStatusCode.RequestedRangeNotSatisfiable, "InvalidRange", "The requested range is not satisfiable");
            }

            var response = ObjectResponse(HttpStatusCode.PartialContent, data, data[(int)from..]);
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return response;
        }

        return ObjectResponse(HttpStatusCode.OK, data, data);
    }

    private HttpResponseMessage PutObject(SortedDictionary<string, byte[]> objects, Seen r)
    {
        if (r.Header("If-None-Match") == "*")
        {
            if (RejectsIfNoneMatch)
            {
                return Error(HttpStatusCode.NotImplemented, "NotImplemented", "A header you provided implies functionality that is not implemented");
            }

            if (objects.ContainsKey(r.Key))
            {
                return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
            }
        }

        objects[r.Key] = r.Body;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", ETagOf(r.Body));
        return response;
    }

    private HttpResponseMessage CopyObject(SortedDictionary<string, byte[]> objects, Seen r, string source)
    {
        var decoded = Uri.UnescapeDataString(source).TrimStart('/');
        var slash = decoded.IndexOf('/');
        if (slash < 0 || !Buckets.TryGetValue(decoded[..slash], out var sourceObjects)
                      || !sourceObjects.TryGetValue(decoded[(slash + 1)..], out var data))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchKey", "The specified key does not exist.");
        }

        objects[r.Key] = data.ToArray();
        return Xml(HttpStatusCode.OK, new XElement(Ns + "CopyObjectResult",
            new XElement(Ns + "LastModified", Modified.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
            new XElement(Ns + "ETag", ETagOf(data))));
    }

    private HttpResponseMessage UploadPart(Seen r, int number)
    {
        if (r.Param("uploadId") is not { } id || !_uploads.TryGetValue(id, out var parts))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.");
        }

        parts[number] = r.Body;
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        response.Headers.TryAddWithoutValidation("ETag", ETagOf(r.Body));
        return response;
    }

    private HttpResponseMessage Complete(SortedDictionary<string, byte[]> objects, Seen r, string uploadId)
    {
        if (!_uploads.TryGetValue(uploadId, out var parts))
        {
            return Error(HttpStatusCode.NotFound, "NoSuchUpload", "The specified upload does not exist.");
        }

        if (r.Header("If-None-Match") == "*" && objects.ContainsKey(r.Key))
        {
            return Error(HttpStatusCode.PreconditionFailed, "PreconditionFailed", "At least one of the pre-conditions you specified did not hold");
        }

        var listed = XDocument.Parse(Encoding.UTF8.GetString(r.Body)).Root!.Elements()
            .Where(e => e.Name.LocalName == "Part")
            .Select(e => (
                Number: int.Parse(e.Elements().First(c => c.Name.LocalName == "PartNumber").Value, CultureInfo.InvariantCulture),
                ETag: e.Elements().First(c => c.Name.LocalName == "ETag").Value))
            .ToList();
        if (listed.Count != parts.Count || listed.Any(p => !parts.TryGetValue(p.Number, out var data) || ETagOf(data) != p.ETag))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidPart", "One or more of the specified parts could not be found.");
        }

        objects[r.Key] = listed.SelectMany(p => parts[p.Number]).ToArray();
        _uploads.Remove(uploadId);
        return Xml(HttpStatusCode.OK, new XElement(Ns + "CompleteMultipartUploadResult",
            new XElement(Ns + "Key", r.Key), new XElement(Ns + "ETag", "\"multipart\"")));
    }

    private HttpResponseMessage BulkDelete(SortedDictionary<string, byte[]> objects, Seen r)
    {
        if (!BulkDeleteSupported)
        {
            return Error(HttpStatusCode.NotImplemented, "NotImplemented", "A header you provided implies functionality that is not implemented");
        }

        if (r.Header("Content-MD5") != Convert.ToBase64String(MD5.HashData(r.Body)))
        {
            return Error(HttpStatusCode.BadRequest, "InvalidRequest", "Missing required header for this request: Content-MD5");
        }

        var keys = XDocument.Parse(Encoding.UTF8.GetString(r.Body)).Root!.Elements()
            .Where(e => e.Name.LocalName == "Object")
            .Select(e => e.Elements().First(c => c.Name.LocalName == "Key").Value);
        foreach (var key in keys)
        {
            objects.Remove(key);
        }

        return Xml(HttpStatusCode.OK, new XElement(Ns + "DeleteResult"));
    }

    private static HttpResponseMessage ObjectResponse(HttpStatusCode status, byte[] whole, byte[] body)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentLength = status == HttpStatusCode.OK && body.Length == 0 ? whole.Length : body.Length;
        response.Content.Headers.LastModified = Modified;
        response.Headers.TryAddWithoutValidation("ETag", ETagOf(whole));
        return response;
    }

    private static HttpResponseMessage Xml(HttpStatusCode status, XElement root) =>
        new(status)
        {
            Content = new StringContent(
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" + root.ToString(SaveOptions.DisableFormatting),
                Encoding.UTF8, "application/xml"),
        };

    private SortedDictionary<string, byte[]> BucketOf(string bucket)
    {
        if (!Buckets.TryGetValue(bucket, out var objects))
        {
            objects = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            Buckets[bucket] = objects;
        }

        return objects;
    }
}
