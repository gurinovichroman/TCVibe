using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Cloud;

/// <summary>How a request is sent: under the metadata timeout, or as a transfer that takes as long as it takes.</summary>
internal enum CloudRequestKind
{
    /// <summary>A listing, stat, create, move or delete: bound by <see cref="CloudFileSystem.MetadataTimeout"/>, body buffered.</summary>
    Metadata,

    /// <summary>A download: no timeout, the body is streamed to the caller.</summary>
    Download,

    /// <summary>An upload or one chunk of it: no timeout, the (short) answer buffered.</summary>
    Upload,
}

/// <summary>
/// What Google Drive, Dropbox and OneDrive share: the browser sign-in, a bearer token on every request,
/// the metadata timeout, one refresh after a 401, a bounded wait on 429 / <c>Retry-After</c>, and error
/// bodies turned into messages a user can read. The providers only say how paths become requests.
/// </summary>
/// <remarks>
/// Asynchronous all the way down over plain <see cref="HttpClient"/>, like the WebDAV file system,
/// with no SDK. Nothing here opens a browser except <see cref="SignInAsync"/>: a request without a
/// usable token fails with <see cref="SignInRequiredException"/>, so the transfer queue, which makes
/// file systems of its own, never pops one up.
/// </remarks>
public abstract class CloudFileSystem : IVirtualFileSystem, IInteractiveSignIn, IRangeReader
{
    private readonly bool _ownsClient;
    private readonly TimeSpan _metadataTimeout = TimeSpan.FromSeconds(30);
    private string? _accountEmail;
    private bool _disposed;

    private protected CloudFileSystem(ConnectionInfo connection, OAuthProvider provider, HttpClient? client, TimeProvider? time)
    {
        ArgumentNullException.ThrowIfNull(connection);
        Connection = connection;
        _ownsClient = client is null;
        Http = client ?? CreateHttpClient();
        Time = time ?? TimeProvider.System;
        Auth = new OAuthClient(provider, connection, Http, Time);
    }

    public abstract string Scheme { get; }

    /// <summary>The account's e-mail once known (after a sign-in, or from the connection), else the provider's name.</summary>
    public string DisplayName =>
        _accountEmail ?? (string.IsNullOrWhiteSpace(Connection.Username) ? ProviderName : Connection.Username.Trim());

    /// <summary>"Google Drive", "Dropbox", "OneDrive": the provider in error messages.</summary>
    public string ProviderName => Auth.Provider.Name;

    /// <summary>
    /// How long a listing, stat, create, move or delete may take before it is called failed. Transfers
    /// are not bound by it: a large file legitimately takes as long as it takes.
    /// </summary>
    public TimeSpan MetadataTimeout
    {
        get => _metadataTimeout;
        init
        {
            _metadataTimeout = value;
            Auth.RequestTimeout = value;
        }
    }

    /// <summary>How long the browser sign-in may take.</summary>
    public TimeSpan SignInTimeout
    {
        get => Auth.SignInTimeout;
        init => Auth.SignInTimeout = value;
    }

    /// <summary>Opens the provider's sign-in page; the default is the user's default browser.</summary>
    public Func<Uri, CancellationToken, Task> OpenBrowser
    {
        get => Auth.OpenBrowser;
        init => Auth.OpenBrowser = value;
    }

    /// <summary>How many times a throttled request (429, 503) is sent again before its error is reported.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>
    /// The longest wait a <c>Retry-After</c> may ask for. A provider asking for longer gets its error
    /// reported at once ("try again in …") instead of a transfer that sits silent for minutes.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The first wait when a throttling answer names none; it doubles on each retry. A test seam.</summary>
    internal TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(1);

    public bool NeedsSignIn => Auth.NeedsSignIn;

    public event EventHandler<VfsChangedEventArgs>? Changed;

    private protected ConnectionInfo Connection { get; }

    private protected HttpClient Http { get; }

    private protected TimeProvider Time { get; }

    internal OAuthClient Auth { get; }

