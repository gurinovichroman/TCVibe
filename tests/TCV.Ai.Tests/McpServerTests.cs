using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using TCV.Ai.Mcp;

namespace TCV.Ai.Tests;

/// <summary>
/// The transport, over a real loopback socket. Everything here is about the cost of being reachable
/// (ADR-0018): the bearer token, the <c>Origin</c> check that blocks DNS-rebinding, and the endpoint
/// file that is the only way a client can find the port. Those are the parts where a mistake is a
/// security hole rather than a bug, and none of them are exercised by the protocol unit tests.
/// </summary>
public sealed class McpServerTests : IAsyncDisposable
{
    private readonly FakeAgentBridge _bridge = new();
    private readonly McpServer _server;
    private readonly McpEndpointInfo _endpoint;
    private readonly string _endpointFile;
    private readonly HttpClient _http = new();

    public McpServerTests()
    {
        _endpointFile = Path.Combine(
            Path.GetTempPath(), $"tcv-mcp-{Guid.NewGuid():n}.json");

        _server = new McpServer(new McpProtocol(_bridge), () => _bridge.CurrentScope().Roots);
        _endpoint = _server.Start(preferredPort: 0, endpointFilePath: _endpointFile)!;
    }

    public async ValueTask DisposeAsync()
    {
        await _server.StopAsync();
        _http.Dispose();

        try { if (File.Exists(_endpointFile)) File.Delete(_endpointFile); }
        catch { /* best effort */ }
    }

    private HttpRequestMessage Post(string json, bool withToken = true, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.Url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

        if (withToken)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.Token);
        if (origin is not null)
            request.Headers.Add("Origin", origin);

