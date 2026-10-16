using System.Collections.Specialized;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Web;
using TCV.Network.Cloud;

namespace TCV.Network.Tests.Cloud;

/// <summary>One request as the scripted server saw it.</summary>
internal sealed record Recorded(
    HttpMethod Method, Uri Uri, string? Bearer, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string Text => Encoding.UTF8.GetString(Body);

    public string Url => Uri.AbsoluteUri;

    /// <summary>The query string, decoded.</summary>
    public NameValueCollection Query => HttpUtility.ParseQueryString(Uri.Query);

    /// <summary>A form body (token requests), decoded.</summary>
    public NameValueCollection Form => HttpUtility.ParseQueryString(Text);

    public JsonElement Json => JsonDocument.Parse(Text).RootElement.Clone();

    public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
}

/// <summary>
/// A server in a handler: token requests get a token (scriptable), every other request goes to the
/// test's responder. All requests are recorded, the token ones included.
/// </summary>
internal sealed class CloudHttp : HttpMessageHandler
{
    private static readonly HashSet<Uri> TokenEndpoints =
    [
        OAuthProvider.GoogleDrive.TokenEndpoint,
        OAuthProvider.Dropbox.TokenEndpoint,
        OAuthProvider.OneDrive.TokenEndpoint,
    ];

    private readonly Func<Recorded, CancellationToken, Task<HttpResponseMessage>> _respond;
    private readonly List<Recorded> _requests = [];
    private int _tokens;

    public CloudHttp(Func<Recorded, HttpResponseMessage> respond)
        : this((r, _) => Task.FromResult(respond(r)))
    {
    }

    public CloudHttp(Func<Recorded, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    /// <summary>Answers token requests; by default a fresh access token per request, valid for an hour.</summary>
    public Func<Recorded, HttpResponseMessage> Token { get; set; } = null!;

    public IReadOnlyList<Recorded> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public IReadOnlyList<Recorded> TokenRequests => Requests.Where(r => TokenEndpoints.Contains(r.Uri)).ToList();

    public IReadOnlyList<Recorded> Api => Requests.Where(r => !TokenEndpoints.Contains(r.Uri)).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in request.Headers)
        {
            headers[name] = string.Join(", ", values);
        }

        if (request.Content is not null)
        {
            foreach (var (name, values) in request.Content.Headers)
            {
                headers[name] = string.Join(", ", values);
            }
        }

        var recorded = new Recorded(request.Method, request.RequestUri!, request.Headers.Authorization?.Parameter, headers, body);
        lock (_requests)
        {
            _requests.Add(recorded);
        }

        HttpResponseMessage response;
        if (TokenEndpoints.Contains(recorded.Uri))
        {
            response = Token is null
                ? Kit.Json($$"""{"access_token":"access-{{Interlocked.Increment(ref _tokens)}}","expires_in":3600,"token_type":"Bearer"}""")
                : Token(recorded);
        }
        else
        {
            response = await _respond(recorded, ct);
        }

        response.RequestMessage = request;
        return response;
    }
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class FakeTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

internal static class Kit
{
    public static ConnectionInfo Connection(NetworkProtocol protocol, string? refreshToken = "refresh-1") => new()
    {
        Protocol = protocol,
        Name = protocol.ToString(),
        ClientId = "client-id",
        ClientSecret = protocol == NetworkProtocol.GoogleDrive ? "client-secret" : null,
        RefreshToken = refreshToken,
    };

    public static GoogleDriveFileSystem Google(CloudHttp http, ConnectionInfo? connection = null, TimeProvider? time = null) =>
        new(connection ?? Connection(NetworkProtocol.GoogleDrive), new HttpClient(http), time) { RetryBackoff = TimeSpan.Zero };

    public static DropboxFileSystem Dropbox(CloudHttp http, ConnectionInfo? connection = null, TimeProvider? time = null) =>
        new(connection ?? Connection(NetworkProtocol.Dropbox), new HttpClient(http), time) { RetryBackoff = TimeSpan.Zero };

    public static OneDriveFileSystem OneDrive(CloudHttp http, ConnectionInfo? connection = null, TimeProvider? time = null) =>
        new(connection ?? Connection(NetworkProtocol.OneDrive), new HttpClient(http), time)
        {
            RetryBackoff = TimeSpan.Zero,
            CopyPollInterval = TimeSpan.Zero,
        };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status) { Content = new ByteArrayContent([]) };

    public static HttpResponseMessage Bytes(byte[] data, HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(data) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return response;
    }

    public static HttpResponseMessage Unexpected(Recorded request) =>
        Json($$$"""{"error":{"code":"unexpected","message":"The test did not expect {{{request.Method}}} {{{request.Uri.AbsolutePath}}}"}}""",
            HttpStatusCode.NotImplemented);

    /// <summary>Writes <paramref name="data"/> through the file system's upload stream and commits it.</summary>
    public static async Task UploadAsync(TCV.Core.Interfaces.IVirtualFileSystem fs, string path, byte[] data, bool overwrite = false)
    {
        await using var output = await fs.OpenWriteAsync(new TCV.Core.Domain.VfsPath(path), overwrite);
        await output.WriteAsync(data);
        await ((TCV.Core.Interfaces.IDeferredUploadStream)output).CommitAsync(null, CancellationToken.None);
    }

    public static async Task<byte[]> ReadAllAsync(Stream stream)
    {
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        }
    }
}
