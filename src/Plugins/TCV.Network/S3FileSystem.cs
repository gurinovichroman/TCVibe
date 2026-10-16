using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// Amazon S3 and the S3-compatible stores (R2, B2, MinIO, Wasabi…) as a virtual file system,
/// <c>s3://</c>: buckets and key prefixes are folders, objects are files. Plain
/// <see cref="HttpClient"/> and an own SigV4 signer (<see cref="S3Signer"/>), no AWS SDK.
/// </summary>
/// <remarks>
/// <para>
/// With a bucket in the connection the root is that bucket and a path is a key; without one the root
/// lists the buckets and the first segment of a path names the bucket.
/// </para>
/// <para>
/// S3 has no folders. A folder is a prefix that objects are stored under, or the zero-byte marker
/// <c>prefix/</c> that <see cref="CreateDirectoryAsync"/> leaves (the AWS console's convention). So
/// renaming or moving one copies and deletes every object under it, server-side.
/// </para>
/// <para>
/// Asynchronous all the way down, like <see cref="WebDavFileSystem"/>; the XML lives in
/// <see cref="S3Response"/> and the addressing in <see cref="S3Endpoint"/>.
/// </para>
/// </remarks>
public sealed class S3FileSystem : IVirtualFileSystem, IRangeReader, IReadLinkSource
{
    private const string FallbackRegion = "us-east-1";
    private const string BucketRegionHeader = "x-amz-bucket-region";
    private const int MaxParts = 10_000;

    /// <summary>DeleteObjects takes at most this many keys.</summary>
    private const int DeleteBatchSize = 1000;

    /// <summary>Server-side copies and single deletes in flight at once when a folder is moved or emptied.</summary>
    private const int Parallelism = 8;

    private static readonly KeyValuePair<string, string> IfNoneMatchAny = new("If-None-Match", "*");

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly S3Endpoint _endpoint;
    private readonly string? _bucket;
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly string? _profile;
    private readonly bool _regionGiven;

    /// <summary>Buckets found to live elsewhere than the connection says: their region and endpoint host.</summary>
    private readonly ConcurrentDictionary<string, BucketLocation> _locations = new(StringComparer.Ordinal);

    private readonly Lock _credentialsGate = new();
    private S3Credentials? _credentials;
    private bool _credentialsResolved;
    private string _region;
    private volatile bool _bulkDeleteUnsupported;
    private bool _disposed;

    /// <summary>Connects to <paramref name="connection"/>: endpoint, bucket, region, keys or an AWS profile.</summary>
    public S3FileSystem(ConnectionInfo connection)
        : this(connection, CreateClient(), ownsClient: true)
    {
    }

    /// <summary>Test seam: runs against a caller-supplied client without opening a socket.</summary>
    internal S3FileSystem(ConnectionInfo connection, HttpClient client)
        : this(connection, client, ownsClient: false)
    {
    }

    private S3FileSystem(ConnectionInfo connection, HttpClient client, bool ownsClient)
    {
        ArgumentNullException.ThrowIfNull(connection);

        _endpoint = S3Endpoint.From(connection);
        _client = client;
        _ownsClient = ownsClient;
        _bucket = string.IsNullOrWhiteSpace(connection.Bucket) ? null : connection.Bucket.Trim().Trim('/');
        _accessKey = connection.Username?.Trim() ?? string.Empty;
        _secretKey = connection.Password ?? string.Empty;
        _profile = string.IsNullOrWhiteSpace(connection.Profile) ? null : connection.Profile.Trim();
        _regionGiven = !string.IsNullOrWhiteSpace(connection.Region);
        _region = _regionGiven ? connection.Region!.Trim() : FallbackRegion;

        var defaultPort = _endpoint.Scheme == "http" ? 80 : 443;
        DisplayName = _bucket ?? (_endpoint.Port == defaultPort ? _endpoint.Host : $"{_endpoint.Host}:{_endpoint.Port}");
    }

    public string Scheme => "s3";

    /// <summary>The bucket when the connection is limited to one, else the endpoint host.</summary>
    public string DisplayName { get; }

    /// <summary>
    /// How long a listing, stat, delete or folder creation may take before it is called failed.
    /// Transfers and server-side copies are not bound by it: they take as long as the bytes take.
    /// </summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Bodies above this go up in parts: one PUT of a huge file is lost whole by one dropped connection.</summary>
    internal long MultipartThreshold { get; init; } = 64L * 1024 * 1024;

    /// <summary>The smallest part; S3 refuses parts under 5 MiB except the last.</summary>
    internal long MinPartSize { get; init; } = 8L * 1024 * 1024;

    /// <summary>CopyObject refuses sources over 5 GiB; larger objects are streamed through instead.</summary>
    internal long MaxServerSideCopy { get; init; } = 5L * 1024 * 1024 * 1024;

    /// <summary>The time requests are signed with.</summary>
    internal Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>The AWS credentials file a profile is read from; null for the CLI's own location.</summary>
    internal string? CredentialsFilePath { get; init; }

    /// <summary>The AWS config file the profile's region is read from; null for the CLI's own location.</summary>
    internal string? ConfigFilePath { get; init; }

    public event EventHandler<VfsChangedEventArgs>? Changed;

    // ── Reading ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var (bucket, key) = Locate(path);
        if (bucket is null)
        {
            return (await ListBucketsAsync(ct))
                .Select(b => (IFileEntry)new NetworkFileEntry(new VfsPath("/" + b.Name), b.Name, true, 0, b.Created))
                .ToList();
        }

