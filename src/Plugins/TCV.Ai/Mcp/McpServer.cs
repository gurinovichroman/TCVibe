using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TCV.Ai.Mcp;

/// <summary>
/// MCP over Streamable HTTP, bound to loopback only.
/// <para>
/// The transport choice is forced by where the server lives (ADR-0018): the useful things it exposes —
/// the live panel selection, the mounted archive, the operation queue — exist only inside the running
/// window, and a stdio server is a process the <em>client</em> spawns. HTTP on <c>127.0.0.1</c> lets the
/// already-running window be the server, with the port and token published through
/// <see cref="McpEndpointFile"/>.
/// </para>
/// <para>
/// Being reachable over a socket is a real cost, so three things are non-negotiable and all
/// implemented here: the listener is bound to the loopback address (never <c>+</c> or <c>*</c>),
/// every request must carry the per-run bearer token, and <c>Origin</c> must be absent or local — the
/// last one blocks DNS-rebinding, where a page in the user's browser posts to <c>127.0.0.1</c> and
/// borrows the agent's access.
/// </para>
/// </summary>
public sealed class McpServer : IAsyncDisposable
{
    private readonly McpProtocol _protocol;
    private readonly ILogger<McpServer>? _logger;
    private readonly Func<IReadOnlyList<string>> _approvedRoots;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _token = string.Empty;
    private string? _endpointFilePath;

    /// <summary>Requests being served right now, so a stop can wait for them (bounded).</summary>
    private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

    /// <summary>
    /// Ceiling on requests served at once. Reads are cheap and a pending approval prompt must not
    /// stall them, but an unbounded fan-out from a misbehaving client is not something to allow.
    /// </summary>
    private const int MaxConcurrentRequests = 16;

    /// <summary>Largest request body accepted. Tool arguments are a few paths; anything bigger is abuse.</summary>
    private const int MaxBodyBytes = 1_048_576;

    private readonly SemaphoreSlim _slots = new(MaxConcurrentRequests, MaxConcurrentRequests);

    public McpServer(
        McpProtocol protocol,
        Func<IReadOnlyList<string>> approvedRoots,
        ILogger<McpServer>? logger = null)
    {
        _protocol = protocol;
        _approvedRoots = approvedRoots;
        _logger = logger;
    }

    public bool IsRunning => _listener is { IsListening: true };

    /// <summary>The live endpoint, or null while stopped. Shown in the "what is exposed" dialog.</summary>
    public McpEndpointInfo? Endpoint { get; private set; }

