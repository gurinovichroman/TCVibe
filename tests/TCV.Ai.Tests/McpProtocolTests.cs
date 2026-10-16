using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using TCV.Ai.Agents;
using TCV.Ai.Mcp;

namespace TCV.Ai.Tests;

/// <summary>
/// The JSON-RPC conversation and, more importantly, the guarantees around it: notifications get no
/// reply, a refused path comes back as a structured error the agent can act on, and the write tools
/// do not exist while writes are off.
/// </summary>
public class McpProtocolTests
{
    private static (McpProtocol Protocol, FakeAgentBridge Bridge) Build(bool writesEnabled = false)
    {
        var bridge = new FakeAgentBridge { WritesEnabled = writesEnabled };
        return (new McpProtocol(bridge), bridge);
    }

    private static JsonObject Request(string method, JsonObject? parameters = null, int? id = 1)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (id is not null) message["id"] = id;
        if (parameters is not null) message["params"] = parameters;
        return message;
    }

    private static JsonObject Call(string tool, JsonObject? arguments = null) =>
        Request("tools/call", new JsonObject
        {
            ["name"] = tool,
            ["arguments"] = arguments ?? new JsonObject(),
        });

    /// <summary>The tool payload, unwrapped from the MCP content envelope.</summary>
    private static JsonObject Payload(JsonNode? response)
    {
        var text = response!["result"]!["content"]![0]!["text"]!.GetValue<string>();
        return (JsonObject)JsonNode.Parse(text)!;
    }

    private static bool IsError(JsonNode? response) =>
        response!["result"]!["isError"]!.GetValue<bool>();

    // ── Handshake ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Initialize_AdvertisesToolsAndEchoesTheRequestedVersion()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Request("initialize", new JsonObject
        {
            ["protocolVersion"] = "2099-01-01",
            ["clientInfo"] = new JsonObject { ["name"] = "claude-code" },
        }));

        response!["result"]!["protocolVersion"]!.GetValue<string>().Should().Be("2099-01-01");
        response["result"]!["capabilities"]!["tools"].Should().NotBeNull();
        protocol.ClientName.Should().Be("claude-code");
    }

    [Fact]
    public async Task Initialize_WithoutAVersion_AdvertisesOurOwn()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Request("initialize"));

        response!["result"]!["protocolVersion"]!.GetValue<string>()
            .Should().Be(McpProtocol.ProtocolVersion);
    }

    [Fact]
    public async Task InitializedNotification_GetsNoReply()
    {
        var (protocol, _) = Build();

        // A reply to a notification makes strict clients drop the session, so null is the contract.
        var response = await protocol.HandleAsync(Request("notifications/initialized", id: null));

        response.Should().BeNull();
    }

    [Fact]
    public async Task UnknownNotification_GetsNoReply()
    {
        var (protocol, _) = Build();

        (await protocol.HandleAsync(Request("something/else", id: null))).Should().BeNull();
    }

    [Fact]
    public async Task UnknownMethodWithAnId_GetsMethodNotFound()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Request("does/notExist"));

        response!["error"]!["code"]!.GetValue<int>().Should().Be(-32601);
    }

    [Fact]
    public async Task NonObjectRequest_IsAnInvalidRequest()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(JsonValue.Create(42));

        response!["error"]!["code"]!.GetValue<int>().Should().Be(-32600);
    }

    // ── tools/list ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolsList_WithWritesOff_OmitsTheMutatingTools()
    {
        var (protocol, _) = Build(writesEnabled: false);

        var response = await protocol.HandleAsync(Request("tools/list"));
        var names = response!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();

        names.Should().Contain(McpToolCatalog.Selection);
        names.Should().NotContain(McpToolCatalog.Copy);
        names.Should().NotContain(McpToolCatalog.Delete);
    }

    [Fact]
    public async Task ToolsList_WithWritesOn_IncludesTheMutatingTools()
    {
        var (protocol, _) = Build(writesEnabled: true);

        var response = await protocol.HandleAsync(Request("tools/list"));
        var names = response!["result"]!["tools"]!.AsArray()
            .Select(t => t!["name"]!.GetValue<string>()).ToList();

        names.Should().Contain([McpToolCatalog.Copy, McpToolCatalog.Move, McpToolCatalog.Delete]);
    }

    [Fact]
    public async Task ToolsList_EveryToolDeclaresAnObjectSchema()
    {
        var (protocol, _) = Build(writesEnabled: true);

        var response = await protocol.HandleAsync(Request("tools/list"));

        foreach (var tool in response!["result"]!["tools"]!.AsArray())
        {
            tool!["inputSchema"]!["type"]!.GetValue<string>().Should().Be("object");
            tool["description"]!.GetValue<string>().Should().NotBeEmpty();
        }
    }

    [Fact]
    public async Task ToolsList_AdvertisesNoSchemeTheParserRefuses()
    {
        // The catalog used to offer sftp:// and dav:// paths that AgentPathRef.Parse rejects
        // unconditionally, and never mentioned panel:// — the form that actually reaches a mount.
        var (protocol, _) = Build(writesEnabled: true);

        var response = await protocol.HandleAsync(Request("tools/list"));
        var text = response!["result"]!["tools"]!.ToJsonString();

        foreach (var refused in new[] { "sftp://", "ftp://", "ftps://", "dav://", "davs://" })
            text.Should().NotContain(refused);

        text.Should().Contain("panel://");
    }

    [Fact]
    public async Task Initialize_InstructionsNameOnlyReachableFormsOfAPath()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Request("initialize"));
        var instructions = response!["result"]!["instructions"]!.GetValue<string>();

        instructions.Should().Contain("panel://");
        instructions.Should().NotContain("dav://");
    }

    // ── tcv_selection ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Selection_ReportsPanelsSelectionAndScope()
    {
        var (protocol, _) = Build();

        var payload = Payload(await protocol.HandleAsync(Call(McpToolCatalog.Selection)));

        payload["active_directory"]!.GetValue<string>().Should().Be(@"D:\repo");
        payload["branch"]!.GetValue<string>().Should().Be("main");
        payload["selection"]!.AsArray().Should().HaveCount(1);
        payload["approved_roots"]!.AsArray().Should().HaveCount(1);
        payload["writes_enabled"]!.GetValue<bool>().Should().BeFalse();
    }

    // ── Scope enforcement ─────────────────────────────────────────────────────

    [Fact]
    public async Task Read_PathOutsideScope_ReturnsAStructuredError()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(
            Call(McpToolCatalog.Read, new JsonObject { ["path"] = @"C:\Windows\win.ini" }));

        IsError(response).Should().BeTrue();

        var error = Payload(response)["error"]!;
        error["code"]!.GetValue<string>().Should().Be(McpError.OutsideScope);
        error["hint"]!.GetValue<string>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task Read_WithNoScopeApproved_SaysSoAndTellsTheUserWhatToDo()
    {
        var (protocol, bridge) = Build();
        bridge.Scope = new PathScope([]);

        var response = await protocol.HandleAsync(
            Call(McpToolCatalog.Read, new JsonObject { ["path"] = @"D:\repo\a.cs" }));

        Payload(response)["error"]!["code"]!.GetValue<string>().Should().Be(McpError.NoScope);
    }

    [Fact]
    public async Task Read_PanelPath_IsLeftToTheBridgeToCheck()
    {
        // A panel:// path has no disk path to resolve here: only the bridge knows what the panel has
        // mounted, and it applies the panel rule itself (ADR-0023, AgentHardeningTests).
        var (protocol, bridge) = Build();
        bridge.Scope = new PathScope([]);

        var response = await protocol.HandleAsync(
            Call(McpToolCatalog.Read, new JsonObject { ["path"] = "panel://active/app.log" }));

        IsError(response).Should().BeFalse();
        bridge.LastReadPath.Should().Be("panel://active/app.log");
    }

    // ── Arguments ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Read_WithoutAPath_ReportsTheMissingArgument()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Call(McpToolCatalog.Read));

        Payload(response)["error"]!["code"]!.GetValue<string>().Should().Be(McpError.InvalidParams);
    }

    [Fact]
    public async Task Read_ClampsMaxBytesToTheHardCap()
    {
        var (protocol, bridge) = Build();

        await protocol.HandleAsync(Call(McpToolCatalog.Read, new JsonObject
        {
            ["path"] = @"D:\repo\a.cs",
            ["max_bytes"] = 999_999_999,
        }));

        bridge.LastMaxBytes.Should().Be(McpToolCatalog.MaxReadBytes);
    }

    [Fact]
    public async Task Read_WithMaxBytesAsAString_StillWorks()
    {
        // Models do send "4096" where a number belongs; a wrong shape must not fail the call.
        var (protocol, bridge) = Build();

        await protocol.HandleAsync(Call(McpToolCatalog.Read, new JsonObject
        {
            ["path"] = @"D:\repo\a.cs",
            ["max_bytes"] = "4096",
        }));

        bridge.LastMaxBytes.Should().Be(4096);
    }

    [Fact]
    public async Task List_WithoutAPath_FallsBackToTheActivePanel()
    {
        var (protocol, bridge) = Build();

        await protocol.HandleAsync(Call(McpToolCatalog.List));

        bridge.LastListedPath.Should().Be(@"D:\repo");
    }

    [Fact]
    public async Task List_WithoutAPathWhileTheMountIsWithheld_AsksThePanelForItsRefusal()
    {
        // The bridge withholds the directory of a mount the agent may not read; the panel itself then
        // answers with the reason, rather than the protocol with «the path is empty» (ADR-0023).
        var (protocol, bridge) = Build();
        bridge.Context = bridge.Context with { ActivePath = string.Empty, ActiveIsMounted = true };

        await protocol.HandleAsync(Call(McpToolCatalog.List));

        bridge.LastListedPath.Should().Be("panel://active/");
    }

    [Fact]
    public async Task Search_PassesTheFlagsThrough()
    {
        var (protocol, bridge) = Build();

        await protocol.HandleAsync(Call(McpToolCatalog.Search, new JsonObject
        {
            ["query"] = "where do we decide a panel is remote",
            ["semantic"] = true,
            ["limit"] = 5,
        }));

        bridge.LastSearch!.Semantic.Should().BeTrue();
        bridge.LastSearch.Limit.Should().Be(5);
        bridge.LastSearch.Path.Should().Be(@"D:\repo");
    }

    [Fact]
    public async Task Search_ClampsAnAbsurdLimit()
    {
        var (protocol, bridge) = Build();

        await protocol.HandleAsync(Call(McpToolCatalog.Search, new JsonObject
        {
            ["query"] = "x",
            ["limit"] = 100_000,
        }));

        bridge.LastSearch!.Limit.Should().Be(500);
    }

    // ── Writes ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_WithWritesDisabled_IsRefusedWithReadOnly()
    {
        var (protocol, _) = Build(writesEnabled: false);

        var response = await protocol.HandleAsync(Call(McpToolCatalog.Copy, new JsonObject
        {
            ["sources"] = new JsonArray { @"D:\repo\a.cs" },
            ["destination"] = @"D:\repo\sub",
        }));

        Payload(response)["error"]!["code"]!.GetValue<string>().Should().Be(McpError.ReadOnly);
    }

    [Fact]
    public async Task Copy_WhenApproved_ReportsSuccessAndTheProposalReachedTheGate()
    {
        var (protocol, bridge) = Build(writesEnabled: true);

        var response = await protocol.HandleAsync(Call(McpToolCatalog.Copy, new JsonObject
        {
            ["sources"] = new JsonArray { @"D:\repo\a.cs" },
            ["destination"] = @"D:\repo\sub",
        }));

        IsError(response).Should().BeFalse();
        Payload(response)["approved"]!.GetValue<bool>().Should().BeTrue();

        bridge.LastProposal!.Kind.Should().Be(AgentWriteKind.Copy);
        bridge.LastProposal.Sources.Should().ContainSingle();
    }

    [Fact]
    public async Task Copy_WhenRefused_ComesBackAsDataWithAReasonRatherThanAnError()
    {
        // A refusal is a legitimate answer to a legitimate question: the agent needs to adapt, not retry.
        var (protocol, bridge) = Build(writesEnabled: true);
        bridge.Decision = AgentWriteDecision.Reject("The user refused the operation.");

        var response = await protocol.HandleAsync(Call(McpToolCatalog.Copy, new JsonObject
        {
            ["sources"] = new JsonArray { @"D:\repo\a.cs" },
            ["destination"] = @"D:\repo\sub",
        }));

        IsError(response).Should().BeFalse();

        var payload = Payload(response);
        payload["approved"]!.GetValue<bool>().Should().BeFalse();
        payload["reason"]!.GetValue<string>().Should().Contain("refused");
    }

    [Fact]
    public async Task Delete_TakesItsPathsFromThePathsArgument()
    {
        var (protocol, bridge) = Build(writesEnabled: true);

        await protocol.HandleAsync(Call(McpToolCatalog.Delete, new JsonObject
        {
            ["paths"] = new JsonArray { @"D:\repo\a.cs", @"D:\repo\b.cs" },
        }));

        bridge.LastProposal!.Kind.Should().Be(AgentWriteKind.Delete);
        bridge.LastProposal.Sources.Should().HaveCount(2);
        bridge.LastProposal.Destination.Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_WithAnEmptyArray_ReportsTheMissingArgument()
    {
        var (protocol, _) = Build(writesEnabled: true);

        var response = await protocol.HandleAsync(
            Call(McpToolCatalog.Delete, new JsonObject { ["paths"] = new JsonArray() }));

        Payload(response)["error"]!["code"]!.GetValue<string>().Should().Be(McpError.InvalidParams);
    }

    [Fact]
    public async Task Copy_WithAPathOutsideScope_NeverReachesTheGate()
    {
        var (protocol, bridge) = Build(writesEnabled: true);

        await protocol.HandleAsync(Call(McpToolCatalog.Copy, new JsonObject
        {
            ["sources"] = new JsonArray { @"C:\Windows\win.ini" },
            ["destination"] = @"D:\repo",
        }));

        bridge.LastProposal.Should().BeNull();
    }

    // ── Failures ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnknownTool_IsReportedAsUnsupported()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Call("tcv_nonsense"));

        Payload(response)["error"]!["code"]!.GetValue<string>().Should().Be(McpError.Unsupported);
    }

    [Fact]
    public async Task BridgeThrowingAStructuredError_IsPassedThrough()
    {
        var (protocol, bridge) = Build();
        bridge.ReadError = McpError.NotFoundAt(@"D:\repo\gone.cs");

        var response = await protocol.HandleAsync(
            Call(McpToolCatalog.Read, new JsonObject { ["path"] = @"D:\repo\gone.cs" }));

        IsError(response).Should().BeTrue();
        Payload(response)["error"]!["code"]!.GetValue<string>().Should().Be(McpError.NotFound);
    }

    [Fact]
    public async Task Operations_ReturnsTheJournalWithItsOriginTag()
    {
        var (protocol, bridge) = Build();
        bridge.Operations.Add(new AgentOperationRecord(
            DateTime.UtcNow, "Copy", @"D:\repo\a.cs", @"D:\repo\sub", 1, "Completed", "claude-code"));

        var payload = Payload(await protocol.HandleAsync(Call(McpToolCatalog.Operations)));

        var first = payload["operations"]!.AsArray()[0]!;
        first["kind"]!.GetValue<string>().Should().Be("Copy");
        first["requested_by"]!.GetValue<string>().Should().Be("claude-code");
    }

    [Fact]
    public async Task EveryToolResultIsValidJsonInsideTheContentEnvelope()
    {
        var (protocol, _) = Build();

        var response = await protocol.HandleAsync(Call(McpToolCatalog.Selection));
        var text = response!["result"]!["content"]![0]!["text"]!.GetValue<string>();

        var act = () => JsonDocument.Parse(text);
        act.Should().NotThrow();
    }
}