        var prefix = key.Length == 0 ? string.Empty : key + "/";
        var parent = path.Value.TrimEnd('/');
        var entries = new List<IFileEntry>();
        string? token = null;
        do
        {
            var page = await ListPageAsync(bucket, prefix, delimited: true, token, maxKeys: null, ct);
            foreach (var folder in page.Prefixes)
            {
                var name = folder.StartsWith(prefix, StringComparison.Ordinal) ? folder[prefix.Length..].TrimEnd('/') : string.Empty;
                if (IsShowable(name))
                {
                    entries.Add(new NetworkFileEntry(new VfsPath(parent + "/" + name), name, true, 0, DateTime.MinValue));
                }
            }

            foreach (var item in page.Objects)
            {
                // The marker a "new folder" leaves is the folder itself, not a nameless file in it.
                if (item.Key == prefix || !item.Key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var name = item.Key[prefix.Length..];
                if (IsShowable(name))
                {
                    entries.Add(new NetworkFileEntry(new VfsPath(parent + "/" + name), name, false, item.Size, item.LastModified));
                }
            }

            token = NextToken(page, token);
        }
        while (token is not null);

        return entries;
    }

    public async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var (bucket, key) = Locate(path);
        if (bucket is null)
        {
            // The mount asks for the root to prove the endpoint and the keys work; for a connection that
            // lists buckets, listing them is that proof.
            await ListBucketsAsync(ct);
            return RootEntry();
        }

        if (key.Length == 0)
        {
            return await BucketEntryAsync(bucket, path, ct);
        }

        var info = await HeadObjectAsync(bucket, key, ct);
        if (info is not null)
        {
            return new NetworkFileEntry(path, path.FileName, false, info.Size, info.LastModified);
        }

        // No object of that name: a folder is anything stored under "key/".
        return await PrefixExistsAsync(bucket, key + "/", ct)
            ? new NetworkFileEntry(path, path.FileName, true, 0, DateTime.MinValue)
            : null;
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var (bucket, key) = RequireObject(path);
        var call = new S3Call(HttpMethod.Get, bucket, key) { Kind = CallKind.Download };
        var response = await SendAsync(call, ct);
        try
        {
            await EnsureSuccessAsync(response, call, ct);
            return await response.Content.ReadAsStreamAsync(ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> at <paramref name="offset"/> with a <c>Range</c> request, on
    /// condition (<c>If-Match</c>) that the object still has the ETag <paramref name="validator"/>. An
    /// object that changed (412) or has nothing past the offset (416) is sent whole, from 0.
    /// </summary>
    public async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct)
    {
        var (bucket, key) = RequireObject(path);
        if (offset > 0)
        {
            var headers = new List<KeyValuePair<string, string>>
            {
                new("Range", $"bytes={offset.ToString(CultureInfo.InvariantCulture)}-"),
            };
            if (!string.IsNullOrEmpty(validator))
            {
                headers.Add(new("If-Match", validator));
            }

            var ranged = new S3Call(HttpMethod.Get, bucket, key) { Headers = headers, Kind = CallKind.Download };
            var response = await SendAsync(ranged, ct);
            if (response.StatusCode is not (HttpStatusCode.PreconditionFailed or HttpStatusCode.RequestedRangeNotSatisfiable))
            {
                return await OpenedAsync(response, ranged, offset, ct);
            }

            // The rest of a changed object spliced onto the start of the old one would be neither.
            response.Dispose();
        }

        var whole = new S3Call(HttpMethod.Get, bucket, key) { Kind = CallKind.Download };
        return await OpenedAsync(await SendAsync(whole, ct), whole, 0, ct);
    }

    private async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenedAsync(
        HttpResponseMessage response, S3Call call, long offset, CancellationToken ct)
    {
        try
        {
            await EnsureSuccessAsync(response, call, ct);
            var resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var length = response.Content.Headers.ContentLength ?? 0;
            var total = response.Content.Headers.ContentRange?.Length
                        ?? (length > 0 ? (resumed ? offset + length : length) : 0);
            var etag = ETagOf(response);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return (stream, resumed ? offset : 0, total, etag);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A link that downloads the object at <paramref name="path"/> without credentials until
    /// <paramref name="expiry"/> (at most seven days) has passed. Computed here, no request: it is
    /// signed for the region the bucket was last seen in, so before the first request to a bucket in
    /// another region it names the connection's region. Copy it as <see cref="Uri.AbsoluteUri"/> —
    /// <see cref="Uri.ToString"/> unescapes the signature's query.
    /// </summary>
    /// <exception cref="InvalidOperationException">The connection has no keys to sign with.</exception>
    /// <exception cref="NotSupportedException"><paramref name="path"/> is the root or a bucket, not an object.</exception>
    public Uri Presign(VfsPath path, TimeSpan expiry)
    {
        var (bucket, key) = RequireObject(path);
        var credentials = Credentials()
                          ?? throw new InvalidOperationException("The connection has no access key, so it cannot sign a link.");
        var location = Location(bucket);
        return S3Signer.Presign(
            "GET", _endpoint.BuildUri(bucket, key, [], location.Host), credentials, location.Region, Clock(), expiry);
    }

    /// <summary>True when the connection has keys to sign a link with: typed in, or from its AWS profile.</summary>
    public bool CanCreateReadLink => Credentials() is not null;

    Uri IReadLinkSource.CreateReadLink(VfsPath path, TimeSpan lifetime) => Presign(path, lifetime);

    // ── Writing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A write stream that uploads when committed or disposed: the body is collected in a temporary
    /// file (S3 wants the length and the SHA-256 of a body before it), then sent in one PUT, or in parts
    /// above <see cref="MultipartThreshold"/>. An existing object is never replaced without
    /// <paramref name="overwrite"/>: checked here, and again by the store at the upload.
    /// </summary>
    public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        var (bucket, key) = RequireObject(path);

        // Asked before a byte is collected, and asked again with If-None-Match when the body goes up:
        // not every S3-compatible store honours that header.
        if (!overwrite && await HeadObjectAsync(bucket, key, ct) is not null)
        {
            throw new IOException($"\"{path.FileName}\" already exists.");
        }

        return new S3UploadStream(this, bucket, key, overwrite, path, ct);
    }

    /// <summary>Sends a collected body; called by <see cref="S3UploadStream"/>.</summary>
    internal async Task UploadAsync(
        FileStream body, string sha256, string bucket, string key, bool overwrite, VfsPath path,
        IProgress<long>? progress, CancellationToken ct)
    {
        if (body.Length <= MultipartThreshold)
        {
            await PutObjectAsync(body, sha256, bucket, key, overwrite, path.FileName, progress, ct);
        }
        else
        {
            await PutMultipartAsync(body, bucket, key, overwrite, path.FileName, progress, ct);
        }

        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Created));
    }

