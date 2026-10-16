namespace TCV.Ai.Agents;

/// <summary>What an agent asked to do to the file system.</summary>
public enum AgentWriteKind
{
    Copy,
    Move,
    Delete,
    CreateDirectory,
}

/// <summary>
/// A file-system mutation an agent proposed but has not been allowed to perform. The whole point of
/// the agent-aware layer is that this object exists at all: an MCP write does not reach the disk
/// directly, it becomes a proposal that a human approves and that then travels through
/// <c>OperationQueue</c> into <c>OperationLog</c> (INV-10, ADR-0012), where it is visible and
/// undoable like any other operation.
/// </summary>
/// <param name="Id">Correlates the proposal with its approval dialog and its log line.</param>
/// <param name="Kind">Which mutation.</param>
/// <param name="Sources">Absolute or VFS paths the operation reads/removes.</param>
/// <param name="Destination">Target directory, or empty for <see cref="AgentWriteKind.Delete"/>.</param>
/// <param name="Agent">Who is asking — the MCP client id, shown in the prompt and stored as provenance.</param>
/// <param name="RequestedUtc">When it was proposed, so a stale prompt can be told apart from a fresh one.</param>
public sealed record AgentWriteProposal(
    string Id,
    AgentWriteKind Kind,
    IReadOnlyList<string> Sources,
    string Destination,
    string Agent,
    DateTime RequestedUtc)
{
    public static AgentWriteProposal Create(
        AgentWriteKind kind,
        IReadOnlyList<string> sources,
        string destination,
        string agent,
        DateTime? requestedUtc = null) =>
        new(Guid.NewGuid().ToString("n"),
            kind,
            sources,
            destination ?? string.Empty,
            string.IsNullOrWhiteSpace(agent) ? "unknown agent" : agent.Trim(),
            requestedUtc ?? DateTime.UtcNow);

    /// <summary>True for the mutations that destroy data and so need the loudest prompt (INV-11).</summary>
    public bool IsDestructive => Kind is AgentWriteKind.Delete or AgentWriteKind.Move;

    /// <summary><see cref="Summary"/> as a property, for data binding.</summary>
    public string SummaryText => Summary();

    /// <summary>One-line summary for the prompt and the log: verb, count, and where it lands.</summary>
    public string Summary() =>
        Kind switch
        {
            AgentWriteKind.Delete => $"delete {Sources.Count} item(s)",
            AgentWriteKind.CreateDirectory => $"create directory {Destination}",
            _ => $"{Kind.ToString().ToLowerInvariant()} {Sources.Count} item(s) → {Destination}",
        };
}

/// <summary>
/// The human's answer. <paramref name="Reason"/> is filled on refusal so the agent gets something
/// actionable back rather than a bare "denied" it will simply retry.
/// </summary>
public sealed record AgentWriteDecision(
    bool Approved, string Reason, AgentDecisionCode Code = AgentDecisionCode.Failed)
{
    public static AgentWriteDecision Approve() => new(true, string.Empty, AgentDecisionCode.Done);

    public static AgentWriteDecision Reject(
        string reason, AgentDecisionCode code = AgentDecisionCode.Failed) => new(false, reason, code);

    /// <summary>Refusal used when nobody answered in time — never a silent approval.</summary>
    public static AgentWriteDecision TimedOut() =>
        new(false, "No answer from the user in time; the write was not performed.", AgentDecisionCode.TimedOut);
}

/// <summary>
/// Why a proposal ended as it did, as a code the UI can localise. <see cref="AgentWriteDecision.Reason"/>
/// stays an English sentence because that is what the agent reads.
/// </summary>
public enum AgentDecisionCode
{
    /// <summary>Approved and carried out.</summary>
    Done,

    UserRefused,
    TimedOut,
    WritesDisabled,
    NoWindow,
    NothingToDo,

    /// <summary>The paths were not plain local-disk paths (archive, panel, remote).</summary>
    LocalOnly,

    /// <summary>Approved, but the operation itself failed or the files were not found.</summary>
    Failed,
}

/// <summary>
/// The gate an agent write has to pass. Implemented by the application (it owns the window that
/// asks and the queue that executes); declared here so the MCP server can depend on the contract
/// without depending on WPF.
/// </summary>
public interface IAgentWriteGate
{
    /// <summary>
    /// Puts the proposal in front of the user and, on approval, performs it through the operation
    /// queue. Returns once the operation has finished or been refused — the agent's call blocks
    /// exactly as long as the human takes, which is the honest thing for the agent to observe.
    /// </summary>
    Task<AgentWriteDecision> SubmitAsync(AgentWriteProposal proposal, CancellationToken ct = default);
}
