using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network;

/// <summary>
/// WebDAV as a virtual file system (6.23): <c>dav://host/path</c> and <c>davs://host/path</c> for
/// TLS. Covers what a developer actually does with one — browse, read, write, create, delete,
/// rename — over plain <see cref="HttpClient"/>, with no additional dependency.
/// </summary>
/// <remarks>
/// Asynchronous all the way down, unlike the FTP and SFTP file systems whose libraries are
/// synchronous and need <c>Task.Run</c> (TRAP-06). Parsing lives in <see cref="WebDavResponse"/>,
/// so the protocol's awkward parts are testable without a server.
/// </remarks>
public sealed class WebDavFileSystem : IVirtualFileSystem, IRangeReader
{
    private static readonly HttpMethod PropFind = new("PROPFIND");
    private static readonly HttpMethod MkCol = new("MKCOL");
    private static readonly HttpMethod Move = new("MOVE");

    /// <summary>Body requesting just the properties a listing needs.</summary>
    private const string PropFindBody =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <D:propfind xmlns:D="DAV:">
          <D:prop>
            <D:resourcetype/>
            <D:getcontentlength/>
            <D:getlastmodified/>
          </D:prop>
        </D:propfind>
        """;

    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>
    /// How long a listing, stat, mkdir, move or delete may take before it is called failed.
    /// Transfers are not bound by it: a large file legitimately takes as long as it takes, and is
    /// stopped by the caller's token or by the connection dropping.
    /// </summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Connects to <paramref name="connection"/>, which carries host, port and credentials.</summary>
    public WebDavFileSystem(ConnectionInfo connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var secure = connection.Protocol == NetworkProtocol.WebDavs;
        var port = connection.Port > 0 ? connection.Port : (secure ? 443 : 80);
        _baseUri = new Uri($"{(secure ? "https" : "http")}://{connection.Host}:{port}");

        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15) };
        if (!string.IsNullOrEmpty(connection.Username))
        {
            handler.Credentials = new NetworkCredential(connection.Username, connection.Password);
            handler.PreAuthenticate = true;
            SendsCredentialsInClear = !secure;
        }

        // No overall timeout on the client: HttpClient.Timeout is a wall-clock limit on the whole
        // exchange, which would cut off any transfer longer than it. Metadata calls get their own.
        _client = new HttpClient(handler) { BaseAddress = _baseUri, Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
        DisplayName = $"{connection.Host}{(connection.Port > 0 ? ":" + connection.Port : string.Empty)}";
        Scheme = secure ? "davs" : "dav";
    }

    /// <summary>Test seam: runs against a caller-supplied client without opening a socket.</summary>
    internal WebDavFileSystem(HttpClient client, string scheme = "dav")
    {
        _client = client;
        _baseUri = client.BaseAddress ?? new Uri("http://localhost");
        _ownsClient = false;
        Scheme = scheme;
        DisplayName = _baseUri.Host;
    }

    public string Scheme { get; }

    public string DisplayName { get; }

    /// <summary>True for a login over plain http: the password travels readable (HTTP Basic).</summary>
    public bool SendsCredentialsInClear { get; }

    public event EventHandler<VfsChangedEventArgs>? Changed;

    public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var target = WebDavResponse.NormalizePath(path.Value);
        using var request = new HttpRequestMessage(PropFind, EncodeCollection(target))
        {
            Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
        };
        // Depth 1 is "this collection and its immediate children" — exactly one directory level.
        request.Headers.Add("Depth", "1");

        using var response = await SendMetadataAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(ct);
        return WebDavResponse.ParseMultiStatus(xml, target)
            .Select(r => (IFileEntry)new NetworkFileEntry(
                new VfsPath(r.Href), r.Name, r.IsDirectory, r.Size, r.LastModified))
            .ToList();
    }

    public async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var target = WebDavResponse.NormalizePath(path.Value);
        using var request = new HttpRequestMessage(PropFind, Encode(target))
        {
            Content = new StringContent(PropFindBody, Encoding.UTF8, "application/xml"),
        };
        request.Headers.Add("Depth", "0");

        using var response = await SendMetadataAsync(request, ct);

        // Only "not found" means "does not exist". A login failure or a server error is not an
        // answer to "is it there?" — reporting it as absent made a copy overwrite blindly.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var xml = await response.Content.ReadAsStringAsync(ct);
        // Depth 0 describes the resource itself, so the self-entry filter must not apply here.
        var resource = WebDavResponse.ParseMultiStatus(xml, basePath: " ").FirstOrDefault();
        return resource is null
            ? null
            : new NetworkFileEntry(new VfsPath(resource.Href), resource.Name,
                resource.IsDirectory, resource.Size, resource.LastModified);
    }

    public async Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var response = await GetAsync(path, ct);
        return await response.Content.ReadAsStreamAsync(ct);
    }

    /// <summary>
    /// Opens a download at <paramref name="offset"/> using a <c>Range</c> request. Returns the offset
    /// the stream really starts at — 0 when the server ignored the header and sent the whole file —
    /// and the full size when known (0 otherwise).
    /// </summary>
    public async Task<(Stream Stream, long Offset, long Total)> OpenReadFromAsync(
        VfsPath path, long offset, CancellationToken ct)
    {
        var (stream, start, total, _) = await OpenReadFromAsync(path, offset, ifRange: null, ct);
        return (stream, start, total);
    }

    /// <summary>
    /// The same, conditional on the file still being the one described by <paramref name="ifRange"/>
    /// (an ETag or an HTTP date from an earlier response): a server whose file changed answers with
    /// the whole new file, and the returned offset is 0. Also returns the validator of this response
    /// — its ETag, or else its Last-Modified — or null when the server gave neither.
    /// </summary>
    public async Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? ifRange, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Encode(WebDavResponse.NormalizePath(path.Value)));
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
            if (!string.IsNullOrEmpty(ifRange))
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }
        }

        var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            var length = response.Content.Headers.ContentLength ?? 0;
            var resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            var validator = response.Headers.ETag is { IsWeak: false } etag
                ? etag.ToString()
                : response.Content.Headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            var stream = await response.Content.ReadAsStreamAsync(ct);
            return (stream, resumed ? offset : 0, length > 0 ? (resumed ? offset + length : length) : 0, validator);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> GetAsync(VfsPath path, CancellationToken ct)
    {
        var response = await _client.GetAsync(
            Encode(WebDavResponse.NormalizePath(path.Value)), HttpCompletionOption.ResponseHeadersRead, ct);
        try
        {
            response.EnsureSuccessStatusCode();
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>
    /// A write stream that PUTs its contents when it is committed or disposed. WebDAV has no streaming
    /// append and a PUT needs its length, so the body is collected in a temporary file first (not in
    /// memory: a multi-gigabyte upload must not become a multi-gigabyte array). A body that was
    /// abandoned, or whose copy was cancelled, is never sent. The stream is an
    /// <see cref="IDeferredUploadStream"/>: writing to it only fills the temporary file, and a caller
    /// that shows progress commits it to follow the upload itself.
    /// </summary>
    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        var target = Encode(WebDavResponse.NormalizePath(path.Value));
        return Task.FromResult<Stream>(new WebDavUploadStream(_client, target, overwrite, ct));
    }

    public async Task CopyAsync(
        VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        // Server-side COPY would be one round trip, but not every server implements it against
        // an arbitrary destination; streaming through is slower and always works.
        using var response = await GetAsync(source, ct);
        var total = response.Content.Headers.ContentLength ?? 0;
        await using var input = await response.Content.ReadAsStreamAsync(ct);
        var output = new WebDavUploadStream(_client, Encode(WebDavResponse.NormalizePath(destination.Value)), true, ct);

        try
        {
            // Every byte travels twice — down into the temporary file, then up in the PUT — and each
            // pass counts for half. Counting only the first one put the bar at 100 % before the upload
            // had even started.
            var started = System.Diagnostics.Stopwatch.StartNew();
            var buffer = new byte[81920];
            long copied = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                copied += read;
                progress?.Report(new FileProgress(copied / 2, total, source.FileName, started.Elapsed));
            }

            var collected = copied;
            await output.CommitAsync(
                progress is null
                    ? null
                    : new InlineProgress<long>(sent => progress.Report(
                        new FileProgress((collected + sent) / 2, total, source.FileName, started.Elapsed))),
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

        Changed?.Invoke(this, new VfsChangedEventArgs(destination, WatcherChangeTypes.Created));
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        await SendMoveAsync(source, destination, overwrite: true, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(source, WatcherChangeTypes.Deleted));
    }

    private async Task SendMoveAsync(VfsPath source, VfsPath destination, bool overwrite, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(Move, Encode(WebDavResponse.NormalizePath(source.Value)));

        // AbsoluteUri, not ToString(): ToString() un-escapes, which would put raw spaces and
        // non-ASCII characters into the header that the server then decodes a second time.
        request.Headers.Add("Destination",
            new Uri(_baseUri, Encode(WebDavResponse.NormalizePath(destination.Value))).AbsoluteUri);
        request.Headers.Add("Overwrite", overwrite ? "T" : "F");

        using var response = await SendMetadataAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            throw new IOException($"\"{WebDavResponse.LastSegment(destination.Value)}\" already exists.");
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, Encode(WebDavResponse.NormalizePath(path.Value)));
        using var response = await SendMetadataAsync(request, ct);
        response.EnsureSuccessStatusCode();
        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Deleted));
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            MkCol, EncodeCollection(WebDavResponse.NormalizePath(path.Value)));
        using var response = await SendMetadataAsync(request, ct);
        response.EnsureSuccessStatusCode();
        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Created));
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        var normalized = WebDavResponse.NormalizePath(path.Value);
        var slash = normalized.LastIndexOf('/');
        var parent = slash <= 0 ? string.Empty : normalized[..slash];

        // A rename never replaces an existing entry: Overwrite: F makes the server refuse instead.
        await SendMoveAsync(path, new VfsPath(parent + "/" + newName), overwrite: false, ct);
        Changed?.Invoke(this, new VfsChangedEventArgs(path, WatcherChangeTypes.Renamed));
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

    /// <summary>Sends a short request under <see cref="MetadataTimeout"/>.</summary>
    private async Task<HttpResponseMessage> SendMetadataAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The WebDAV server did not answer within {MetadataTimeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary>Percent-encodes each path segment, leaving the separators alone.</summary>
    private static string Encode(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// The same, with the trailing slash a collection request wants. Appending it blindly would
    /// turn the root into <c>"//"</c>, which a relative URI reads as the start of an authority —
    /// <c>new Uri(baseAddress, "//")</c> throws, so listing a connection mounted at <c>/</c> would
    /// fail before a single request went out.
    /// </summary>
    private static string EncodeCollection(string path)
    {
        var encoded = Encode(path);
        return encoded.EndsWith('/') ? encoded : encoded + "/";
    }

    /// <summary>
    /// Collects a body in a temporary file and PUTs it when committed, or on dispose — unless it was
    /// abandoned. Internal (not private) only so tests can see that the temporary file goes away.
    /// </summary>
    internal sealed class WebDavUploadStream : Stream, IDeferredUploadStream
    {
        private readonly HttpClient _client;
        private readonly string _target;
        private readonly bool _overwrite;
        private readonly CancellationToken _ct;
        private readonly FileStream _buffer;
        private bool _finished;
        private bool _aborted;

        public WebDavUploadStream(HttpClient client, string target, bool overwrite, CancellationToken ct)
        {
            _client = client;
            _target = target;
            _overwrite = overwrite;
            _ct = ct;
            _buffer = new FileStream(
                Path.Combine(Path.GetTempPath(), $"tcv-dav-{Guid.NewGuid():N}.tmp"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
                FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        }

        public void Abort() => _aborted = true;

        /// <summary>Where the body is collected; the file is gone once the stream is committed or disposed.</summary>
        internal string BufferPath => _buffer.Name;

        public async Task CommitAsync(IProgress<long>? progress, CancellationToken ct)
        {
            if (_finished)
            {
                throw new InvalidOperationException("The upload was already sent or discarded.");
            }

            _finished = true;
            try
            {
                // Asked for explicitly, so unlike dispose this never reports success without sending.
                if (_aborted)
                {
                    throw new InvalidOperationException("An aborted upload cannot be committed.");
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, ct);
                linked.Token.ThrowIfCancellationRequested();
                await PutAsync(progress, linked.Token);
            }
            finally
            {
                await _buffer.DisposeAsync();
            }
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _buffer.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _buffer.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _buffer.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                // Off the caller's synchronisation context: blocking on it from a UI thread would deadlock.
                Task.Run(FinishAsync).GetAwaiter().GetResult();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await FinishAsync();
            await base.DisposeAsync();
        }

        private async Task FinishAsync()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            try
            {
                if (_aborted || _ct.IsCancellationRequested)
                {
                    return;
                }

                await PutAsync(progress: null, _ct);
            }
            finally
            {
                await _buffer.DisposeAsync();
            }
        }

        private async Task PutAsync(IProgress<long>? progress, CancellationToken ct)
        {
            _buffer.Position = 0;
            using var content = new ProgressStreamContent(_buffer, progress);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = _buffer.Length;

            using var request = new HttpRequestMessage(HttpMethod.Put, _target) { Content = content };
            if (!_overwrite)
            {
                // "Only if nothing is there": the server answers 412 instead of replacing.
                request.Headers.TryAddWithoutValidation("If-None-Match", "*");
            }

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (response.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                throw new IOException("The file already exists on the server.");
            }

            response.EnsureSuccessStatusCode();
        }
    }
}