        return request;
    }

    private static string Rpc(string method, int? id = 1, string? parameters = null)
    {
        var body = new StringBuilder("{\"jsonrpc\":\"2.0\"");
        if (id is not null) body.Append($",\"id\":{id}");
        body.Append($",\"method\":\"{method}\"");
        if (parameters is not null) body.Append($",\"params\":{parameters}");
        return body.Append('}').ToString();
    }

    // ── It actually serves ────────────────────────────────────────────────────

    [Fact]
    public void Start_BindsLoopbackAndPublishesAnEndpoint()
    {
        _server.IsRunning.Should().BeTrue();
        _endpoint.Url.Should().StartWith("http://127.0.0.1:");
        _endpoint.Token.Should().NotBeNullOrWhiteSpace();
        _endpoint.ProcessId.Should().Be(Environment.ProcessId);
    }

    [Fact]
    public async Task Initialize_RoundTripsOverHttp()
    {
        var response = await _http.SendAsync(Post(Rpc("initialize")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        payload!["result"]!["serverInfo"]!["name"]!.GetValue<string>()
            .Should().Be("tcvibe");
    }

    [Fact]
    public async Task ToolsCall_ReachesTheBridge()
    {
        var body = Rpc("tools/call", parameters: $"{{\"name\":\"{McpToolCatalog.List}\"}}");

        var response = await _http.SendAsync(Post(body));
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        payload!["result"]!["isError"]!.GetValue<bool>().Should().BeFalse();
        _bridge.LastListedPath.Should().Be(@"D:\repo");
    }

    [Fact]
    public async Task Notification_IsAcceptedWithNoBody()
    {
        // JSON-RPC forbids replying to a notification; 202 with an empty body is how that is said
        // over HTTP.
        var response = await _http.SendAsync(
            Post(Rpc("notifications/initialized", id: null)));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Batch_AnswersEveryRequestThatHadAnId()
    {
        var body = $"[{Rpc("ping", id: 1)},{Rpc("ping", id: 2)},{Rpc("notifications/initialized", id: null)}]";

        var response = await _http.SendAsync(Post(body));
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        payload!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task Batch_Empty_IsAnInvalidRequest()
    {
        var response = await _http.SendAsync(Post("[]"));
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync());

        payload!["error"]!["code"]!.GetValue<int>().Should().Be(-32600);
    }

    [Fact]
    public async Task Batch_OneMalformedItem_FailsOnlyThatItem()
    {
        var body = $"[{Rpc("ping", id: 1)},{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":42}},{Rpc("ping", id: 3)}]";

        var response = await _http.SendAsync(Post(body));
        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsArray();

        payload.Should().HaveCount(3);
        payload[0]!["result"].Should().NotBeNull();
        payload[1]!["error"]!["code"]!.GetValue<int>().Should().Be(-32600);
        payload[2]!["result"].Should().NotBeNull();
    }

    [Fact]
    public async Task Initialize_HandsOutADifferentSessionToEachClient()
    {
        var one = await _http.SendAsync(Post(Rpc("initialize")));
        var two = await _http.SendAsync(Post(Rpc("initialize")));

        one.Headers.GetValues("Mcp-Session-Id").Single()
            .Should().NotBe(two.Headers.GetValues("Mcp-Session-Id").Single());
    }

    [Fact]
    public async Task OrdinaryReply_CarriesNoSessionHeader()
    {
        var response = await _http.SendAsync(Post(Rpc("ping")));

        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
    }

    [Fact]
    public async Task Post_LargerThanTheCap_IsRefused()
    {
        var response = await _http.SendAsync(Post(new string(' ', 2 * 1024 * 1024) + Rpc("ping")));

        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task ReadsAreServedWhileAnotherRequestIsStillInFlight()
    {
        // Two requests at once must both be answered: the server no longer handles one at a time.
        var results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => _http.SendAsync(Post(Rpc("ping", id: i + 1)))));

        results.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    // ── The cost of being reachable ───────────────────────────────────────────

    [Fact]
    public async Task Request_WithoutTheToken_IsUnauthorized()
    {
        var response = await _http.SendAsync(Post(Rpc("initialize"), withToken: false));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Request_WithAWrongToken_IsUnauthorized()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _endpoint.Url)
        {
            Content = new StringContent(Rpc("initialize"), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('0', 48));

        var response = await _http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SessionHeader_IsNotDerivedFromTheBearerToken()
    {
        // It travels in a response header; a header that is a prefix of the secret hands part of the
        // secret to whoever can make the server answer.
        var response = await _http.SendAsync(Post(Rpc("initialize")));

        var session = response.Headers.GetValues("Mcp-Session-Id").Single();

        session.Should().NotBeNullOrWhiteSpace();
        _endpoint.Token.Should().NotStartWith(session);
        _endpoint.Token.Should().NotContain(session);
    }

    [Fact]
    public async Task SessionHeader_IsWithheldFromAnUnauthenticatedCaller()
    {
        var response = await _http.SendAsync(Post(Rpc("initialize"), withToken: false));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Contains("Mcp-Session-Id").Should().BeFalse();
    }

    [Fact]
    public async Task Request_FromARemoteOrigin_IsRefused()
    {
        // DNS-rebinding: a page in the user's browser posting to 127.0.0.1 would otherwise borrow the
        // agent's access. Browsers always send Origin, so this is the check that stops it.
        var response = await _http.SendAsync(Post(Rpc("initialize"), origin: "https://evil.example"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:8080")]
    public async Task Request_FromALocalOrigin_IsAllowed(string origin)
    {
        var response = await _http.SendAsync(Post(Rpc("initialize"), origin: origin));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Get_IsRefusedBecauseTheServerNeverPushes()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, _endpoint.Url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _endpoint.Token);

        var response = await _http.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task MalformedJson_IsAParseError()
    {
        var response = await _http.SendAsync(Post("{ not json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var payload = JsonNode.Parse(await response.Content.ReadAsStringAsync());
        payload!["error"]!["code"]!.GetValue<int>().Should().Be(-32700);
    }

    // ── Discovery ─────────────────────────────────────────────────────────────

    [Fact]
    public void Start_WritesTheDiscoveryFileWithTheLiveEndpoint()
    {
        var onDisk = McpEndpointFile.Read(_endpointFile);

        onDisk.Should().NotBeNull();
        onDisk!.Url.Should().Be(_endpoint.Url);
        onDisk.Token.Should().Be(_endpoint.Token);
        // The file doubles as the honest disclosure of what is exposed (§ 3.6.4).
        onDisk.ApprovedRoots.Should().Equal(_bridge.CurrentScope().Roots);
    }

    [Fact]
    public void ClientCommandLine_IsReadyToPaste()
    {
        var line = _endpoint.ClientCommandLine();

        line.Should().Contain("--transport http");
        line.Should().Contain(_endpoint.Url);
        line.Should().Contain(_endpoint.Token);
    }

    [Fact]
    public async Task Stop_ReleasesThePortAndRemovesTheDiscoveryFile()
    {
        await _server.StopAsync();

        _server.IsRunning.Should().BeFalse();
        // A token left lying about after the process would keep granting access.
        File.Exists(_endpointFile).Should().BeFalse();

        var act = async () => await _http.SendAsync(Post(Rpc("initialize")));
        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
