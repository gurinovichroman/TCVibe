using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using TCV.Ai.Agents;
using TCV.App.Settings;
using TCV.Core.Interfaces;

namespace TCV.App.Agents;

/// <summary>A decided proposal, kept so the dashboard can show what agents have been asking for.</summary>
public sealed record AgentWriteRecord(
    AgentWriteProposal Proposal,
    bool Approved,
    string Outcome,
    DateTime DecidedUtc,
    AgentDecisionCode Code = AgentDecisionCode.Failed)
{
    public string Summary => Proposal.Summary();
    public string Agent => Proposal.Agent;

    /// <summary>English word kept for logs and tests; the dashboard shows <see cref="VerdictText"/>.</summary>
    public string Verdict => Approved ? "approved" : "refused";

    public string VerdictText => Localization.LocalizationManager.Get(
        Approved ? "Agent_VerdictApproved" : "Agent_VerdictRefused");

    /// <summary>
    /// The outcome in the user's language. The agent-facing sentence (<see cref="Outcome"/>) stays
    /// English; only an operation's own failure message is shown as it came.
    /// </summary>
    public string OutcomeText => Code switch
    {
        AgentDecisionCode.Done => Localization.LocalizationManager.Get("Agent_OutcomeDone"),
        AgentDecisionCode.UserRefused => Localization.LocalizationManager.Get("Agent_OutcomeUserRefused"),
        AgentDecisionCode.TimedOut => Localization.LocalizationManager.Get("Agent_OutcomeTimedOut"),
        AgentDecisionCode.WritesDisabled => Localization.LocalizationManager.Get("Agent_OutcomeWritesDisabled"),
        AgentDecisionCode.NoWindow => Localization.LocalizationManager.Get("Agent_OutcomeNoWindow"),
        AgentDecisionCode.NothingToDo => Localization.LocalizationManager.Get("Agent_OutcomeNothingToDo"),
        AgentDecisionCode.LocalOnly => Localization.LocalizationManager.Get("Agent_OutcomeLocalOnly"),
        // The detail (a path, the operation's own error) is data and stays as it came; the frame around
        // it is what was English.
        _ => string.Format(Localization.LocalizationManager.Get("Agent_OutcomeFailed"), Outcome),
    };

    /// <summary>Local time as text — the dashboard shows local times everywhere.</summary>
    public string DecidedText => DecidedUtc.ToLocalTime().ToString("g");
}

/// <summary>
/// The supervisor (6.13b): the one place an agent-initiated mutation can become a real file
/// operation, and only after a human says yes.
/// <para>
/// This is the third option (ADR-0012). Existing filesystem MCP servers offer a
/// binary choice — grant everything up front, or confirm every call inside a chat window. Here the
/// proposal lands in the file manager the user is already looking at, gets approved once with its
/// targets spelled out, and then runs through <c>OperationQueue</c> like any other operation, which
/// means it appears in the operation log and is undoable (INV-10, INV-11).
/// </para>
/// </summary>
public sealed class AgentWriteGate : IAgentWriteGate
{
    /// <summary>Decided proposals kept in memory for the dashboard.</summary>
    public const int MaxRecent = 100;

    private readonly AppSettings _settings;
    private readonly ILogger<AgentWriteGate> _logger;
    private readonly object _gate = new();

