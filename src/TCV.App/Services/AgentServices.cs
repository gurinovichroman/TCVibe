using TCV.Ai.Agents;
using TCV.Ai.Llm;
using TCV.Ai.Semantic;
using TCV.App.Agents;

namespace TCV.App.Services;

/// <summary>
/// The agent-aware layer handed around as one dependency: recorded sessions, the MCP server, the
/// write gate, the language-model client and the semantic index.
/// <para>
/// Bundled for the same reason as <see cref="GitWorkflowServices"/>: <c>MainViewModel</c> already
/// takes more constructor parameters than it should (TRAP-17, task 6.26), and five more would be five
/// more reasons never to split it. Like that bundle this is not a facade — it exposes the services as
/// they are, so the logic stays in <c>TCV.Ai</c> where it is testable without WPF.
/// </para>
/// </summary>
public sealed class AgentServices
{
    public AgentServices(
        AgentSessionStore sessions,
        AgentWriteGate gate,
        AgentBridge bridge,
        McpServerHost mcp,
        LlmClient llm,
        SemanticIndexService semantic)
    {
        Sessions = sessions;
        Gate = gate;
        Bridge = bridge;
        Mcp = mcp;
        Llm = llm;
        Semantic = semantic;
    }

    /// <summary>Recorded agent runs, the evidence behind edit provenance (6.12).</summary>
    public AgentSessionStore Sessions { get; }

    /// <summary>Approve/refuse gate in front of every agent-initiated mutation (6.13b).</summary>
    public AgentWriteGate Gate { get; }

    /// <summary>What the MCP server asks the application (6.13).</summary>
    public AgentBridge Bridge { get; }

    /// <summary>The server's lifetime and the "what is exposed" state (6.13).</summary>
    public McpServerHost Mcp { get; }

    /// <summary>The single path to a language model (6.14, ADR-0007).</summary>
    public LlmClient Llm { get; }

    /// <summary>Local embedding index behind semantic search (6.16).</summary>
    public SemanticIndexService Semantic { get; }

    /// <summary>
    /// Connects the window to the layer. Called once by <c>MainViewModel</c>: the bridge and the gate
    /// are singletons that need the live panels, and the window is transient.
    /// </summary>
    public void Attach(IAgentHost host)
    {
        Bridge.Host = host;
        Gate.Host = host;
        StartSessionHousekeeping();
    }

    private Timer? _heartbeat;

    /// <summary>
    /// Sessions a crashed run left open are closed now, ended at the last heartbeat, and a heartbeat is
    /// kept while this window lives (TRAP-31). <c>SaveState</c> closes sessions on a normal exit; a
    /// crash or a kill skips it, and an open session claims every change under its root — including
    /// the ones made while the application was not even running.
    /// <para>
    /// Only the window that owns the sessions does this. A guest workspace window shares
    /// <c>agent-sessions.json</c> with it and must neither close its sessions nor pose as it.
    /// </para>
    /// </summary>
    private void StartSessionHousekeeping()
    {
        if (!Mcp.CanConfigure || _heartbeat is not null) return;

        Sessions.CloseStale();
        Sessions.Heartbeat();

        _heartbeat = new Timer(_ => Sessions.Heartbeat(), null, HeartbeatInterval, HeartbeatInterval);
    }

    /// <summary>Well inside <see cref="AgentSessionStore.AliveWindow"/>, so a running app is never mistaken for a dead one.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
}