    /// <summary>
    /// Binds and starts serving. <paramref name="preferredPort"/> of 0 picks a free ephemeral port,
    /// which is the sane default: a fixed port collides with other software and with a second
    /// portable copy of this app.
    /// </summary>
    /// <returns>The published endpoint, or null when the port could not be bound.</returns>
    public McpEndpointInfo? Start(int preferredPort = 0, string? endpointFilePath = null)
    {
        if (IsRunning) return Endpoint;

        var port = preferredPort > 0 ? preferredPort : FindFreePort();
        var prefix = $"http://127.0.0.1:{port}/mcp/";

        try
        {
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            _listener = listener;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not start the MCP server on {Prefix}", prefix);
            return null;
        }

        // A fresh token per run: the endpoint file is readable by anything running as this user, so a
        // token that outlived the process would keep granting access after the window closed.
        _token = GenerateToken();

        // Session ids (McpProtocol) are random and independent of the token: they travel in a response
        // header, and a header that is a prefix of the bearer token would hand part of the secret to
        // anyone who can make the server answer at all.
        _endpointFilePath = endpointFilePath ?? McpEndpointFile.DefaultPath;

        Endpoint = new McpEndpointInfo(
            $"http://127.0.0.1:{port}/mcp",
            _token,
            Environment.ProcessId,
            DateTime.UtcNow,
            _approvedRoots());

        McpEndpointFile.Write(Endpoint, _endpointFilePath);

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));

        _logger?.LogInformation(
            "MCP server listening on {Url} ({Roots} approved root(s))",
            Endpoint.Url, Endpoint.ApprovedRoots.Count);

        return Endpoint;
    }

    public async Task StopAsync()
    {
        if (_cts is null && _listener is null) return;

        _cts?.Cancel();

        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "MCP listener close failed");
        }

        if (_loop is not null)
        {
            // The accept loop unblocks by the listener closing under it; the wait is bounded so a
            // stuck request cannot hold up application shutdown.
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _logger?.LogDebug(ex, "MCP accept loop did not stop cleanly"); }
        }

        // Requests still running were cancelled with the token above; give them a moment to unwind so
        // they do not touch the protocol after it is gone.
        try { await Task.WhenAll(_inFlight.Keys).WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) { _logger?.LogDebug(ex, "MCP requests did not finish before the stop"); }

        _cts?.Dispose();
        _cts = null;
        _loop = null;
        _listener = null;
        Endpoint = null;
        _token = string.Empty;

        // Only the file this process wrote: a second window started from the same folder may have
        // published its own since, and deleting that would strand its clients.
        McpEndpointFile.DeleteIfOwnedBy(Environment.ProcessId, _endpointFilePath);
        _logger?.LogInformation("MCP server stopped");
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var listener = _listener;
        if (listener is null) return;

        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;     // listener stopped — the ordinary way out
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Requests are served concurrently, so a write that is waiting on the user (up to the
            // approval timeout) does not hold every read behind it. What must stay one-at-a-time is
            // the writes themselves, and McpProtocol serialises exactly those.
            if (!await _slots.WaitAsync(0, CancellationToken.None))
            {
                TryFail(context, 503, "too many concurrent requests");
                continue;
            }

            var task = ServeAsync(context, ct);
            _inFlight[task] = 0;
            _ = task.ContinueWith(
                finished =>
                {
                    _inFlight.TryRemove(finished, out _);
                    _slots.Release();
                },
                TaskScheduler.Default);
        }
    }

    private async Task ServeAsync(HttpListenerContext context, CancellationToken ct)
    {
        // Off the accept thread from the first line: the next request must be accepted while this one
        // is still reading its body.
        await Task.Yield();

        try
        {
            await HandleRequestAsync(context, ct);
        }
        catch (OperationCanceledException)
        {
            TryFail(context, 503, "server stopping");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP request failed");
            TryFail(context, 500, "internal error");
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
    {
        var request = context.Request;

        if (!IsLoopback(request))
        {
            TryFail(context, 403, "loopback only");
            return;
        }

        if (!IsOriginAcceptable(request))
        {
            _logger?.LogWarning("Rejected MCP request with Origin {Origin}", request.Headers["Origin"]);
            TryFail(context, 403, "origin not allowed");
            return;
        }

        if (!IsAuthorized(request))
        {
            TryFail(context, 401, "bearer token required");
            return;
        }

        // GET would be the server→client SSE stream. We never push, so saying 405 up front is more
        // honest than holding a stream open that will emit nothing.
        if (request.HttpMethod == "GET")
        {
            TryFail(context, 405, "this server does not send unsolicited messages");
            return;
        }

        if (request.HttpMethod == "DELETE")
        {
            Respond(context, 200, null);
            return;
        }

        if (request.HttpMethod != "POST")
        {
            TryFail(context, 405, "POST expected");
            return;
        }

        if (request.ContentLength64 > MaxBodyBytes)
        {
            TryFail(context, 413, "request body too large");
            return;
        }

        string body;
        using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
        {
            // Chunked bodies report no length, so the cap is enforced while reading as well.
            var buffer = new char[8192];
            var text = new StringBuilder();
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                text.Append(buffer, 0, read);
                if (text.Length > MaxBodyBytes)
                {
                    TryFail(context, 413, "request body too large");
                    return;
                }
            }

            body = text.ToString();
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Malformed MCP payload");
            Respond(context, 400, McpProtocol.Error(null, -32700, "Parse error"));
            return;
        }

        // The session the call arrived on. Unknown or absent ids fall back to an anonymous session
        // rather than an error: a single local client behind a bearer token gains little from strict
        // tracking, and the anonymous name is the safe one (it cannot be chosen by the caller).
        var known = _protocol.FindSession(request.Headers["Mcp-Session-Id"]);
        var requestContext = new McpRequestContext { Session = known };

        // A client may batch requests in an array; each is answered independently and notifications
        // contribute nothing to the reply.
        if (parsed is JsonArray batch)
        {
            if (batch.Count == 0)
            {
                Respond(context, 200, McpProtocol.Error(null, -32600, "Invalid Request: empty batch."));
                return;
            }

            var replies = new JsonArray();
            foreach (var item in batch)
            {
                var reply = await HandleItemAsync(item, requestContext, ct);
                if (reply is not null) replies.Add(reply);
            }

            if (replies.Count == 0) Respond(context, 202, null);
            else Respond(context, 200, replies, requestContext.Opened);
            return;
        }

        var response = await HandleItemAsync(parsed, requestContext, ct);
        if (response is null) Respond(context, 202, null);   // notification: accepted, nothing to say
        else Respond(context, 200, response, requestContext.Opened);
    }

    /// <summary>
    /// One message of a request or batch. The protocol answers malformed input itself; this only
    /// makes sure one item that throws unexpectedly becomes that item's error, not the batch's.
    /// </summary>
    private async Task<JsonNode?> HandleItemAsync(
        JsonNode? item, McpRequestContext requestContext, CancellationToken ct)
    {
        try
        {
            return await _protocol.HandleAsync(item, requestContext, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP message failed");
            var id = item is JsonObject obj && obj["id"] is JsonValue value ? value : null;
            return McpProtocol.Error(id, -32603, "Internal error");
        }
    }

    /// <summary>
    /// True when the caller really is on this machine. The prefix already restricts this, but the
    /// check is cheap and the consequence of being wrong is the whole point of the security section.
    /// </summary>
    private static bool IsLoopback(HttpListenerRequest request) =>
        request.RemoteEndPoint?.Address is { } address && IPAddress.IsLoopback(address);

    /// <summary>
    /// Blocks DNS-rebinding: a browser page can post to <c>127.0.0.1</c>, and it always sends an
    /// <c>Origin</c>. Non-browser clients send none, so "absent" is allowed and any remote origin is not.
    /// </summary>
    private static bool IsOriginAcceptable(HttpListenerRequest request)
    {
        var origin = request.Headers["Origin"];
        if (string.IsNullOrEmpty(origin)) return true;

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
               && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private bool IsAuthorized(HttpListenerRequest request)
    {
        var header = request.Headers["Authorization"];
        if (string.IsNullOrEmpty(header)) return false;

        // No token means the server is stopped, not that anything goes: without this an empty
        // "Bearer " header would compare equal to an empty secret.
        if (_token.Length == 0) return false;

        const string scheme = "Bearer ";
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;

        var presented = header[scheme.Length..].Trim();

        // Fixed-time comparison: the token is short and a local attacker can make many attempts.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_token));
    }

    private void Respond(
        HttpListenerContext context, int status, JsonNode? payload, McpSession? opened = null)
    {
        try
        {
            var response = context.Response;
            response.StatusCode = status;

            // Streamable HTTP lets the server hand out a session id on initialize: conforming clients
            // echo it back, which is how a prompt knows which client is asking. Only on a reply the
            // caller earned — the 401/403 refusals go out before the token was checked, and a caller
            // that failed authentication is told nothing about sessions.
            if (status < 400 && opened is not null)
                response.Headers["Mcp-Session-Id"] = opened.Id;

            if (payload is null)
            {
                response.ContentLength64 = 0;
                response.Close();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;
            response.ContentLength64 = bytes.Length;
            response.OutputStream.Write(bytes, 0, bytes.Length);
            response.Close();
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not write the MCP response");
        }
    }

    private void TryFail(HttpListenerContext context, int status, string message)
    {
        try
        {
            Respond(context, status, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = null,
                ["error"] = new JsonObject { ["code"] = -32600, ["message"] = message },
            });
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Could not write the MCP failure response");
        }
    }

    /// <summary>
    /// Asks the OS for a free loopback port by binding one and letting go. There is a race between
    /// releasing it and <see cref="HttpListener"/> claiming it, but the window is microseconds on a
    /// machine that is not exhausting ports, and the alternative — a hard-coded port — fails far more
    /// often and less visibly.
    /// </summary>
    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private static string GenerateToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