    /// <summary>
    /// Signs in through the browser, then learns the account's e-mail: it becomes the display name, and
    /// the connection's user name when that is empty.
    /// </summary>
    public async Task SignInAsync(CancellationToken ct)
    {
        await Auth.SignInAsync(ct);

        string? email;
        try
        {
            email = await FetchAccountEmailAsync(ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // The sign-in has succeeded and its token is stored; the name is only for show.
            return;
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            return;
        }

        _accountEmail = email.Trim();
        if (string.IsNullOrWhiteSpace(Connection.Username))
        {
            Connection.Username = _accountEmail;
            Auth.RaiseSecretsChanged();
        }
    }

    public abstract Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default);

    public abstract Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default);

    public abstract Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default);

    public abstract Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
        VfsPath path, long offset, string? validator, CancellationToken ct);

    /// <summary>
    /// A write stream that uploads when committed or disposed (see <see cref="CloudUploadStream"/>).
    /// With <paramref name="overwrite"/> false an existing file is never replaced: the check is made
    /// here, so a copy fails before collecting gigabytes, and again by the upload itself.
    /// </summary>
    public abstract Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default);

    /// <summary>A server-side copy of one file; an existing destination file is replaced.</summary>
    public abstract Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct);

    /// <summary>Moves a file or folder; an existing destination file is replaced (it goes to the provider's bin).</summary>
    public abstract Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct);

    /// <summary>
    /// Deletes into the provider's bin (Drive's trash, Dropbox's deleted files, OneDrive's recycle bin),
    /// from where the user can restore it. A folder goes whole; without <paramref name="recursive"/> only an empty one.
    /// </summary>
    public abstract Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct);

    /// <summary>Creates a folder; one that already exists is left as it is, as on the local disk.</summary>
    public abstract Task CreateDirectoryAsync(VfsPath path, CancellationToken ct);

    /// <summary>Renames in place; never replaces an existing entry.</summary>
    public abstract Task RenameAsync(VfsPath path, string newName, CancellationToken ct);

    public bool SupportsOperation(VfsOperation operation) => operation switch
    {
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
        Auth.Dispose();
        if (_ownsClient)
        {
            Http.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    // ── For the providers ────────────────────────────────────────────────────

    /// <summary>The provider's error code and message from an error body; nulls when it has none.</summary>
    private protected abstract (string? Code, string? Message) ParseError(string body);

    /// <summary>The signed-in account's e-mail, asked for right after a sign-in.</summary>
    private protected abstract Task<string?> FetchAccountEmailAsync(CancellationToken ct);

    /// <summary>
    /// True for an answer that means "slow down": 429, and 503 with or without <c>Retry-After</c>.
    /// Google also says it with 403 and a rate-limit reason.
    /// </summary>
    private protected virtual ValueTask<bool> IsThrottledAsync(HttpResponseMessage response, CancellationToken ct) =>
        ValueTask.FromResult(response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable);

    /// <summary>
    /// Sends a request built by <paramref name="create"/> — a factory, because a request cannot be sent
    /// twice and a 401 or a throttling answer sends it again. Adds the bearer token unless the address
    /// is pre-authenticated (<paramref name="authorize"/> false), which must never see the token.
    /// The caller checks the status: a "not found" is an answer for some callers and an error for others.
    /// </summary>
    private protected async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> create, CloudRequestKind kind, CancellationToken ct, bool authorize = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var renewed = false;
        var attempt = 0;
        while (true)
        {
            using var request = create();
            string? token = null;
            if (authorize)
            {
                token = await Auth.GetAccessTokenAsync(ct);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            var response = await SendOnceAsync(request, kind, ct);
            if (authorize && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                if (renewed)
                {
                    // Even a token refreshed a moment ago is refused: the grant itself is gone.
                    Auth.MarkSignInRequired();
                    throw new SignInRequiredException(ProviderName);
                }

                // Expired early or revoked: one refresh, one more try.
                Auth.Invalidate(token!);
                renewed = true;
                continue;
            }

            if (attempt < MaxRetries && await RetryDelayAsync(response, attempt, ct) is { } delay)
            {
                response.Dispose();
                attempt++;
                await Task.Delay(delay, Time, ct);
                continue;
            }

            return response;
        }
    }

    /// <summary>Throws the provider's error unless <paramref name="response"/> succeeded.</summary>
    private protected async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ErrorAsync(response, ct);
        }
    }

    /// <summary>Turns an error answer into an exception whose message a user can read.</summary>
    private protected async Task<Exception> ErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await ReadBodyAsync(response, ct);
        var (code, message) = ParseError(body);
        message = string.IsNullOrWhiteSpace(message)
            ? CloudJson.Excerpt(body) ?? response.ReasonPhrase ?? "no details"
            : message.Trim();

        if (response.StatusCode == HttpStatusCode.TooManyRequests || await IsThrottledAsync(response, ct))
        {
            var wait = RetryAfter(response);
            return new CloudDriveException(
                $"{ProviderName} is limiting requests right now. Try again " +
                (wait is { } w && w > TimeSpan.Zero ? $"in {Math.Ceiling(w.TotalSeconds):0} seconds." : "in a minute."),
                response.StatusCode, code);
        }

        var status = (int)response.StatusCode;
        return new CloudDriveException(
            $"{ProviderName}: {message.TrimEnd('.')}. (HTTP {status}{(code is null ? string.Empty : ", " + code)})",
            response.StatusCode, code);
    }

    /// <summary>The body of an answer as text — buffered first, so it can be read again.</summary>
    private protected static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(ct);
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Parses a successful answer's JSON, throwing the provider's error for any other.</summary>
    private protected async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(body.Length == 0 ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw new IOException($"{ProviderName} sent an answer that is not JSON.", ex);
        }
    }

    private protected FileNotFoundException NotFound(string path) =>
        new($"\"{path}\" was not found on {ProviderName}.", path);

    private protected DirectoryNotFoundException FolderNotFound(string path) =>
        new($"The folder \"{path}\" was not found on {ProviderName}.");

    private protected static IOException AlreadyExists(string name) => new($"\"{name}\" already exists.");

    private protected void RaiseChanged(string path, WatcherChangeTypes change) =>
        Changed?.Invoke(this, new VfsChangedEventArgs(new VfsPath(path), change));

    private protected static NetworkFileEntry Entry(string path, bool isDirectory, long size, DateTime modified) =>
        new(new VfsPath(path), CloudPath.Name(path), isDirectory, isDirectory ? 0 : size, modified);

    private protected static NetworkFileEntry RootEntry() => new(VfsPath.Root, string.Empty, true, 0, DateTime.MinValue);

    /// <summary>A server-side copy is one step: its progress jumps from nothing to done.</summary>
    private protected static void ReportCopied(IProgress<FileProgress>? progress, long size, string name, Stopwatch started) =>
        progress?.Report(new FileProgress(size, size, name, started.Elapsed));

    private protected static Task<Stream> UploadStream(
        Func<FileStream, IProgress<long>?, CancellationToken, Task> send, CancellationToken ct) =>
        Task.FromResult<Stream>(new CloudUploadStream(send, ct));

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private static HttpClient CreateHttpClient()
    {
        // No automatic redirects: OneDrive sends a download to a pre-authenticated address that must
        // not receive the bearer token, and a Drive upload session says "go on" with a 308.
        var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(15), AllowAutoRedirect = false };

        // No overall timeout on the client: it is a wall-clock limit on the whole exchange, which would
        // cut off any transfer longer than it. Metadata calls get their own.
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task<HttpResponseMessage> SendOnceAsync(HttpRequestMessage request, CloudRequestKind kind, CancellationToken ct)
    {
        if (kind != CloudRequestKind.Metadata)
        {
            var completion = kind == CloudRequestKind.Download
                ? HttpCompletionOption.ResponseHeadersRead
                : HttpCompletionOption.ResponseContentRead;
            return await Http.SendAsync(request, completion, ct);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MetadataTimeout);
        try
        {
            return await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{ProviderName} did not answer within {MetadataTimeout.TotalSeconds:0} seconds.");
        }
    }

    private async Task<TimeSpan?> RetryDelayAsync(HttpResponseMessage response, int attempt, CancellationToken ct)
    {
        if (!await IsThrottledAsync(response, ct))
        {
            return null;
        }

        var delay = RetryAfter(response) ?? RetryBackoff * Math.Pow(2, attempt);
        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay <= MaxRetryDelay ? delay : null;
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - Time.GetUtcNow(),
        _ => null,
    };
}