    public AgentWriteGate(AppSettings settings, ILogger<AgentWriteGate> logger)
    {
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Set by <c>MainViewModel</c>; null before the window exists.</summary>
    public IAgentHost? Host { get; set; }

    /// <summary>Proposals awaiting an answer. Bound by the dashboard, mutated on the UI thread.</summary>
    public ObservableCollection<AgentWriteProposal> Pending { get; } = new();

    /// <summary>Decided proposals, newest first.</summary>
    public ObservableCollection<AgentWriteRecord> Recent { get; } = new();

    /// <summary>Raised whenever <see cref="Pending"/> or <see cref="Recent"/> changed.</summary>
    public event EventHandler? Changed;

    public async Task<AgentWriteDecision> SubmitAsync(
        AgentWriteProposal proposal, CancellationToken ct = default)
    {
        if (Host is not { } host)
            return Finish(proposal, AgentWriteDecision.Reject(
                "The main window is not available.", AgentDecisionCode.NoWindow));

        if (!_settings.Agent.McpAllowWrites)
            return Finish(proposal, AgentWriteDecision.Reject(
                "Agent writes are disabled in settings.", AgentDecisionCode.WritesDisabled));

        if (proposal.Sources.Count == 0)
            return Finish(proposal, AgentWriteDecision.Reject(
                "The proposal names nothing to act on.", AgentDecisionCode.NothingToDo));

        // Refused before anyone is asked. Supervised writes run on the local disk only, and the check
        // used to sit after the approval — so the user was shown a prompt, said yes, and only then was
        // the agent told it could not be done.
        if (!IsLocalDiskOnly(proposal))
            return Finish(proposal, AgentWriteDecision.Reject(LocalOnlyReason, AgentDecisionCode.LocalOnly));

        Track(proposal);
        try
        {
            bool approved;
            try
            {
                // A bounded wait, because the agent is blocked on this call. Timing out refuses —
                // an unattended machine must not become an approving one. The token goes to the host
                // as well as bounding our wait: it is what takes the prompt down, and a modal window
                // left standing after we stopped listening would block the application outright.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(EffectiveTimeout(_settings));

                approved = await host
                    .ConfirmAgentWriteAsync(proposal, timeout.Token)
                    .WaitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogInformation("Agent proposal {Id} timed out unanswered", proposal.Id);
                return Finish(proposal, AgentWriteDecision.TimedOut());
            }

            if (!approved)
            {
                _logger.LogInformation("User refused agent proposal: {Summary}", proposal.Summary());
                return Finish(proposal, AgentWriteDecision.Reject(
                    "The user refused the operation.", AgentDecisionCode.UserRefused));
            }

            // Approval can be minutes old by now, and the switch may have been turned off or a
            // directory revoked in between. The permission that counts is the one held at execution.
            if (!_settings.Agent.McpAllowWrites)
                return Finish(proposal, AgentWriteDecision.Reject(
                    "Agent writes were disabled while the prompt was open.", AgentDecisionCode.WritesDisabled));

            var error = await host.RunApprovedWriteAsync(proposal, ct);
            return Finish(proposal, error is null
                ? AgentWriteDecision.Approve()
                : AgentWriteDecision.Reject(error, error == LocalOnlyReason
                    ? AgentDecisionCode.LocalOnly
                    : AgentDecisionCode.Failed));
        }
        finally
        {
            Untrack(proposal);
        }
    }

    /// <summary>What the agent is told when a proposal names something that is not a plain disk path.</summary>
    public const string LocalOnlyReason = "Supervised writes work on local disk paths only.";

    /// <summary>
    /// The result of an approved write as the agent hears it: null when everything was done, otherwise
    /// why not. An operation that skipped items (a junction not followed, TRAP-46) still ends
    /// <see cref="OperationState.Completed"/>, and reporting that as done told the agent its move had
    /// happened while part of the source was still where it was.
    /// </summary>
    public static string? DescribeOutcome(
        OperationState state, int skippedCount, IReadOnlyCollection<string> warnings, string? failure)
    {
        if (state != OperationState.Completed)
            return failure ?? "The operation did not complete.";

        if (skippedCount <= 0) return null;

        var detail = warnings.Count > 0 ? " (" + string.Join("; ", warnings) + ")" : string.Empty;
        return $"Only partly done: {skippedCount} item(s) were skipped{detail}.";
    }

    /// <summary>
    /// True when every path of the proposal is an ordinary local path. An archive (<c>zip://</c>), a
    /// panel (<c>panel://</c>) or a remote location would need its mount held open for the whole
    /// operation (INV-09), and refusing plainly beats a transfer that fails halfway.
    /// </summary>
    public static bool IsLocalDiskOnly(AgentWriteProposal proposal) =>
        proposal.Sources.Concat([proposal.Destination])
            .All(path => path.Length == 0 || !path.Contains("://", StringComparison.Ordinal));

    /// <summary>
    /// How long a prompt waits before it counts as refused. One definition for the gate's wait and
    /// for the countdown the dialog shows, so the two cannot drift apart.
    /// </summary>
    public static TimeSpan EffectiveTimeout(AppSettings settings) =>
        TimeSpan.FromSeconds(Math.Clamp(settings.Agent.ApprovalTimeoutSeconds, 10, 3600));

    /// <summary>Clears the decided list — the dashboard's "clear" action.</summary>
    public void ClearHistory()
    {
        OnUi(() => Recent.Clear());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private AgentWriteDecision Finish(AgentWriteProposal proposal, AgentWriteDecision decision)
    {
        var record = new AgentWriteRecord(
            proposal, decision.Approved,
            decision.Approved ? "done" : decision.Reason,
            DateTime.UtcNow,
            decision.Code);

        OnUi(() =>
        {
            Recent.Insert(0, record);
            while (Recent.Count > MaxRecent) Recent.RemoveAt(Recent.Count - 1);
        });

        Changed?.Invoke(this, EventArgs.Empty);
        return decision;
    }

    private void Track(AgentWriteProposal proposal)
    {
        lock (_gate) OnUi(() => Pending.Add(proposal));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Untrack(AgentWriteProposal proposal)
    {
        lock (_gate)
        {
            OnUi(() =>
            {
                var existing = Pending.FirstOrDefault(p => p.Id == proposal.Id);
                if (existing is not null) Pending.Remove(existing);
            });
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs a collection mutation on the UI thread. The gate is driven from the MCP server's
    /// background thread, and these collections are bound to a window.
    /// </summary>
    private static void OnUi(Action action)
    {
        var application = System.Windows.Application.Current;
        if (application is null) { action(); return; }   // unit tests: no dispatcher, no window

        if (application.Dispatcher.CheckAccess()) action();
        else application.Dispatcher.Invoke(action);
    }
}