    private async Task PutObjectAsync(
        FileStream body, string sha256, string bucket, string key, bool overwrite, string name,
        IProgress<long>? progress, CancellationToken ct)
    {
        var call = new S3Call(HttpMethod.Put, bucket, key)
        {
            Headers = overwrite ? [] : [IfNoneMatchAny],
            Body = () =>
            {
                body.Position = 0;
                var content = new ProgressStreamContent(body, progress);
                content.Headers.ContentType = ContentTypeFor(key);
                content.Headers.ContentLength = body.Length;
                return content;
            },
            PayloadHash = sha256,
            Kind = CallKind.Upload,
        };
        using var response = await SendConditionalAsync(call, name, ct);
    }

    private async Task PutMultipartAsync(
        FileStream body, string bucket, string key, bool overwrite, string name,
        IProgress<long>? progress, CancellationToken ct)
    {
        var initiate = new S3Call(HttpMethod.Post, bucket, key)
        {
            Query = [new("uploads", string.Empty)],
            Body = () => new ByteArrayContent([]) { Headers = { ContentType = ContentTypeFor(key) } },
        };
        string uploadId;
        using (var response = await SendAsync(initiate, ct))
        {
            await EnsureSuccessAsync(response, initiate, ct);
            uploadId = S3Response.ParseUploadId(await response.Content.ReadAsStringAsync(ct));
        }

        try
        {
            var parts = new List<(int Number, string ETag)>();
            var partSize = PartSize(body.Length, MinPartSize);
            var number = 0;
            for (long offset = 0; offset < body.Length; offset += partSize)
            {
                ct.ThrowIfCancellationRequested();
                number++;
                var start = offset;
                var size = Math.Min(partSize, body.Length - offset);
                var hash = await HashRangeAsync(body, start, size, ct);
                var sent = progress is null ? null : new InlineProgress<long>(bytes => progress.Report(start + bytes));
                var call = new S3Call(HttpMethod.Put, bucket, key)
                {
                    Query = [new("partNumber", number.ToString(CultureInfo.InvariantCulture)), new("uploadId", uploadId)],
                    Body = () => new ProgressStreamContent(new FileSegmentStream(body, start, size), sent)
                    {
                        Headers = { ContentLength = size },
                    },
                    PayloadHash = hash,
                    Kind = CallKind.Upload,
                };
                using var response = await SendAsync(call, ct);
                await EnsureSuccessAsync(response, call, ct);
                parts.Add((number, ETagOf(response) ?? throw new S3Exception(
                    $"The server did not confirm part {number} of \"{name}\" (no ETag).", response.StatusCode, null)));
            }

            var xml = Encoding.UTF8.GetBytes(S3Response.BuildCompleteBody(parts));
            var complete = new S3Call(HttpMethod.Post, bucket, key)
            {
                Query = [new("uploadId", uploadId)],
                Headers = overwrite ? [] : [IfNoneMatchAny],
                Body = () => new ByteArrayContent(xml) { Headers = { ContentType = new MediaTypeHeaderValue("application/xml") } },
                PayloadHash = S3Signer.Sha256Hex(xml),

                // Assembling gigabytes of parts takes the server a while; the metadata timeout would cut it off.
                Kind = CallKind.Upload,
            };
            using var done = await SendConditionalAsync(complete, name, ct);
            await ThrowIfErrorBodyAsync(done, bucket, key, ct);
        }
        catch
        {
            // Parts of an upload that is neither completed nor aborted stay stored — and billed — out of sight.
            await AbortMultipartAsync(bucket, key, uploadId);
            throw;
        }
    }

    private async Task AbortMultipartAsync(string bucket, string key, string uploadId)
    {
        try
        {
            var call = new S3Call(HttpMethod.Delete, bucket, key) { Query = [new("uploadId", uploadId)] };
            using var response = await SendAsync(call, CancellationToken.None);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            // Best effort: the failure that led here is the one worth reporting. A lifecycle rule on the
            // bucket (AbortIncompleteMultipartUpload) is the safety net for this case.
        }
    }

    /// <summary>
    /// The part size for a body of <paramref name="length"/>: at least <paramref name="minPartSize"/>,
    /// and large enough that the body fits S3's limit of 10 000 parts.
    /// </summary>
    internal static long PartSize(long length, long minPartSize) =>
        Math.Max(minPartSize, (length + MaxParts - 1) / MaxParts);

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        var (bucket, key) = Locate(path);
        if (bucket is null || key.Length == 0)
        {
            throw new NotSupportedException(
                "Buckets are created in the provider's console; create a folder inside a bucket instead.");
        }

