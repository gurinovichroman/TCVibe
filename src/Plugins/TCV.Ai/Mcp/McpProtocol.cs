using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TCV.Ai.Agents;

namespace TCV.Ai.Mcp;

/// <summary>
/// The Model Context Protocol conversation: JSON-RPC 2.0 in, JSON-RPC 2.0 out, with the tool calls
/// routed to an <see cref="IAgentBridge"/>. Transport-free on purpose — <see cref="McpServer"/> owns
/// the HTTP listener, and everything interesting about the protocol (argument validation, scope
/// enforcement, error shaping) is testable here without opening a socket.
/// </summary>
public sealed partial class McpProtocol
{
    /// <summary>
    /// Protocol revision advertised on <c>initialize</c>. When the client asks for a different one we
    /// echo its version back rather than refusing: the parts of MCP this server implements —
    /// <c>tools/list</c> and <c>tools/call</c> — have been stable across revisions, and a hard version
    /// match would break every client on the next spec bump for no gain (ADR-0018). Only a value shaped
    /// like a revision date is echoed; anything else gets our own.
    /// </summary>
    public const string ProtocolVersion = "2025-06-18";

    // JSON-RPC 2.0 error codes.
    private const int ParseError = -32700;
    private const int InvalidRequest = -32600;
    private const int MethodNotFound = -32601;
    private const int InvalidParams = -32602;
    private const int InternalError = -32603;

    /// <summary>Sessions kept for name lookup; the oldest is forgotten past this.</summary>
    private const int MaxSessions = 64;

    private readonly IAgentBridge _bridge;
    private readonly string _serverName;
    private readonly string _serverVersion;
    private readonly ILogger? _logger;

    private readonly ConcurrentDictionary<string, McpSession> _sessions = new(StringComparer.Ordinal);
    private readonly McpSession _anonymous = new(string.Empty, McpSession.DefaultClientName);

    // Writes are the one thing that must stay one-at-a-time: each ends in a modal prompt, and two
    // prompts stacked on top of each other are exactly how a user approves the wrong one. Reads are not
    // held up behind it, and an identical proposal that is already waiting is joined rather than asked
    // about twice (a client that timed out and retried would otherwise prompt for the same thing again).
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Dictionary<string, Task<(AgentWriteProposal Proposal, AgentWriteDecision Decision)>> _pendingWrites = new();

    public McpProtocol(
        IAgentBridge bridge,
        string serverName = "tcvibe",
        string serverVersion = "1.0.0",
        ILogger? logger = null)
    {
        _bridge = bridge;
        _serverName = serverName;
        _serverVersion = serverVersion;
        _logger = logger;
    }

    /// <summary>
    /// Name of the client that initialised most recently. Diagnostic only: prompts use the name of the
    /// session the request arrived on, never this one.
    /// </summary>
    public string ClientName { get; private set; } = McpSession.DefaultClientName;

    /// <summary>The session with this id, or null (unknown, expired, or none sent).</summary>
    public McpSession? FindSession(string? id) =>
        !string.IsNullOrEmpty(id) && _sessions.TryGetValue(id, out var session) ? session : null;

    /// <summary>
    /// Handles one request and returns the response, or null when the message was a notification
    /// (JSON-RPC forbids replying to those, and a stray reply makes strict clients drop the session).
    /// </summary>
    public Task<JsonNode?> HandleAsync(JsonNode? request, CancellationToken ct = default) =>
        HandleAsync(request, new McpRequestContext(), ct);

