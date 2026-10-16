using System.IO;
using FluentAssertions;
using TCV.App.Navigation;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// ROADMAP 6.53 — «run an agent on a task»: it now preselects the configured default agent (and
/// says when it cannot), follows the «a worktree creates a workspace» setting, and refuses a branch
/// another checkout already has before git does.
/// </summary>
public class AgentLaunchDialogViewModelTests
{
    private const string Current = @"D:\projects\thing";
    private const string WorktreeRoot = @"D:\projects\thing-worktrees";

    private static readonly ExternalTool Claude = new() { Name = "Claude Code", Command = "claude", InTerminal = true };
    private static readonly ExternalTool Codex = new() { Name = "Codex", Command = "codex", InTerminal = true };

    private static AgentLaunchDialogViewModel Create(
        ExternalToolChoice? choice = null,
        IReadOnlyDictionary<string, string>? held = null,
        bool createWorkspace = true,
        bool insideRepository = true,
        string[]? branches = null) =>
        new([Claude, Codex],
            choice ?? new ExternalToolChoice(Claude, ExternalToolChoiceKind.FirstDetected, string.Empty),
            Current,
            insideRepository ? WorktreeRoot : string.Empty,
            branches ?? [],
            held ?? new Dictionary<string, string>(),
            setupCommand: string.Empty,
            createWorkspaceByDefault: createWorkspace,
            insideRepository: insideRepository);

    [Fact]
    public void Constructor_ConfiguredDefault_IsPreselectedEvenWhenNotFirst()
    {
        var vm = Create(new ExternalToolChoice(Codex, ExternalToolChoiceKind.Default, "Codex"));

        vm.SelectedAgent.Should().BeSameAs(Codex);
        vm.HasAgentNotice.Should().BeFalse();
    }

    [Fact]
    public void Constructor_DefaultUnavailable_PreselectsTheStandInAndSaysSo()
    {
        var vm = Create(new ExternalToolChoice(Codex, ExternalToolChoiceKind.DefaultUnavailable, "Gemini CLI"));

        vm.SelectedAgent.Should().BeSameAs(Codex);
        vm.HasAgentNotice.Should().BeTrue();
        vm.AgentNotice.Should().Contain("AgentLaunch_DefaultUnavailable");
    }

