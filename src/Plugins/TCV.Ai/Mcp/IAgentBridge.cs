using TCV.Ai.Agents;

namespace TCV.Ai.Mcp;

/// <summary>
/// What the running application can answer for an agent. Declared here, implemented in
/// <c>TCV.App</c>: everything below needs the live panels, the plugin host's VFS resolution and the
/// operation queue, none of which may be referenced from a plugin (ADR-0012).
/// </summary>
/// <remarks>
/// Every method may throw <see cref="McpToolException"/> to return a structured error. Anything else
/// that escapes is turned into an <see cref="McpError.Internal"/> by the protocol layer, so a bug in
/// the bridge degrades to one failed tool call rather than a dead server.
/// </remarks>
public interface IAgentBridge
{
    /// <summary>
    /// The deixis channel (§ 3.1): what the user means by «these files». Cheap to produce and the
    /// single most useful thing the server offers, because the panel already holds the answer that
    /// today gets hand-copied into a prompt.
    /// </summary>
    Task<AgentPanelContext> GetContextAsync(CancellationToken ct = default);

    /// <summary>Lists a directory through the VFS, so <c>zip://</c> and <c>sftp://</c> work (§ 3.2).</summary>
    Task<IReadOnlyList<AgentEntry>> ListAsync(string path, CancellationToken ct = default);

    /// <summary>Reads a file as text, capped at <paramref name="maxBytes"/>.</summary>
    Task<AgentFileText> ReadAsync(string path, int maxBytes, CancellationToken ct = default);

    /// <summary>Searches by name or content, reusing the index rather than making the agent walk (§ 3.4).</summary>
    Task<IReadOnlyList<AgentSearchHit>> SearchAsync(AgentSearchRequest request, CancellationToken ct = default);

    /// <summary>Recent operation-log lines, so an agent can check what its last write actually did.</summary>
    Task<IReadOnlyList<AgentOperationRecord>> RecentOperationsAsync(int limit, CancellationToken ct = default);

    /// <summary>Puts a mutation through the approval gate and the operation queue (§ 3.3, INV-10).</summary>
    Task<AgentWriteDecision> ProposeWriteAsync(AgentWriteProposal proposal, CancellationToken ct = default);

    /// <summary>
    /// The directories currently approved for agent access. Read per call rather than captured once:
    /// the user may approve or revoke a root while an agent is connected, and a cached scope would
    /// keep honouring a permission that has been withdrawn.
    /// </summary>
    PathScope CurrentScope();

    /// <summary>Whether agent writes are permitted at all — the read-only switch (§ 3.6.2).</summary>
    bool WritesEnabled { get; }
}

/// <summary>
/// The panel context handed to an agent: where both panels are, what is selected, which workspace
/// and branch are active.
/// </summary>
public sealed record AgentPanelContext(
    string ActivePath,
    string OppositePath,
    IReadOnlyList<string> Selection,
    string Workspace,
    string Branch,
    string ActiveScheme,
    bool ActiveIsMounted,
    IReadOnlyList<string> ApprovedRoots,
    bool WritesEnabled);

/// <summary>One directory entry, as an agent needs it.</summary>
public sealed record AgentEntry(
    string Path, string Name, bool IsDirectory, long Size, DateTime ModifiedUtc);

/// <summary>File content plus the honesty flags: was it cut short, and how was it decoded.</summary>
public sealed record AgentFileText(
    string Path, string Text, bool Truncated, long Size, string Encoding);

/// <param name="Semantic">Ask the local embedding index instead of exact matching (6.16).</param>
/// <param name="NamesOnly">Match file names instead of contents.</param>
public sealed record AgentSearchRequest(
    string Query, string Path, bool Semantic, bool NamesOnly, int Limit);

/// <param name="Line">1-based line of a content hit, or 0 for a name hit.</param>
/// <param name="Score">Relevance for semantic hits; 0 for exact ones, which are not ranked.</param>
public sealed record AgentSearchHit(string Path, int Line, string Text, double Score);

/// <summary>One line of the operation log, as an agent needs it.</summary>
public sealed record AgentOperationRecord(
    DateTime AtUtc, string Kind, string Source, string? Destination,
    int ItemCount, string Outcome, string Origin);