    /// <summary>Same, with the transport's per-request context (which session the call arrived on).</summary>
    public async Task<JsonNode?> HandleAsync(
        JsonNode? request, McpRequestContext context, CancellationToken ct = default)
    {
        if (request is not JsonObject message)
            return Error(null, InvalidRequest, "Invalid Request: expected a JSON-RPC object.");

        // An id is a string, a number or null. Anything else cannot be echoed back faithfully, and
        // replying with it would only produce a second malformed message.
        var hasId = message.ContainsKey("id");
        var idNode = hasId ? message["id"] : null;
        if (idNode is not null && idNode is not JsonValue)
            return Error(null, InvalidRequest, "Invalid Request: id must be a string, a number or null.");

        if (message["method"] is not JsonValue methodValue
            || !methodValue.TryGetValue<string>(out var method)
            || string.IsNullOrEmpty(method))
        {
            return Error(idNode, InvalidRequest, "Invalid Request: method must be a non-empty string.");
        }

        // No id means notification, and a notification is never answered — not even to say it was
        // malformed, and never by running it: a tool call nobody can hear the result of is not one to run.
        if (!hasId) return null;
        var id = idNode;

        var paramsNode = message["params"];
        if (paramsNode is not null and not JsonObject)
            return Error(id, InvalidParams, "Invalid params: expected an object.");
        var parameters = paramsNode as JsonObject;

        try
        {
            switch (method)
            {
                case "initialize":
                    return Result(id, Initialize(parameters, context));

                case "notifications/initialized":
                case "initialized":
                    return Result(id, new JsonObject());

                case "ping":
                    return Result(id, new JsonObject());

                case "tools/list":
                    return Result(id, new JsonObject
                    {
                        ["tools"] = McpToolCatalog.Describe(_bridge.WritesEnabled),
                    });

                case "tools/call":
                    return await CallToolAsync(id, parameters, context, ct);

                // Declared unsupported rather than silently empty, so a client does not wait on a
                // capability we never advertised.
                case "resources/list":
                case "prompts/list":
                    return Error(id, MethodNotFound, $"{method} is not supported: this server exposes tools only.");

                default:
                    return Error(id, MethodNotFound, $"Unknown method: {method}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP method {Method} failed", method);
            return Error(id, InternalError, "Internal error: " + ex.Message);
        }
    }

    private JsonObject Initialize(JsonObject? parameters, McpRequestContext context)
    {
        var requested = AsString(parameters?["protocolVersion"]);
        var clientName = McpSession.Sanitize(AsString(parameters?["clientInfo"]?["name"]));

        var opened = OpenSession(clientName);
        context.Opened = opened;
        ClientName = clientName;

        var version = requested is not null && RevisionShape().IsMatch(requested) ? requested : ProtocolVersion;
        _logger?.LogInformation("MCP client {Client} connected (protocol {Version})", clientName, version);

        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = _serverName,
                ["version"] = _serverVersion,
            },
            // Sent as instructions rather than baked into each tool description: this is the framing
            // an agent needs once, not per call.
            ["instructions"] =
                "This server exposes the running TCVibe window. Call tcv_selection first — "
                + "it tells you which files the user has selected and which directories you are allowed "
                + "to touch. A path is one of three forms: a Windows path, an archive path "
                + "(zip://D:\\dist.zip!/logs/app.log), or a panel path (panel://active/… or "
                + "panel://opposite/…) for whatever a panel currently has mounted; a panel path is "
                + "absolute inside that mount (panel://active/logs/app.log), exactly as tcv_selection "
                + "and tcv_list print it. An SFTP, FTP or "
                + "WebDAV location is reached through panel:// only — those schemes are refused when "
                + "written directly, because the server never opens connections or holds credentials "
                + "of its own. "
                + "Write tools do not act immediately: they propose an operation that the user approves, "
                + "and approved operations are recorded in the manager's undoable operation log.",
        };
    }

    private McpSession OpenSession(string clientName)
    {
        var session = new McpSession(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), clientName);

        _sessions[session.Id] = session;

        if (_sessions.Count > MaxSessions)
        {
            foreach (var old in _sessions.Values.OrderBy(s => s.CreatedUtc).Take(_sessions.Count - MaxSessions))
                _sessions.TryRemove(old.Id, out _);
        }

        return session;
    }

    // ─────────────────────────────── tools/call ───────────────────────────────

    private async Task<JsonNode> CallToolAsync(
        JsonNode? id, JsonObject? parameters, McpRequestContext context, CancellationToken ct)
    {
        var name = AsString(parameters?["name"]);
        if (string.IsNullOrEmpty(name))
            return Error(id, InvalidParams, "Invalid params: params.name must be the name of a tool.");

        var argsNode = parameters?["arguments"];
        if (argsNode is not null and not JsonObject)
            return Error(id, InvalidParams, "Invalid params: params.arguments must be an object.");
        var args = argsNode as JsonObject ?? new JsonObject();

        var client = (context.Session ?? _anonymous).ClientName;

        try
        {
            if (McpToolCatalog.MutatingTools.Contains(name) && !_bridge.WritesEnabled)
                return Result(id, ToolError(McpError.WritesDisabled()));

            var payload = name switch
            {
                McpToolCatalog.Selection   => ToolOk(await SelectionAsync(ct)),
                McpToolCatalog.List        => ToolOk(await ListAsync(args, ct)),
                McpToolCatalog.Read        => ToolOk(await ReadAsync(args, ct)),
                McpToolCatalog.Search      => ToolOk(await SearchAsync(args, ct)),
                McpToolCatalog.Operations  => ToolOk(await OperationsAsync(args, ct)),
                McpToolCatalog.Copy        => ToolOk(await WriteAsync(AgentWriteKind.Copy, args, client, ct)),
                McpToolCatalog.Move        => ToolOk(await WriteAsync(AgentWriteKind.Move, args, client, ct)),
                McpToolCatalog.Delete      => ToolOk(await WriteAsync(AgentWriteKind.Delete, args, client, ct)),
                _ => ToolError(new McpError(
                    McpError.Unsupported,
                    $"Unknown tool: {name}",
                    "Call tools/list for the current set.")),
            };

            return Result(id, payload);
        }
        catch (McpToolException ex)
        {
            return Result(id, ToolError(ex.Error));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "MCP tool {Tool} failed", name);
            return Result(id, ToolError(new McpError(McpError.Internal, ex.Message,
                "The manager logged the failure; retry or ask the user to check the app.")));
        }
    }

    private async Task<JsonObject> SelectionAsync(CancellationToken ct)
    {
        var context = await _bridge.GetContextAsync(ct);

        var selection = new JsonArray();
        foreach (var path in context.Selection) selection.Add(path);

        var roots = new JsonArray();
        foreach (var root in context.ApprovedRoots) roots.Add(root);

        return new JsonObject
        {
            ["active_directory"] = context.ActivePath,
            ["opposite_directory"] = context.OppositePath,
            ["selection"] = selection,
            ["workspace"] = context.Workspace,
            ["branch"] = context.Branch,
            ["active_scheme"] = context.ActiveScheme,
            ["active_is_mount"] = context.ActiveIsMounted,
            ["approved_roots"] = roots,
            ["writes_enabled"] = context.WritesEnabled,
        };
    }

    private async Task<JsonObject> ListAsync(JsonObject args, CancellationToken ct)
    {
        var path = await PathArgumentAsync(args, "path", ct);
        var entries = await _bridge.ListAsync(path, ct);

        var items = new JsonArray();
        foreach (var entry in entries)
        {
            items.Add(new JsonObject
            {
                ["path"] = entry.Path,
                ["name"] = entry.Name,
                ["is_directory"] = entry.IsDirectory,
                ["size"] = entry.Size,
                ["modified_utc"] = entry.ModifiedUtc.ToString("O"),
            });
        }

        return new JsonObject { ["path"] = path, ["entries"] = items };
    }

    private async Task<JsonObject> ReadAsync(JsonObject args, CancellationToken ct)
    {
        var path = RequiredString(args, "path");
        var resolved = ResolvePath(path);

        var maxBytes = OptionalInt(args, "max_bytes", McpToolCatalog.DefaultReadBytes);
        maxBytes = Math.Clamp(maxBytes, 1, McpToolCatalog.MaxReadBytes);

        var content = await _bridge.ReadAsync(resolved, maxBytes, ct);

        return new JsonObject
        {
            ["path"] = content.Path,
            ["text"] = content.Text,
            ["truncated"] = content.Truncated,
            ["size"] = content.Size,
            ["encoding"] = content.Encoding,
        };
    }

    private async Task<JsonObject> SearchAsync(JsonObject args, CancellationToken ct)
    {
        var query = RequiredString(args, "query");
        var path = await PathArgumentAsync(args, "path", ct);

        // Search walks a directory tree on the local disk. A panel:// or archive path resolves fine for
        // tcv_list and tcv_read, but there is nothing to walk with the disk engine, and the default
        // (the active panel) is a panel:// path whenever that panel has a mount open.
        if (AgentPathRef.Parse(path).Kind != AgentPathKind.Local)
        {
            throw new McpToolException(McpError.Invalid(
                "tcv_search searches a Windows directory, and no such directory was given.",
                "Pass path as a Windows directory (see approved_roots in tcv_selection); "
                + "the active panel is showing a mount, so there is no default."));
        }

        var request = new AgentSearchRequest(
            query,
            path,
            OptionalBool(args, "semantic", false),
            OptionalBool(args, "names_only", false),
            Math.Clamp(OptionalInt(args, "limit", 50), 1, 500));

        var hits = await _bridge.SearchAsync(request, ct);

        var items = new JsonArray();
        foreach (var hit in hits)
        {
            var item = new JsonObject
            {
                ["path"] = hit.Path,
                ["text"] = hit.Text,
            };
            if (hit.Line > 0) item["line"] = hit.Line;
            if (hit.Score != 0) item["score"] = Math.Round(hit.Score, 4);
            items.Add(item);
        }

        return new JsonObject
        {
            ["query"] = query,
            ["path"] = path,
            ["semantic"] = request.Semantic,
            ["hits"] = items,
        };
    }

    private async Task<JsonObject> OperationsAsync(JsonObject args, CancellationToken ct)
    {
        var limit = Math.Clamp(OptionalInt(args, "limit", 20), 1, 200);
        var records = await _bridge.RecentOperationsAsync(limit, ct);

        var items = new JsonArray();
        foreach (var record in records)
        {
            var item = new JsonObject
            {
                ["at_utc"] = record.AtUtc.ToString("O"),
                ["kind"] = record.Kind,
                ["source"] = record.Source,
                ["items"] = record.ItemCount,
                ["outcome"] = record.Outcome,
            };
            if (!string.IsNullOrEmpty(record.Destination)) item["destination"] = record.Destination;
            if (!string.IsNullOrEmpty(record.Origin)) item["requested_by"] = record.Origin;
            items.Add(item);
        }

        return new JsonObject { ["operations"] = items };
    }

    private async Task<JsonObject> WriteAsync(
        AgentWriteKind kind, JsonObject args, string client, CancellationToken ct)
    {
        var sourceKey = kind == AgentWriteKind.Delete ? "paths" : "sources";
        var sources = RequiredStringArray(args, sourceKey).Select(ResolvePath).ToList();

        var destination = kind == AgentWriteKind.Delete
            ? string.Empty
            : ResolvePath(RequiredString(args, "destination"));

        var proposal = AgentWriteProposal.Create(kind, sources, destination, client);
        _logger?.LogInformation("Agent {Client} proposed: {Summary}", client, proposal.Summary());

        var (decided, decision) = await SubmitOnceAsync(proposal, ct);

        // A refusal is not a protocol error: the agent asked a legitimate question and got "no".
        // It comes back as data with the reason, so the agent can adapt instead of retrying blindly.
        return new JsonObject
        {
            ["approved"] = decision.Approved,
            ["reason"] = decision.Reason,
            ["proposal_id"] = decided.Id,
            ["summary"] = decided.Summary(),
        };
    }

    /// <summary>
    /// Sends the proposal through the bridge — one at a time, and once per distinct request: a second
    /// identical call that arrives while the first is still waiting for the user shares its answer.
    /// </summary>
    private async Task<(AgentWriteProposal Proposal, AgentWriteDecision Decision)> SubmitOnceAsync(
        AgentWriteProposal proposal, CancellationToken ct)
    {
        var key = string.Join(
            '\n', proposal.Agent, proposal.Kind, proposal.Destination, string.Join('\u0001', proposal.Sources));

        Task<(AgentWriteProposal, AgentWriteDecision)> task;
        lock (_pendingWrites)
        {
            if (!_pendingWrites.TryGetValue(key, out task!))
            {
                task = RunWriteAsync(proposal, key, ct);
                _pendingWrites[key] = task;
            }
        }

        return await task.WaitAsync(ct);
    }

    private async Task<(AgentWriteProposal, AgentWriteDecision)> RunWriteAsync(
        AgentWriteProposal proposal, string key, CancellationToken ct)
    {
        // Called under the _pendingWrites lock, before the task is registered. Without this yield a
        // proposal that is refused synchronously (no window, writes off) runs the finally below inside
        // the caller's lock — the key is removed before it was added, and the finished task is then
        // registered and answers every identical retry from a stale entry forever.
        await Task.Yield();

        try
        {
            await _writeGate.WaitAsync(ct);
            try
            {
                return (proposal, await _bridge.ProposeWriteAsync(proposal, ct));
            }
            finally
            {
                _writeGate.Release();
            }
        }
        finally
        {
            lock (_pendingWrites) _pendingWrites.Remove(key);
        }
    }

    // ─────────────────────────────── Arguments ───────────────────────────────

    /// <summary>
    /// A path argument that defaults to the active panel's directory when omitted — which is what
    /// "list the files here" means and saves the agent a round trip through tcv_selection.
    /// </summary>
    private async Task<string> PathArgumentAsync(JsonObject args, string key, CancellationToken ct)
    {
        if (AsString(args[key]) is { Length: > 0 } given)
            return ResolvePath(given);

        var context = await _bridge.GetContextAsync(ct);

        // A mount the agent may not read has its directory withheld (ADR-0023). Asking the panel itself
        // brings back the bridge's refusal and its hint, where an empty path would only say «empty».
        if (context.ActivePath.Length == 0 && context.ActiveIsMounted)
        {
            return ResolvePath($"{AgentPathRef.PanelScheme}://{AgentPathRef.ActivePanel}/");
        }

        return ResolvePath(context.ActivePath);
    }

    /// <summary>
    /// Turns a requested path into one the bridge may act on, or throws a structured refusal.
    /// <para>
    /// Which part gets scope-checked depends on the form (see <see cref="AgentPathRef"/>): a local path
    /// is checked itself, an archive path has its <em>archive file</em> checked — the entries inside
    /// cannot escape it — and a <c>panel://</c> path is passed on unchecked, because only the bridge
    /// knows what the panel has mounted: it checks an archive's file the same way and refuses a server
    /// connection unless the user allowed it (ADR-0023).
    /// </para>
    /// </summary>
    private string ResolvePath(string path)
    {
        var reference = AgentPathRef.Parse(path);

        switch (reference.Kind)
        {
            case AgentPathKind.Local:
                return CheckScope(reference.Container);

            case AgentPathKind.Archive:
                // Rewritten with the resolved archive path so the bridge opens the real file rather
                // than whatever a symlink pointed at.
                return (reference with { Container = CheckScope(reference.Container) }).ToString();

            default:
                return reference.ToString();
        }
    }

    private string CheckScope(string localPath)
    {
        var scope = _bridge.CurrentScope().Check(localPath);
        if (!scope.IsAllowed) throw new McpToolException(McpError.FromScope(scope));

        return scope.ResolvedPath;
    }

    /// <summary>The string a node holds, or null when it is absent or of another type.</summary>
    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string RequiredString(JsonObject args, string key) =>
        AsString(args[key]) is { Length: > 0 } value
            ? value
            : throw new McpToolException(McpError.Invalid(
                $"Missing required argument '{key}'.", $"Pass arguments.{key} as a string."));

    private static IReadOnlyList<string> RequiredStringArray(JsonObject args, string key)
    {
        if (args[key] is not JsonArray array || array.Count == 0)
        {
            throw new McpToolException(McpError.Invalid(
                $"Missing required argument '{key}'.", $"Pass arguments.{key} as a non-empty array of paths."));
        }

        var result = new List<string>(array.Count);
        foreach (var item in array)
        {
            // Strict: quietly dropping an element would make «delete these three» act on two.
            if (AsString(item) is not { Length: > 0 } value)
            {
                throw new McpToolException(McpError.Invalid(
                    $"Argument '{key}' contains an entry that is not a non-empty string.",
                    "Every element must be a non-empty path string."));
            }

            result.Add(value);
        }

        return result;
    }

    /// <summary>
    /// Reads an optional number. <see cref="JsonValue.TryGetValue{T}"/> rather than a
    /// <c>ValueKind</c> switch: a node parsed from the wire is backed by a <c>JsonElement</c>, but one
    /// built in memory is backed by a plain CLR value, and asking the latter for its element throws —
    /// which would silently turn every argument into its default.
    /// </summary>
    private static int OptionalInt(JsonObject args, string key, int fallback)
    {
        if (args[key] is not JsonValue value) return fallback;

        if (value.TryGetValue<int>(out var number)) return number;

        // Models do send "50" where a number belongs.
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed)) return parsed;

        return fallback;
    }

    private static bool OptionalBool(JsonObject args, string key, bool fallback)
    {
        if (args[key] is not JsonValue value) return fallback;

        if (value.TryGetValue<bool>(out var flag)) return flag;
        if (value.TryGetValue<string>(out var text) && bool.TryParse(text, out var parsed)) return parsed;

        return fallback;
    }

    // ─────────────────────────────── Envelopes ───────────────────────────────

    /// <summary>
    /// A successful tool result. The payload travels as one JSON text block, which is what MCP
    /// clients render and what models parse most reliably.
    /// </summary>
    private static JsonObject ToolOk(JsonObject payload) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = payload.ToJsonString(WriteOptions),
            },
        },
        ["isError"] = false,
    };

    /// <summary>
    /// A failed tool call: <c>isError</c> so the client marks it, and the structured
    /// <c>{code, message, hint}</c> as the text so the agent can act on it.
    /// </summary>
    private static JsonObject ToolError(McpError error) => new()
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = new JsonObject
                {
                    ["error"] = new JsonObject
                    {
                        ["code"] = error.Code,
                        ["message"] = error.Message,
                        ["hint"] = error.Hint,
                    },
                }.ToJsonString(WriteOptions),
            },
        },
        ["isError"] = true,
    };

    private static JsonObject Result(JsonNode? id, JsonNode payload) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["result"] = payload,
    };

    internal static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex RevisionShape();

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
}
