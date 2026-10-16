using TCV.Ai.Agents;
using TCV.Ai.Mcp;
using TCV.Core.Interfaces;

namespace TCV.App.Agents;

/// <summary>What the panels currently show — the raw material of <c>tcv_selection</c>.</summary>
/// <param name="ActiveMount">What the active panel has mounted, or null for a panel on the disk.</param>
/// <param name="OppositeMount">The same for the opposite panel.</param>
public sealed record AgentPanelSnapshot(
    string ActivePath,
    string OppositePath,
    IReadOnlyList<string> Selection,
    string Workspace,
    string Branch,
    string ActiveScheme,
    AgentMountInfo? ActiveMount = null,
    AgentMountInfo? OppositeMount = null)
{
    public bool ActiveIsMounted => ActiveMount is not null;
    public bool OppositeIsMounted => OppositeMount is not null;
}

/// <summary>
/// One panel's mounted file system and where inside it the panel is standing.
/// </summary>
/// <param name="Info">What is mounted, for the access rule (<see cref="PanelMountAccess"/>).</param>
/// <param name="Lease">
/// Holds the mount alive for as long as it is undisposed (INV-09). The panel can be navigated away
/// from or disconnected while an agent's read is in flight, and without this the mount would be
/// disposed under it (TRAP-05). Callers must dispose it when they are done with <paramref name="Vfs"/>.
/// </param>
public sealed record AgentPanelMount(
    IVirtualFileSystem Vfs, string CurrentPath, AgentMountInfo Info, IDisposable Lease);

/// <summary>
/// The window's side of the agent layer, implemented by <c>MainViewModel</c>.
/// <para>
/// It exists so <see cref="AgentBridge"/> can stay a plain service: the four things below all need
/// the live panels or the operation queue, and every one of them has to be marshalled onto the UI
/// thread — the MCP server answers on a background thread, and reading a panel's selection from
/// there is exactly the kind of race that shows up once a month and never in a test.
/// </para>
/// </summary>
public interface IAgentHost
{
    /// <summary>Panel state. Implementations marshal to the UI thread; callers may be anywhere.</summary>
    AgentPanelSnapshot SnapshotPanels();

    /// <summary>
    /// What a panel has open, for resolving a <c>panel://</c> path. The returned mount carries a lease
    /// the caller must dispose.
    /// </summary>
    AgentPanelMount? PanelMount(bool active);

    /// <summary>
    /// Puts a proposal in front of the user. True when they approved it. Cancelling
    /// <paramref name="ct"/> withdraws the prompt — the gate's wait is bounded, and a modal window
    /// nobody closes would block the application for good.
    /// </summary>
    Task<bool> ConfirmAgentWriteAsync(AgentWriteProposal proposal, CancellationToken ct);

    /// <summary>
    /// Runs an approved proposal through <c>OperationQueue</c> (INV-10). Returns null on success, or a
    /// message to hand back to the agent.
    /// </summary>
    Task<string?> RunApprovedWriteAsync(AgentWriteProposal proposal, CancellationToken ct);

    /// <summary>Exact, name or semantic search on the agent's behalf (§ 3.4).</summary>
    Task<IReadOnlyList<AgentSearchHit>> SearchForAgentAsync(
        AgentSearchRequest request, CancellationToken ct);
}
