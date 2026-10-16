using TCV.Ai.Agents;
using TCV.Ai.Mcp;

namespace TCV.Ai.Tests;

/// <summary>
/// A bridge that records what it was asked and answers from fields. Reference fake for the protocol
/// tests — the point of <see cref="McpProtocol"/> being transport-free is that it can be driven like
/// this, with no window, no socket and no disk.
/// </summary>
public sealed class FakeAgentBridge : IAgentBridge
{
    public AgentPanelContext Context { get; set; } = new(
        @"D:\repo", @"D:\other", [@"D:\repo\a.cs"], "tcv", "main", "file", false, [@"D:\repo"], false);

    public List<AgentEntry> Entries { get; } = new();
    public AgentFileText FileText { get; set; } = new(@"D:\repo\a.cs", "hello", false, 5, "utf-8");
    public List<AgentSearchHit> Hits { get; } = new();
    public List<AgentOperationRecord> Operations { get; } = new();
    public AgentWriteDecision Decision { get; set; } = AgentWriteDecision.Approve();

    public PathScope Scope { get; set; } = new([@"D:\repo"]);
    public bool WritesEnabled { get; set; }

    /// <summary>Set to have the next read throw a structured error, as a real bridge would.</summary>
    public McpError? ReadError { get; set; }

    // What the protocol actually passed down — asserted on in the tests.
    public string? LastListedPath { get; private set; }
    public string? LastReadPath { get; private set; }
    public int LastMaxBytes { get; private set; }
    public AgentSearchRequest? LastSearch { get; private set; }
    public AgentWriteProposal? LastProposal { get; private set; }

    public PathScope CurrentScope() => Scope;

    public Task<AgentPanelContext> GetContextAsync(CancellationToken ct = default) =>
        Task.FromResult(Context);

    public Task<IReadOnlyList<AgentEntry>> ListAsync(string path, CancellationToken ct = default)
    {
        LastListedPath = path;
        return Task.FromResult<IReadOnlyList<AgentEntry>>(Entries);
    }

    public Task<AgentFileText> ReadAsync(string path, int maxBytes, CancellationToken ct = default)
    {
        LastReadPath = path;
        LastMaxBytes = maxBytes;

        if (ReadError is { } error) throw new McpToolException(error);
        return Task.FromResult(FileText);
    }

    public Task<IReadOnlyList<AgentSearchHit>> SearchAsync(
        AgentSearchRequest request, CancellationToken ct = default)
    {
        LastSearch = request;
        return Task.FromResult<IReadOnlyList<AgentSearchHit>>(Hits);
    }

    public Task<IReadOnlyList<AgentOperationRecord>> RecentOperationsAsync(
        int limit, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AgentOperationRecord>>(Operations.Take(limit).ToList());

    public Task<AgentWriteDecision> ProposeWriteAsync(
        AgentWriteProposal proposal, CancellationToken ct = default)
    {
        LastProposal = proposal;
        Interlocked.Increment(ref _proposals);
        return PendingDecision ?? Task.FromResult(Decision);
    }

    private int _proposals;

    /// <summary>How many proposals reached the gate — one per distinct request.</summary>
    public int ProposalCount => Volatile.Read(ref _proposals);

    /// <summary>When set, a proposal waits on this instead of answering — a user who has not decided yet.</summary>
    public Task<AgentWriteDecision>? PendingDecision { get; set; }
}