    [Fact]
    public void Constructor_NoDefaultConfigured_SaysNothing()
    {
        Create().HasAgentNotice.Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateWorkspace_StartsFromTheSetting(bool setting)
    {
        Create(createWorkspace: setting).CreateWorkspace.Should().Be(setting);
    }

    [Fact]
    public void Confirm_WorkspaceTurnedOff_IsCarriedIntoTheResult()
    {
        var vm = Create(createWorkspace: true);
        vm.Task = "Fix login redirect";
        vm.CreateWorkspace = false;

        vm.ConfirmCommand.Execute(null);

        vm.Result!.CreateWorktree.Should().BeTrue();
        vm.Result.CreateWorkspace.Should().BeFalse();
    }

    [Fact]
    public void Confirm_RunningInPlace_NeverCreatesAWorkspace()
    {
        var vm = Create(createWorkspace: true);
        vm.Task = "Explain the build";
        vm.CreateWorktree = false;

        vm.ConfirmCommand.Execute(null);

        vm.Result!.CreateWorkspace.Should().BeFalse();
        vm.Result.TargetPath.Should().Be(Current);
    }

    [Fact]
    public void BranchHeldByAnotherCheckout_BlocksStartAndNamesTheCheckout()
    {
        var held = new Dictionary<string, string> { ["fix-login"] = @"D:\projects\thing-worktrees\fix-login" };
        var vm = Create(held: held, branches: ["fix-login"]);
        vm.Task = "Fix login";

        vm.Branch.Should().Be("fix-login");
        vm.BranchHolder.Should().Be(@"D:\projects\thing-worktrees\fix-login");
        vm.CanConfirm.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("AgentLaunch_BranchHeld");
    }

    [Fact]
    public void BranchHeld_DoesNotMatterWhenRunningInPlace()
    {
        var held = new Dictionary<string, string> { ["fix-login"] = Current };
        var vm = Create(held: held, branches: ["fix-login"]);
        vm.Task = "Fix login";

        vm.CreateWorktree = false;

        vm.CanConfirm.Should().BeTrue();
        vm.ValidationMessage.Should().BeEmpty();
    }

    [Fact]
    public void FreeBranch_CanStart()
    {
        var held = new Dictionary<string, string> { ["main"] = Current };
        var vm = Create(held: held, branches: ["main"]);

        vm.Task = "Fix login";

        vm.BranchHolder.Should().BeNull();
        vm.CanConfirm.Should().BeTrue(vm.ValidationMessage);
        vm.TargetPath.Should().Be(Path.Combine(WorktreeRoot, "fix-login"));
    }

    // ── The «folder not empty» check runs off the getters (6.62, INV-06) ─────────

    [Fact]
    public async Task TargetPath_FolderWithContent_BlocksStartOnceTheProbeAnswers()
    {
        var target = Path.Combine(WorktreeRoot, "fix-login");
        var vm = CreateProbing(path => path == target);

        vm.Task = "Fix login";
        await vm.TargetProbe;

        vm.TargetPath.Should().Be(target);
        vm.CanConfirm.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_WtPathNotEmpty");
    }

    [Fact]
    public async Task CanConfirmAndValidationMessage_AreReadOften_ButNeverTouchTheDisk()
    {
        var probes = 0;
        var vm = CreateProbing(_ =>
        {
            Interlocked.Increment(ref probes);
            return false;
        });
        vm.Task = "Fix login";
        await vm.TargetProbe;
        var afterTyping = Volatile.Read(ref probes);

        for (var i = 0; i < 50; i++)
        {
            _ = vm.CanConfirm;
            _ = vm.ValidationMessage;
        }

        Volatile.Read(ref probes).Should().Be(afterTyping, "the bindings read these on every keystroke");
        vm.CanConfirm.Should().BeTrue(vm.ValidationMessage);
    }

    [Fact]
    public async Task TargetPath_ChangedWhileTheDiskIsSlow_OnlyTheLatestAnswerCounts()
    {
        var first = Path.Combine(WorktreeRoot, "fix-login");
        var second = Path.Combine(WorktreeRoot, "other");
        using var firstAnswer = new ManualResetEventSlim();
        var vm = CreateProbing(path =>
        {
            if (path == first)
            {
                firstAnswer.Wait(TimeSpan.FromSeconds(10));
                return true;   // full, but by now nobody asks about it any more
            }

            return false;
        });

        vm.Task = "Fix login";
        var slow = vm.TargetProbe;
        vm.TargetPath = second;
        await vm.TargetProbe;
        firstAnswer.Set();
        await slow;

        vm.CanConfirm.Should().BeTrue(vm.ValidationMessage);
        vm.ValidationMessage.Should().BeEmpty();
    }

    [Fact]
    public async Task TargetPath_NewPathNotProbedYet_DoesNotInheritTheOldVerdict()
    {
        var full = Path.Combine(WorktreeRoot, "fix-login");
        var other = Path.Combine(WorktreeRoot, "other");
        using var otherAnswer = new ManualResetEventSlim();
        var vm = CreateProbing(path =>
        {
            if (path == other)
            {
                otherAnswer.Wait(TimeSpan.FromSeconds(10));
            }

            return path == full;
        });
        vm.Task = "Fix login";
        await vm.TargetProbe;
        vm.CanConfirm.Should().BeFalse();

        vm.TargetPath = other;

        // The message must not name the new folder with the old folder's verdict.
        vm.ValidationMessage.Should().NotContain("Git_WtPathNotEmpty");
        otherAnswer.Set();
        await vm.TargetProbe;
        vm.CanConfirm.Should().BeTrue(vm.ValidationMessage);
    }

    private static AgentLaunchDialogViewModel CreateProbing(Func<string, bool> folderHasContent) =>
        new([Claude, Codex],
            new ExternalToolChoice(Claude, ExternalToolChoiceKind.FirstDetected, string.Empty),
            Current,
            WorktreeRoot,
            [],
            new Dictionary<string, string>(),
            setupCommand: string.Empty,
            createWorkspaceByDefault: true,
            insideRepository: true,
            folderHasContent: folderHasContent);

    [Fact]
    public void HeldBranches_MapsTheCurrentBranchToThisCheckoutAndTheRestToTheirOwners()
    {
        var held = AgentLaunchDialogViewModel.HeldBranches(
            [
                new GitBranchInfo("main", IsCurrent: true, CheckedOutAt: null),
                new GitBranchInfo("feature", IsCurrent: false, CheckedOutAt: @"D:\wt\feature"),
                new GitBranchInfo("idle", IsCurrent: false, CheckedOutAt: null),
            ],
            Current);

        held.Should().HaveCount(2);
        held["main"].Should().Be(Current);
        held["feature"].Should().Be(@"D:\wt\feature");
    }
}
