using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.Ai.Agents;
using TCV.Ai.Mcp;
using TCV.App.Agents;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The supervisor's refusal paths (6.13b). What matters here is not that approval works — it is that
/// every way of <em>not</em> approving ends with the agent told no <em>and</em> the prompt gone: the
/// dialog is modal, so one left standing after the gate stopped listening blocks the whole window and
/// goes on offering an «Allow» that performs no write.
/// </summary>
public class AgentWriteGateTests
{
    private static AgentWriteProposal Proposal() =>
        AgentWriteProposal.Create(
            AgentWriteKind.Delete, [@"D:\repo\a.cs"], string.Empty, "claude-code");

    private static AgentWriteGate Gate(AppSettings settings, IAgentHost host)
    {
        var gate = new AgentWriteGate(settings, NullLogger<AgentWriteGate>.Instance) { Host = host };
        settings.Agent.McpAllowWrites = true;
        return gate;
    }

    [Fact]
    public async Task SubmitAsync_WithWritesDisabled_RefusesWithoutAskingAnyone()
    {
        var host = new RecordingHost();
        var settings = new AppSettings();
        var gate = Gate(settings, host);
        settings.Agent.McpAllowWrites = false;

        var decision = await gate.SubmitAsync(Proposal());

        decision.Approved.Should().BeFalse();
        host.Asked.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitAsync_WhenTheWaitIsCut_TakesThePromptDownWithIt()
    {
        // The gate's own timeout works the same way; cancelling from outside gets there in a test's
        // worth of time rather than the ten-second floor on ApprovalTimeoutSeconds.
        var host = new RecordingHost();
        var gate = Gate(new AppSettings(), host);
        using var caller = new CancellationTokenSource();

        var pending = gate.SubmitAsync(Proposal(), caller.Token);
        await host.Asking.Task;
        caller.Cancel();

        var act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The token handed to the host is what withdraws the dialog. Without it the modal window
        // would still be up with nobody left to answer.
        host.Withdrawn.Should().BeTrue();
        gate.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task SubmitAsync_WithNothingToActOn_IsRefusedRatherThanShown()
    {
        var host = new RecordingHost();
        var gate = Gate(new AppSettings(), host);

        var decision = await gate.SubmitAsync(
            AgentWriteProposal.Create(AgentWriteKind.Delete, [], string.Empty, "claude-code"));

        decision.Approved.Should().BeFalse();
        host.Asked.Should().BeFalse();
        gate.Recent.Should().ContainSingle();
    }

    /// <summary>A host that never answers, so the gate's giving-up paths are the ones under test.</summary>
    private sealed class RecordingHost : IAgentHost
    {
        public bool Asked { get; private set; }
        public bool Withdrawn { get; private set; }

        /// <summary>Completes once the prompt is "on screen", so the test does not cancel too early.</summary>
        public TaskCompletionSource Asking { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ConfirmAgentWriteAsync(AgentWriteProposal proposal, CancellationToken ct)
        {
            Asked = true;

            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() =>
            {
                Withdrawn = true;
                answer.TrySetResult(false);
            });

            Asking.TrySetResult();
            return answer.Task;
        }

        public AgentPanelSnapshot SnapshotPanels() =>
            new(@"D:\repo", @"D:\other", [], string.Empty, string.Empty, "file");

        public AgentPanelMount? PanelMount(bool active) => null;

        public Task<string?> RunApprovedWriteAsync(AgentWriteProposal proposal, CancellationToken ct) =>
            Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AgentSearchHit>> SearchForAgentAsync(
            AgentSearchRequest request, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AgentSearchHit>>([]);
    }
}

/// <summary>
/// The prompt itself. It is the last thing between an agent and the file system, so a second decision
/// must not reach a window that is already closing, and a withdrawal must be indistinguishable from
/// «Refuse» as far as the answer goes.
/// </summary>
public class AgentApprovalDialogViewModelTests
{
    private static AgentApprovalDialogViewModel Model(AgentWriteKind kind = AgentWriteKind.Delete) =>
        new(AgentWriteProposal.Create(kind, [@"D:\repo\a.cs"], @"D:\repo\out", "claude-code"));

    [Fact]
    public void Withdraw_ClosesThePromptAsARefusal()
    {
        var vm = Model();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.Withdraw();

        closed.Should().Be(1);
        vm.Withdrawn.Should().BeTrue();
        vm.Approved.Should().BeFalse();
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void Approve_AfterAWithdrawal_ChangesNothing()
    {
        // The window is already gone by then; approving into it would report an approval for a write
        // the gate has long since refused.
        var vm = Model();
        vm.Withdraw();

        vm.ApproveCommand.Execute(null);

        vm.Approved.Should().BeFalse();
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void Withdraw_AfterTheUserAnswered_LeavesTheAnswerAlone()
    {
        var vm = Model();
        vm.ApproveCommand.Execute(null);

        vm.Withdraw();

        vm.Approved.Should().BeTrue();
        vm.Withdrawn.Should().BeFalse();
    }

    [Fact]
    public void Headline_NamesTheActionForEveryKind()
    {
        foreach (var kind in Enum.GetValues<AgentWriteKind>())
            Model(kind).Headline.Should().NotBeNullOrWhiteSpace();
    }
}