        var call = new S3Call(HttpMethod.Put, bucket, key + "/") { Body = () => new ByteArrayContent([]) };
        using var response = await SendAsync(call, ct);
        await EnsureSuccessAsync(response, call, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Created));
    }

    // ── Copy, move, rename ───────────────────────────────────────────────────

    /// <summary>
    /// Copies within this store: server-side (CopyObject), so the bytes never come down; a source over
    /// 5 GiB, which CopyObject refuses, is streamed through. A folder copies every object under it.
    /// </summary>
    public async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var (srcBucket, srcKey) = RequireObject(source);
        var (dstBucket, dstKey) = RequireObject(destination);
        var started = Stopwatch.StartNew();

        var info = await HeadObjectAsync(srcBucket, srcKey, ct);
        if (info is not null)
        {
            await CopyObjectAsync(srcBucket, srcKey, dstBucket, dstKey, info.Size, progress, source.FileName, started, ct);
        }
        else
        {
            RejectInsideItself(srcBucket, srcKey, dstBucket, dstKey, source.FileName);
            if (await TransferPrefixAsync(srcBucket, srcKey + "/", dstBucket, dstKey + "/", deleteSource: false, ct) == 0)
            {
                throw new FileNotFoundException($"\"{source.FileName}\" does not exist.");
            }
        }

        Changed?.Invoke(this, new VfsChangedEventArgs(destination, WatcherChangeTypes.Created));
    }

    /// <summary>Copy and delete: S3 cannot rename. Replaces an object already at <paramref name="destination"/>.</summary>
    public async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var (srcBucket, srcKey) = RequireObject(source);
        var (dstBucket, dstKey) = RequireObject(destination);
        if (srcBucket == dstBucket && srcKey == dstKey)
        {
            return;
        }

        await MoveCoreAsync(srcBucket, srcKey, dstBucket, dstKey, source.FileName, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(source, WatcherChangeTypes.Deleted));
        Changed?.Invoke(this, new VfsChangedEventArgs(destination, WatcherChangeTypes.Created));
    }

    /// <summary>Renames by copy and delete; never replaces an existing object or folder of the new name.</summary>
    public async Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Contains('/') || newName.Contains('\\'))
        {
            throw new ArgumentException($"\"{newName}\" is not a valid name.", nameof(newName));
        }

        var (bucket, key) = RequireObject(path);
        var slash = key.LastIndexOf('/');
        var newKey = (slash < 0 ? string.Empty : key[..(slash + 1)]) + newName;

        // Ordinal: keys are case-sensitive, so "readme" → "README" is a real rename (VfsPath would call them equal).
        if (newKey == key)
        {
            return;
        }

        // A copy onto an existing key replaces it without a word; the check has to be ours.
        if (await HeadObjectAsync(bucket, newKey, ct) is not null || await PrefixExistsAsync(bucket, newKey + "/", ct))
        {
            throw new IOException($"\"{newName}\" already exists.");
        }

        await MoveCoreAsync(bucket, key, bucket, newKey, path.FileName, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Renamed));
    }

    private async Task MoveCoreAsync(
        string srcBucket, string srcKey, string dstBucket, string dstKey, string name, CancellationToken ct)
    {
        var info = await HeadObjectAsync(srcBucket, srcKey, ct);
        if (info is not null)
        {
            await CopyObjectAsync(srcBucket, srcKey, dstBucket, dstKey, info.Size, null, name, Stopwatch.StartNew(), ct);
            await DeleteObjectAsync(srcBucket, srcKey, ct);
            return;
        }

        RejectInsideItself(srcBucket, srcKey, dstBucket, dstKey, name);
        if (await TransferPrefixAsync(srcBucket, srcKey + "/", dstBucket, dstKey + "/", deleteSource: true, ct) == 0)
        {
            throw new FileNotFoundException($"\"{name}\" does not exist.");
        }
    }

    private static void RejectInsideItself(string srcBucket, string srcKey, string dstBucket, string dstKey, string name)
    {
        if (srcBucket == dstBucket && (dstKey + "/").StartsWith(srcKey + "/", StringComparison.Ordinal))
        {
            throw new IOException($"The folder \"{name}\" cannot be copied or moved into itself.");
        }
    }

    /// <summary>
    /// Copies every object under <paramref name="srcPrefix"/> (folder markers included, so empty
    /// subfolders survive), a page at a time. With <paramref name="deleteSource"/> a page is deleted
    /// once all of it has arrived: a failure leaves each object in one place or both, never in neither.
    /// Returns how many objects there were.
    /// </summary>
    private async Task<int> TransferPrefixAsync(
        string srcBucket, string srcPrefix, string dstBucket, string dstPrefix, bool deleteSource, CancellationToken ct)
    {
        var count = 0;
        var started = Stopwatch.StartNew();
        await foreach (var page in ListAllAsync(srcBucket, srcPrefix, ct))
        {
            await Parallel.ForEachAsync(
                page,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
                async (item, token) => await CopyObjectAsync(
                    srcBucket, item.Key, dstBucket, dstPrefix + item.Key[srcPrefix.Length..], item.Size,
                    null, string.Empty, started, token));
            if (deleteSource)
            {
                await DeleteObjectsAsync(srcBucket, page.Select(o => o.Key).ToList(), ct);
            }

            count += page.Count;
        }

        return count;
    }

    private async Task CopyObjectAsync(
        string srcBucket, string srcKey, string dstBucket, string dstKey, long size,
        IProgress<FileProgress>? progress, string name, Stopwatch started, CancellationToken ct)
    {
        if (size > MaxServerSideCopy)
        {
            await StreamCopyAsync(srcBucket, srcKey, dstBucket, dstKey, size, progress, name, started, ct);
            return;
        }

        var call = new S3Call(HttpMethod.Put, dstBucket, dstKey)
        {
            Headers = [new("x-amz-copy-source", "/" + S3Signer.UriEncode(srcBucket) + "/" + S3Endpoint.EncodeKey(srcKey))],
            Body = () => new ByteArrayContent([]),

            // A server-side copy of gigabytes takes minutes; the metadata timeout would call it failed.
            Kind = CallKind.Upload,
        };
        using var response = await SendAsync(call, ct);
        await EnsureSuccessAsync(response, call, ct);
        await ThrowIfErrorBodyAsync(response, dstBucket, dstKey, ct);
        progress?.Report(new FileProgress(size, size, name, started.Elapsed));
    }

    /// <summary>Down and up again, for what CopyObject refuses. Every byte travels twice; each pass counts for half.</summary>
    private async Task StreamCopyAsync(
        string srcBucket, string srcKey, string dstBucket, string dstKey, long size,
        IProgress<FileProgress>? progress, string name, Stopwatch started, CancellationToken ct)
    {
        var get = new S3Call(HttpMethod.Get, srcBucket, srcKey) { Kind = CallKind.Download };
        using var response = await SendAsync(get, ct);
        await EnsureSuccessAsync(response, get, ct);
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var output = new S3UploadStream(this, dstBucket, dstKey, overwrite: true, PathOf(dstBucket, dstKey), ct);
        try
        {
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                progress?.Report(new FileProgress(copied / 2, size, name, started.Elapsed));
            }

            var collected = copied;
            await output.CommitAsync(
                progress is null
                    ? null
                    : new InlineProgress<long>(sent => progress.Report(
                        new FileProgress((collected + sent) / 2, size, name, started.Elapsed))),
                ct);
        }
        catch
        {
            output.Abort();
            throw;
        }
        finally
        {
            await output.DisposeAsync();
        }
    }

    // ── Deleting ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes an object, or a folder: with <paramref name="recursive"/> every object under it (in
    /// batches of DeleteObjects), without only when nothing but its marker is left in it.
    /// </summary>
    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        var (bucket, key) = Locate(path);
        if (bucket is null || key.Length == 0)
        {
            throw new NotSupportedException(bucket is null
                ? "The list of buckets cannot be deleted."
                : $"The bucket \"{bucket}\" is not deleted from here; empty and remove it in the provider's console.");
        }

        if (recursive)
        {
            if (await DeletePrefixAsync(bucket, key + "/", ct) == 0)
            {
                // Nothing under it: an object, or already gone (S3 answers both alike).
                await DeleteObjectAsync(bucket, key, ct);
            }
        }
        else if (await HeadObjectAsync(bucket, key, ct) is not null)
        {
            await DeleteObjectAsync(bucket, key, ct);
        }
        else
        {
            var page = await ListPageAsync(bucket, key + "/", delimited: false, token: null, maxKeys: 2, ct);
            if (page.Objects.Any(o => o.Key != key + "/") || page.Prefixes.Count > 0)
            {
                throw new IOException($"The folder \"{path.FileName}\" is not empty.");
            }

            if (page.Objects.Count > 0)
            {
                await DeleteObjectAsync(bucket, key + "/", ct);
            }
        }

        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Deleted));
    }

    private async Task<int> DeletePrefixAsync(string bucket, string prefix, CancellationToken ct)
    {
        var count = 0;
        await foreach (var page in ListAllAsync(bucket, prefix, ct))
        {
            await DeleteObjectsAsync(bucket, page.Select(o => o.Key).ToList(), ct);
            count += page.Count;
        }

        return count;
    }

    /// <summary>
    /// Deletes <paramref name="keys"/> a thousand per request (DeleteObjects, which wants a
    /// <c>Content-MD5</c>). A store without it — Google Cloud Storage's S3 interface answers 501 — gets
    /// one DELETE per key from then on.
    /// </summary>
    private async Task DeleteObjectsAsync(string bucket, IReadOnlyList<string> keys, CancellationToken ct)
    {
        foreach (var batch in keys.Chunk(DeleteBatchSize))
        {
            ct.ThrowIfCancellationRequested();
            if (_bulkDeleteUnsupported || batch.Length == 1)
            {
                await DeleteEachAsync(bucket, batch, ct);
                continue;
            }

            var body = Encoding.UTF8.GetBytes(S3Response.BuildDeleteBody(batch));
            var md5 = MD5.HashData(body);
            var call = new S3Call(HttpMethod.Post, bucket, string.Empty)
            {
                Query = [new("delete", string.Empty)],
                Body = () => new ByteArrayContent(body)
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/xml"), ContentMD5 = md5 },
                },
                PayloadHash = S3Signer.Sha256Hex(body),
            };
            using var response = await SendAsync(call, ct);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await FailureAsync(response, call, ct);
                if (response.StatusCode != HttpStatusCode.NotImplemented && failure.ErrorCode != "NotImplemented")
                {
                    throw failure;
                }

                _bulkDeleteUnsupported = true;
                await DeleteEachAsync(bucket, batch, ct);
                continue;
            }

            var failures = S3Response.ParseDeleteResult(await response.Content.ReadAsStringAsync(ct));
            if (failures.Count > 0)
            {
                var first = failures[0];
                var reason = S3Exception.Describe(
                    HttpStatusCode.Forbidden, new S3Error(first.Code, first.Message, null), bucket, first.Key, Credentials() is null);
                throw new S3Exception(
                    failures.Count == 1 ? reason : $"{failures.Count} objects were not deleted. The first: {reason}",
                    HttpStatusCode.OK, first.Code);
            }
        }
    }

    private Task DeleteEachAsync(string bucket, IEnumerable<string> keys, CancellationToken ct) =>
        Parallel.ForEachAsync(
            keys,
            new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct },
            async (key, token) => await DeleteObjectAsync(bucket, key, token));

    private async Task DeleteObjectAsync(string bucket, string key, CancellationToken ct)
    {
        var call = new S3Call(HttpMethod.Delete, bucket, key);
        using var response = await SendAsync(call, ct);

        // Already gone is gone: S3 itself answers 204 for that, some compatible stores 404.
        if (response.StatusCode != HttpStatusCode.NotFound)
        {
            await EnsureSuccessAsync(response, call, ct);
        }
    }

    public bool SupportsOperation(VfsOperation operation) => operation switch
    {
        // Everything the panel needs; the recycle bin is a local-disk concept.
        VfsOperation.Read or VfsOperation.Write or VfsOperation.Delete
            or VfsOperation.CreateDirectory or VfsOperation.Rename
            or VfsOperation.Move or VfsOperation.Copy => true,
        _ => false,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    // ── Listing helpers ──────────────────────────────────────────────────────

    private async Task<IReadOnlyList<S3Bucket>> ListBucketsAsync(CancellationToken ct)
    {
        var buckets = new List<S3Bucket>();
        string? token = null;
        do
        {
            ct.ThrowIfCancellationRequested();
            var call = new S3Call(HttpMethod.Get, null, string.Empty)
            {
                Query = token is null ? [] : [new("continuation-token", token)],
            };
            using var response = await SendAsync(call, ct);
            await EnsureSuccessAsync(response, call, ct);
            var (page, next) = S3Response.ParseBuckets(await response.Content.ReadAsStringAsync(ct));
            buckets.AddRange(page);
            token = !string.IsNullOrEmpty(next) && next != token ? next : null;
        }
        while (token is not null);

        return buckets;
    }

    private async Task<S3ListPage> ListPageAsync(
        string bucket, string prefix, bool delimited, string? token, int? maxKeys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var query = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("encoding-type", "url") };
        if (delimited)
        {
            query.Add(new("delimiter", "/"));
        }

        if (prefix.Length > 0)
        {
            query.Add(new("prefix", prefix));
        }

        if (maxKeys is { } max)
        {
            query.Add(new("max-keys", max.ToString(CultureInfo.InvariantCulture)));
        }

        if (token is not null)
        {
            query.Add(new("continuation-token", token));
        }

        var call = new S3Call(HttpMethod.Get, bucket, string.Empty) { Query = query };
        using var response = await SendAsync(call, ct);
        await EnsureSuccessAsync(response, call, ct);
        return S3Response.ParseListObjects(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Every object under <paramref name="prefix"/>, at any depth, a page (up to 1000) at a time.</summary>
    private async IAsyncEnumerable<IReadOnlyList<S3Object>> ListAllAsync(
        string bucket, string prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        do
        {
            var page = await ListPageAsync(bucket, prefix, delimited: false, token, maxKeys: null, ct);
            var objects = page.Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            if (objects.Count > 0)
            {
                yield return objects;
            }

            token = NextToken(page, token);
        }
        while (token is not null);
    }

    /// <summary>The token of the next page; null at the end, or when a store says "more" but gives no new token.</summary>
    private static string? NextToken(S3ListPage page, string? current) =>
        page.IsTruncated && !string.IsNullOrEmpty(page.NextContinuationToken) && page.NextContinuationToken != current
            ? page.NextContinuationToken
            : null;

    private async Task<bool> PrefixExistsAsync(string bucket, string prefix, CancellationToken ct)
    {
        try
        {
            var page = await ListPageAsync(bucket, prefix, delimited: false, token: null, maxKeys: 1, ct);
            return page.Objects.Count > 0 || page.Prefixes.Count > 0;
        }
        catch (S3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // No such bucket: then nothing is under the prefix either.
            return false;
        }
    }

    private async Task<ObjectInfo?> HeadObjectAsync(string bucket, string key, CancellationToken ct)
    {
        var call = new S3Call(HttpMethod.Head, bucket, key);
        using var response = await SendAsync(call, ct);

        // Only "not found" means "not there". A refusal is not an answer to "is it there?" — taken as
        // one, a copy would overwrite blindly.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, call, ct);
        return new ObjectInfo(
            response.Content.Headers.ContentLength ?? 0,
            response.Content.Headers.LastModified?.UtcDateTime.ToLocalTime() ?? DateTime.MinValue);
    }

    private async Task<IFileEntry?> BucketEntryAsync(string bucket, VfsPath path, CancellationToken ct)
    {
        var call = new S3Call(HttpMethod.Head, bucket, string.Empty);
        using var response = await SendAsync(call, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            // A HEAD answer has no body to say what is wrong, and a wrong secret, a wrong region and a
            // missing permission all look alike in it. A one-key listing says which, in words — and when
            // it works, the bucket is there and the HEAD refusal was the store's quirk.
            try
            {
                await ListPageAsync(bucket, string.Empty, delimited: true, token: null, maxKeys: 1, ct);
            }
            catch (S3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        return _bucket is null ? new NetworkFileEntry(path, bucket, true, 0, DateTime.MinValue) : RootEntry();
    }

    // ── Requests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Sends <paramref name="call"/>, signed for the region its bucket lives in. A bucket that turns
    /// out to be elsewhere — the server answers 301 or 400 and names the region in
    /// <c>x-amz-bucket-region</c> or in the error — is remembered there and the request sent once more.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(S3Call call, CancellationToken ct)
    {
        var credentials = Credentials();
        var response = await SendOnceAsync(call, credentials, ct);
        if (call.Bucket is null
            || response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.BadRequest))
        {
            return response;
        }

        var current = Location(call.Bucket);
        string? region;
        try
        {
            region = await RedirectRegionAsync(call, response, credentials, ct);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        if (string.IsNullOrEmpty(region) || string.Equals(region, current.Region, StringComparison.OrdinalIgnoreCase))
        {
            return response;
        }

        response.Dispose();
        _locations[call.Bucket] = new BucketLocation(region, S3Endpoint.RetargetHost(current.Host, current.Region, region));
        return await SendOnceAsync(call, credentials, ct);
    }

    private async Task<string?> RedirectRegionAsync(
        S3Call call, HttpResponseMessage response, S3Credentials? credentials, CancellationToken ct)
    {
        if (RegionHeader(response) is { } named)
        {
            return named;
        }

        // Read into memory so that the caller can still read the error when this was not about the region.
        await response.Content.LoadIntoBufferAsync(ct);
        var error = S3Response.ParseError(await response.Content.ReadAsStringAsync(ct));
        if (error?.Code is "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException"
            && !string.IsNullOrEmpty(error.Region))
        {
            return error.Region;
        }

        // A redirect of a HEAD has no body to name the region in; HEAD on the bucket names it in a header,
        // whatever the answer is.
        if (response.StatusCode == HttpStatusCode.MovedPermanently && !(call.Method == HttpMethod.Head && call.Key.Length == 0))
        {
            using var probe = await SendOnceAsync(new S3Call(HttpMethod.Head, call.Bucket, string.Empty), credentials, ct);
            return RegionHeader(probe);
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendOnceAsync(S3Call call, S3Credentials? credentials, CancellationToken ct)
    {
        var location = Location(call.Bucket);
        using var request = new HttpRequestMessage(
            call.Method, _endpoint.BuildUri(call.Bucket, call.Key, call.Query, location.Host));
        foreach (var (name, value) in call.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        request.Content = call.Body?.Invoke();
        if (request.Content is ProgressStreamContent)
        {
            // The server refuses a bad signature before the body, not after megabytes of it — which
            // would surface as a reset connection instead of the error that says what is wrong.
            request.Headers.ExpectContinue = true;
        }

        // Without keys the request goes unsigned, which is how a public bucket is read.
        if (credentials is not null)
        {
            S3Signer.Sign(request, credentials, location.Region, Clock(), call.PayloadHash);
        }

        if (call.Kind != CallKind.Metadata)
        {
            return await _client.SendAsync(
                request,
                call.Kind == CallKind.Download ? HttpCompletionOption.ResponseHeadersRead : HttpCompletionOption.ResponseContentRead,
                ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The S3 server did not answer within {MetadataTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>
    /// Sends a write that must not replace an existing object. 412 means something is there; 501 means
    /// the store does not know the condition, and the write goes again without it (the object was
    /// checked when the stream was opened).
    /// </summary>
    private async Task<HttpResponseMessage> SendConditionalAsync(S3Call call, string name, CancellationToken ct)
    {
        var response = await SendAsync(call, ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            response.Dispose();
            throw new IOException($"\"{name}\" already exists.");
        }

        if (response.StatusCode == HttpStatusCode.NotImplemented && call.Headers.Contains(IfNoneMatchAny))
        {
            response.Dispose();
            call = call with { Headers = call.Headers.Where(h => !h.Equals(IfNoneMatchAny)).ToList() };
            response = await SendAsync(call, ct);
        }

        try
        {
            await EnsureSuccessAsync(response, call, ct);
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, S3Call call, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, call, ct);
        }
    }

    private async Task<S3Exception> FailureAsync(HttpResponseMessage response, S3Call call, CancellationToken ct)
    {
        string? body = null;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // The status alone has to do.
        }

        var error = S3Response.ParseError(body);
        return new S3Exception(
            S3Exception.Describe(response.StatusCode, error, call.Bucket, call.Key, Credentials() is null),
            response.StatusCode, error?.Code);
    }

    /// <summary>CopyObject and CompleteMultipartUpload can answer 200 and fail afterwards: the error then is the body.</summary>
    private async Task ThrowIfErrorBodyAsync(HttpResponseMessage response, string bucket, string key, CancellationToken ct)
    {
        var error = S3Response.ParseError(await response.Content.ReadAsStringAsync(ct));
        if (error is not null)
        {
            throw new S3Exception(
                S3Exception.Describe(HttpStatusCode.InternalServerError, error, bucket, key, Credentials() is null),
                response.StatusCode, error.Code);
        }
    }

    /// <summary>
    /// The keys to sign with: typed into the connection, else read from its AWS profile on first use
    /// (and the profile's region taken when the connection names none), else none — anonymous access
    /// to a public bucket.
    /// </summary>
    private S3Credentials? Credentials()
    {
        lock (_credentialsGate)
        {
            if (_credentialsResolved)
            {
                return _credentials;
            }

            if (_accessKey.Length > 0)
            {
                _credentials = new S3Credentials(_accessKey, _secretKey);
            }
            else if (_profile is not null)
            {
                var profile = AwsSharedProfiles.Load(
                    _profile,
                    CredentialsFilePath ?? AwsSharedProfiles.DefaultCredentialsPath(),
                    ConfigFilePath ?? AwsSharedProfiles.DefaultConfigPath());
                _credentials = profile.Credentials;
                if (!_regionGiven && profile.Region is { Length: > 0 } region)
                {
                    _region = region;
                }
            }

            _credentialsResolved = true;
            return _credentials;
        }
    }

    private BucketLocation Location(string? bucket) =>
        bucket is not null && _locations.TryGetValue(bucket, out var known)
            ? known
            : new BucketLocation(_region, _endpoint.Host);

    // ── Paths ────────────────────────────────────────────────────────────────

    /// <summary>The bucket and key a path names; a null bucket is the list of buckets.</summary>
    private (string? Bucket, string Key) Locate(VfsPath path)
    {
        var value = path.Value.Trim('/');
        if (_bucket is not null)
        {
            return (_bucket, value);
        }

        if (value.Length == 0)
        {
            return (null, string.Empty);
        }

        var slash = value.IndexOf('/');
        return slash < 0 ? (value, string.Empty) : (value[..slash], value[(slash + 1)..]);
    }

    /// <summary>The path of <paramref name="key"/> in <paramref name="bucket"/>, the way <see cref="Locate"/> reads it back.</summary>
    private VfsPath PathOf(string bucket, string key) =>
        new(_bucket is null ? $"/{bucket}/{key}" : "/" + key);

    /// <summary>The bucket and key of an object; the root and a bucket are not objects.</summary>
    private (string Bucket, string Key) RequireObject(VfsPath path)
    {
        var (bucket, key) = Locate(path);
        if (bucket is null || key.Length == 0)
        {
            throw new NotSupportedException(bucket is null
                ? "The list of buckets is not a file."
                : $"\"{bucket}\" is a bucket. Files go inside a bucket; buckets themselves are managed in the provider's console.");
        }

        return (bucket, key);
    }

    /// <summary>
    /// A name a path can carry. Keys may hold what paths cannot — an empty segment (<c>a//b</c>),
    /// <c>.</c> and <c>..</c>, a backslash, which <see cref="VfsPath"/> turns into a separator — and
    /// those objects are left out of the listing rather than shown under a path that names another.
    /// </summary>
    private static bool IsShowable(string name) =>
        name.Length > 0 && name is not ("." or "..") && !name.Contains('/') && !name.Contains('\\');

    private static NetworkFileEntry RootEntry() => new(VfsPath.Root, "/", true, 0, DateTime.MinValue);

    // ── Small helpers ────────────────────────────────────────────────────────

    private static HttpClient CreateClient()
    {
        // No redirects followed: S3's 307 points at another host, and the signature of this one would
        // go along to it. No overall timeout either: HttpClient.Timeout limits the whole exchange, which
        // would cut off any transfer longer than it; metadata calls get their own.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = false };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static string? RegionHeader(HttpResponseMessage response) =>
        response.Headers.TryGetValues(BucketRegionHeader, out var values) && values.FirstOrDefault() is { Length: > 0 } region
            ? region
            : null;

    /// <summary>The ETag as sent, quotes included; raw, because some stores send it unquoted.</summary>
    private static string? ETagOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("ETag", out var values) && values.FirstOrDefault() is { Length: > 0 } etag
            ? etag
            : null;

    private static async Task<string> HashRangeAsync(FileStream body, long start, long size, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        body.Position = start;
        var left = size;
        while (left > 0)
        {
            var read = await body.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, left)), ct);
            if (read == 0)
            {
                throw new EndOfStreamException("The upload's temporary copy ended early.");
            }

            hash.AppendData(buffer, 0, read);
            left -= read;
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>
    /// The type a browser opening a presigned link needs to show the file rather than download it;
    /// <c>application/octet-stream</c> for anything else.
    /// </summary>
    private static MediaTypeHeaderValue ContentTypeFor(string key) => new(Path.GetExtension(key).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" or ".mjs" => "text/javascript",
        ".json" => "application/json",
        ".txt" or ".log" or ".md" or ".csv" => "text/plain",
        ".xml" => "application/xml",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".wasm" => "application/wasm",
        ".mp4" => "video/mp4",
        ".zip" => "application/zip",
        _ => "application/octet-stream",
    });

    private sealed record BucketLocation(string Region, string Host);

    private sealed record ObjectInfo(long Size, DateTime LastModified);

    private enum CallKind
    {
        /// <summary>A short exchange: bound by <see cref="MetadataTimeout"/>, answer read whole.</summary>
        Metadata,

        /// <summary>A body going up or a server-side copy: no timeout, answer read whole.</summary>
        Upload,

        /// <summary>A body coming down: no timeout, the answer streamed.</summary>
        Download,
    }

    /// <summary>
    /// One S3 request, described rather than built: a region redirect sends it again, and an
    /// <see cref="HttpRequestMessage"/> cannot be sent twice. <see cref="Body"/> makes a fresh body each time.
    /// </summary>
    private sealed record S3Call(HttpMethod Method, string? Bucket, string Key)
    {
        public IReadOnlyList<KeyValuePair<string, string>> Query { get; init; } = [];

        public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];

        public Func<HttpContent>? Body { get; init; }

        public string PayloadHash { get; init; } = S3Signer.EmptyPayloadHash;

        public CallKind Kind { get; init; } = CallKind.Metadata;
    }
}
