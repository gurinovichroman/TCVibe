using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.Ai.Agents;
using TCV.Ai.Mcp;
using TCV.App.Agents;
using TCV.App.FileSystem;
using TCV.App.Navigation;
using TCV.App.Operations;
using TCV.App.Settings;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The agent layer's window-side guards: what the gate refuses before it ever asks, which panels
/// <c>panel://</c> may address, the copy kept before a revert, the batch rename that can be undone, and
/// the indexer's refusal to follow links.
/// </summary>
public class AgentHardeningTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "tcv-agent-hard-" + Guid.NewGuid().ToString("n")[..8]);

    public AgentHardeningTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* best effort */ }
    }

    // ── The gate: refuse before asking ────────────────────────────────────────

    private sealed class Host(bool answer) : IAgentHost
    {
        public bool Asked { get; private set; }
        public bool Ran { get; private set; }

        /// <summary>Runs while the «prompt» is open — the user flips a setting before answering.</summary>
        public Action? WhilePrompted { get; init; }

        public Task<bool> ConfirmAgentWriteAsync(AgentWriteProposal proposal, CancellationToken ct)
        {
            Asked = true;
            WhilePrompted?.Invoke();
            return Task.FromResult(answer);
        }

        public Task<string?> RunApprovedWriteAsync(AgentWriteProposal proposal, CancellationToken ct)
        {
            Ran = true;
            return Task.FromResult<string?>(null);
        }

        public AgentPanelSnapshot SnapshotPanels() => new(@"D:\a", @"D:\b", [], "", "", "file");
        public AgentPanelMount? PanelMount(bool active) => null;

        public Task<IReadOnlyList<AgentSearchHit>> SearchForAgentAsync(
            AgentSearchRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentSearchHit>>([]);
    }

    private static AgentWriteGate Gate(AppSettings settings, Host host)
    {
        settings.Agent.McpAllowWrites = true;
        return new AgentWriteGate(settings, NullLogger<AgentWriteGate>.Instance) { Host = host };
    }

    [Theory]
    [InlineData(@"zip://D:\a.zip!/x.txt", @"D:\out")]
    [InlineData(@"D:\a.txt", "panel://opposite/pub")]
    [InlineData("panel://active/logs/app.log", @"D:\out")]
    public async Task SubmitAsync_PathThatIsNotOnTheLocalDisk_IsRefusedWithoutShowingAPrompt(
        string source, string destination)
    {
        var host = new Host(answer: true);
        var gate = Gate(new AppSettings(), host);

        var decision = await gate.SubmitAsync(
            AgentWriteProposal.Create(AgentWriteKind.Copy, [source], destination, "claude-code"));

        decision.Approved.Should().BeFalse();
        decision.Code.Should().Be(AgentDecisionCode.LocalOnly);
        host.Asked.Should().BeFalse();
        gate.Recent.Single().Code.Should().Be(AgentDecisionCode.LocalOnly);
    }

    [Fact]
    public async Task SubmitAsync_WritesSwitchedOffWhileThePromptWasOpen_DoesNotRunTheApprovedWrite()
    {
        var settings = new AppSettings();
        var host = new Host(answer: true) { WhilePrompted = () => settings.Agent.McpAllowWrites = false };
        var gate = Gate(settings, host);

        var decision = await gate.SubmitAsync(
            AgentWriteProposal.Create(AgentWriteKind.Delete, [@"D:\repo\a.cs"], "", "claude-code"));

        decision.Approved.Should().BeFalse();
        decision.Code.Should().Be(AgentDecisionCode.WritesDisabled);
        host.Ran.Should().BeFalse();
    }

    [Fact]
    public async Task SubmitAsync_UserRefuses_IsRecordedAsRefusedByTheUser()
    {
        var host = new Host(answer: false);
        var gate = Gate(new AppSettings(), host);

        var decision = await gate.SubmitAsync(
            AgentWriteProposal.Create(AgentWriteKind.Delete, [@"D:\repo\a.cs"], "", "claude-code"));

        decision.Code.Should().Be(AgentDecisionCode.UserRefused);
        host.Ran.Should().BeFalse();
    }

    [Theory]
    [InlineData(0, 10)]        // clamped up: a prompt that vanishes in a second cannot be answered
    [InlineData(300, 300)]
    [InlineData(999_999, 3600)]
    public void EffectiveTimeout_IsClampedTheSameWayForTheGateAndTheCountdown(int configured, int expected)
    {
        var settings = new AppSettings();
        settings.Agent.ApprovalTimeoutSeconds = configured;

        AgentWriteGate.EffectiveTimeout(settings).Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public void ApprovalDialog_ShowsTheCountdownAndWhatADeleteWillDo()
    {
        var proposal = AgentWriteProposal.Create(AgentWriteKind.Delete, [@"D:\a.txt"], "", "claude-code");

        var permanent = new TCV.App.ViewModels.Dialogs.AgentApprovalDialogViewModel(
            proposal, TimeSpan.FromMinutes(5), toRecycleBin: false);

        permanent.HasCountdown.Should().BeTrue();
        permanent.HasConsequence.Should().BeTrue();
        permanent.ConsequenceIsSevere.Should().BeTrue();

        var recycled = new TCV.App.ViewModels.Dialogs.AgentApprovalDialogViewModel(
            proposal, null, toRecycleBin: true);
        recycled.HasCountdown.Should().BeFalse();
        recycled.ConsequenceIsSevere.Should().BeFalse();
    }

    // ── panel:// ──────────────────────────────────────────────────────────────

    [Fact]
    public void PanelTargetPath_OnAnArchiveMount_IsAbsoluteInsideTheMount()
    {
        // The panel stands in /logs; the path the server printed (/logs/app.log) must mean itself.
        var reference = AgentPathRef.Parse("panel://active/logs/app.log");

        AgentBridge.PanelTargetPath(reference, new InMemoryFileSystem("zip")).Should().Be("/logs/app.log");
    }

    [Fact]
    public void PanelTargetPath_CannotClimbAboveTheMount()
    {
        var reference = AgentPathRef.Parse("panel://active/../../Users/me/.ssh/id_rsa");

        AgentBridge.PanelTargetPath(reference, new InMemoryFileSystem("sftp"))
            .Should().Be("/Users/me/.ssh/id_rsa");
    }

    [Fact]
    public void PanelTargetPath_OnASearchResultsListing_IsRefused()
    {
        // The listing forwards every call to the local disk: nothing on that path is scope-checked.
        var results = new SearchResultsFileSystem(new InMemoryFileSystem("file"), []);

        var act = () => AgentBridge.PanelTargetPath(AgentPathRef.Parse("panel://active/Users/me"), results);

        act.Should().Throw<McpToolException>().Which.Error.Code.Should().Be(McpError.Unsupported);
    }

    [Fact]
    public void PanelTargetPath_OnTheLocalDisk_IsRefused()
    {
        using var local = new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance);

        var act = () => AgentBridge.PanelTargetPath(AgentPathRef.Parse("panel://active/Users"), local);

        act.Should().Throw<McpToolException>();
    }

    // ── panel:// is not a way around the approved directories (6.34, ADR-0023) ──

    [Fact]
    public void PanelMountAccess_ArchiveInsideAnApprovedDirectory_IsReadable()
    {
        var approved = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        var mount = new AgentMountInfo(AgentMountKind.Archive, Path.Combine(approved, "dist.zip"));

        PanelMountAccess.Check(mount, new PathScope([approved]), remoteAllowed: false, "active").Should().BeNull();
    }

    [Fact]
    public void PanelMountAccess_ArchiveOutsideTheApprovedDirectories_IsRefusedWithoutNamingIt()
    {
        var approved = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_temp, "taxes")).FullName;
        var archive = Path.Combine(elsewhere, "2025.zip");

        var refusal = PanelMountAccess.Check(
            new AgentMountInfo(AgentMountKind.Archive, archive), new PathScope([approved]), remoteAllowed: true, "active");

        refusal!.Code.Should().Be(McpError.OutsideScope);
        refusal.Message.Should().NotContain("2025.zip").And.NotContain(elsewhere);
    }

    [Fact]
    public void PanelMountAccess_ArchiveWithNothingApproved_IsRefusedAsNoScope()
    {
        var mount = new AgentMountInfo(AgentMountKind.Archive, Path.Combine(_temp, "dist.zip"));

        PanelMountAccess.Check(mount, new PathScope([]), remoteAllowed: true, "active")!
            .Code.Should().Be(McpError.NoScope);
    }

    [Fact]
    public void PanelMountAccess_ArchiveWithoutAFileOnDisk_IsRefused()
    {
        // An archive opened from inside another one has no file the scope could be checked against.
        var approved = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;

        PanelMountAccess.Check(new AgentMountInfo(AgentMountKind.Archive), new PathScope([approved]), true, "active")
            .Should().NotBeNull();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void PanelMountAccess_ServerConnection_FollowsTheSettingAlone(bool remoteAllowed, bool readable)
    {
        // A server has no disk path; approving a directory says nothing about it, in either direction.
        var approved = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        var mount = new AgentMountInfo(AgentMountKind.Remote);

        (PanelMountAccess.Check(mount, new PathScope([approved]), remoteAllowed, "opposite") is null).Should().Be(readable);
        (PanelMountAccess.Check(mount, new PathScope([]), remoteAllowed, "opposite") is null).Should().Be(readable);
    }

    /// <summary>A window whose active panel has <paramref name="mount"/> open, served by an in-memory file system.</summary>
    private sealed class MountHost(AgentMountInfo mount, IVirtualFileSystem vfs) : IAgentHost
    {
        public bool LeaseReleased { get; private set; }

        public AgentPanelSnapshot SnapshotPanels() =>
            new("/logs", @"D:\b", ["/logs/app.log"], "", "main", vfs.Scheme, mount);

        public AgentPanelMount? PanelMount(bool active) =>
            active ? new AgentPanelMount(vfs, "/logs", mount, new Lease(() => LeaseReleased = true)) : null;

        public Task<bool> ConfirmAgentWriteAsync(AgentWriteProposal proposal, CancellationToken ct) => Task.FromResult(false);
        public Task<string?> RunApprovedWriteAsync(AgentWriteProposal proposal, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<IReadOnlyList<AgentSearchHit>> SearchForAgentAsync(
            AgentSearchRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<AgentSearchHit>>([]);

        private sealed class Lease(Action released) : IDisposable
        {
            public void Dispose() => released();
        }
    }

    private AgentBridge Bridge(AppSettings settings, IAgentHost host)
    {
        var localFs = new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance);
        var plugins = new PluginHost.PluginHost(
            NullLogger<PluginHost.PluginHost>.Instance,
            new PluginHost.PluginLoader(NullLogger<PluginHost.PluginLoader>.Instance),
            localFs);

        return new AgentBridge(
            localFs, plugins, new Services.OperationLog(Path.Combine(_temp, "log.jsonl")), settings,
            Mock.Of<IAgentWriteGate>(), NullLogger<AgentBridge>.Instance) { Host = host };
    }

    private static InMemoryFileSystem Server()
    {
        var vfs = new InMemoryFileSystem("sftp");
        vfs.AddDirectory("/logs");
        vfs.AddFile("/logs/app.log", "hello"u8.ToArray());
        return vfs;
    }

    [Fact]
    public async Task ListAsync_ServerInAPanelWithTheSettingOff_IsRefusedAndTheMountReleased()
    {
        var host = new MountHost(new AgentMountInfo(AgentMountKind.Remote), Server());
        var bridge = Bridge(new AppSettings(), host);

        var act = () => bridge.ListAsync("panel://active/logs");

        (await act.Should().ThrowAsync<McpToolException>()).Which.Error.Code.Should().Be(McpError.OutsideScope);
        host.LeaseReleased.Should().BeTrue();
    }

    [Fact]
    public async Task ListAsync_ServerInAPanelWithTheSettingOn_ListsIt()
    {
        var settings = new AppSettings();
        settings.Agent.McpAllowRemotePanels = true;
        var bridge = Bridge(settings, new MountHost(new AgentMountInfo(AgentMountKind.Remote), Server()));

        var entries = await bridge.ListAsync("panel://active/logs");

        entries.Select(e => e.Path).Should().Equal("panel://active/logs/app.log");
    }

    [Fact]
    public async Task GetContextAsync_MountTheAgentMayNotRead_DisclosesNeitherItsPathNorItsSelection()
    {
        var bridge = Bridge(new AppSettings(), new MountHost(new AgentMountInfo(AgentMountKind.Remote), Server()));

        var context = await bridge.GetContextAsync();

        context.ActivePath.Should().BeEmpty();
        context.Selection.Should().BeEmpty();
        context.Branch.Should().BeEmpty();
        context.ActiveIsMounted.Should().BeTrue();   // that there is a mount is how the agent knows to ask
    }

    [Fact]
    public async Task GetContextAsync_ArchiveInsideAnApprovedDirectory_IsDisclosedInPanelForm()
    {
        var approved = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        var settings = new AppSettings();
        settings.Agent.ApprovedRoots.Add(approved);
        var mount = new AgentMountInfo(AgentMountKind.Archive, Path.Combine(approved, "dist.zip"));
        var bridge = Bridge(settings, new MountHost(mount, new InMemoryFileSystem("zip")));

        var context = await bridge.GetContextAsync();

        context.ActivePath.Should().Be("panel://active/logs");
        context.Selection.Should().Equal("panel://active/logs/app.log");
    }

    // ── Revert keeps a copy ───────────────────────────────────────────────────

    [Fact]
    public void RevertBackup_Save_CopiesTheFileUnderTheApplicationFolder()
    {
        var file = Path.Combine(_temp, "src", "a.cs");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "my uncommitted work");

        var folder = RevertBackup.Save(_temp, [("src/a.cs", file)], new DateTime(2026, 1, 2, 3, 4, 5));

        folder.Should().NotBeNull();
        folder.Should().StartWith(Path.Combine(_temp, RevertBackup.FolderName));
        File.ReadAllText(Path.Combine(folder!, "src", "a.cs")).Should().Be("my uncommitted work");
    }

    [Fact]
    public void RevertBackup_Save_OfADeletedFile_KeepsNothingAndSaysSo()
    {
        RevertBackup.Save(_temp, [("gone.cs", Path.Combine(_temp, "gone.cs"))], DateTime.Now)
            .Should().BeNull();
    }

    [Fact]
    public void RevertBackup_Save_RefusesARelativePathThatClimbsOut()
    {
        var file = Path.Combine(_temp, "a.cs");
        File.WriteAllText(file, "x");

        var folder = RevertBackup.Save(_temp, [(@"..\..\escaped.cs", file)], DateTime.Now);

        folder.Should().BeNull();
        File.Exists(Path.Combine(_temp, "escaped.cs")).Should().BeFalse();
    }

    // ── Batch rename ──────────────────────────────────────────────────────────

    private static InMemoryFileSystem Files(params (string Name, string Content)[] files)
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/dl");
        foreach (var (name, content) in files)
            fs.AddFile("/dl/" + name, System.Text.Encoding.UTF8.GetBytes(content));
        return fs;
    }

    private static async Task<string> Read(InMemoryFileSystem fs, string name)
    {
        await using var stream = await fs.OpenReadAsync(new VfsPath("/dl/" + name));
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task RenameOperation_ChainedNames_EndUpWhereThePreviewSaid()
    {
        // a -> b while b -> c: done in the order given, the first would land on b before b moved away.
        var fs = Files(("a.pdf", "A"), ("b.pdf", "B"));
        var op = new RenameOperation(
            fs, new VfsPath("/dl"), [("a.pdf", "b.pdf"), ("b.pdf", "c.pdf")], NullLogger.Instance);

        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        (await Read(fs, "b.pdf")).Should().Be("A");
        (await Read(fs, "c.pdf")).Should().Be("B");
    }

    [Fact]
    public async Task RenameOperation_ASwap_Works()
    {
        var fs = Files(("a.pdf", "A"), ("b.pdf", "B"));
        var op = new RenameOperation(
            fs, new VfsPath("/dl"), [("a.pdf", "b.pdf"), ("b.pdf", "a.pdf")], NullLogger.Instance);

        await op.ExecuteAsync(CancellationToken.None);

        (await Read(fs, "a.pdf")).Should().Be("B");
        (await Read(fs, "b.pdf")).Should().Be("A");
    }

    [Fact]
    public async Task RenameOperation_Undo_PutsEveryNameBack()
    {
        var fs = Files(("a.pdf", "A"), ("b.pdf", "B"));
        var op = new RenameOperation(
            fs, new VfsPath("/dl"), [("a.pdf", "x.pdf"), ("b.pdf", "y.pdf")], NullLogger.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        var failed = await op.UndoAsync();

        failed.Should().Be(0);
        (await Read(fs, "a.pdf")).Should().Be("A");
        (await Read(fs, "b.pdf")).Should().Be("B");
    }

    [Fact]
    public async Task RenameOperation_IsLoggedAsARename()
    {
        var fs = Files(("a.pdf", "A"));
        var op = new RenameOperation(fs, new VfsPath("/dl"), [("a.pdf", "x.pdf")], NullLogger.Instance);

        op.LogDescriptor!.Kind.Should().Be(OperationKind.Rename);
        op.LogDescriptor.Source.Should().Be("/dl/a.pdf");
        op.LogDescriptor.Destination.Should().Be("x.pdf");
        await Task.CompletedTask;
    }

    // ── The indexer does not follow links ─────────────────────────────────────

    [Fact]
    public async Task FileIndexWalker_SkipReparsePoints_DoesNotEnterAJunction()
    {
        static IFileEntry Entry(string path, string name, bool dir, FileAttributes attributes)
        {
            var mock = new Mock<IFileEntry>();
            mock.SetupGet(e => e.Path).Returns(new VfsPath(path));
            mock.SetupGet(e => e.Name).Returns(name);
            mock.SetupGet(e => e.IsDirectory).Returns(dir);
            mock.SetupGet(e => e.Attributes).Returns(attributes);
            return mock.Object;
        }

        var vfs = new Mock<IVirtualFileSystem>();
        vfs.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/repo"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                Entry("/repo/link", "link", true, FileAttributes.Directory | FileAttributes.ReparsePoint),
                Entry("/repo/a.cs", "a.cs", false, FileAttributes.Normal),
            });
        vfs.Setup(v => v.ListAsync(It.Is<VfsPath>(p => p.Value == "/repo/link"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Entry("/repo/link/secret.txt", "secret.txt", false, FileAttributes.Normal) });

        var walker = new FileIndexWalker(vfs.Object, new FileIndexOptions
        {
            RespectGitignore = false,
            IncludeDirectories = false,
            SkipReparsePoints = true,
        });

        var found = new List<string>();
        await foreach (var entry in walker.EnumerateAsync(new VfsPath("/repo")))
            found.Add(entry.Name);

        found.Should().Equal("a.cs");
    }

    // ── tcv_selection discloses only what is in scope ─────────────────────────

    [Fact]
    public void InScopeOrEmpty_PathOutsideTheApprovedDirectories_IsWithheld()
    {
        // The panels show wherever the user is browsing; an agent approved for one repository must
        // not learn the names of folders and files everywhere else.
        var approved = Directory.CreateDirectory(Path.Combine(_temp, "repo")).FullName;
        var elsewhere = Directory.CreateDirectory(Path.Combine(_temp, "taxes")).FullName;
        var scope = new PathScope([approved]);

        AgentBridge.InScopeOrEmpty(scope, Path.Combine(approved, "a.cs")).Should().NotBeEmpty();
        AgentBridge.InScopeOrEmpty(scope, Path.Combine(elsewhere, "2025.pdf")).Should().BeEmpty();
        AgentBridge.InScopeOrEmpty(new PathScope([]), approved).Should().BeEmpty();
    }

    // ── An approved write that skipped items is not «done» ────────────────────

    [Fact]
    public void DescribeOutcome_CompletedWithSkippedItems_IsNotReportedAsDone()
    {
        // TRAP-46: a move that left a junction behind still ends Completed.
        var outcome = AgentWriteGate.DescribeOutcome(
            OperationState.Completed, 1, ["link: junction or symbolic link not followed"], null);

        outcome.Should().NotBeNull().And.Contain("1 item(s) were skipped").And.Contain("junction");
    }

    [Fact]
    public void DescribeOutcome_CompletedCleanly_IsDone() =>
        AgentWriteGate.DescribeOutcome(OperationState.Completed, 0, [], null).Should().BeNull();

    [Fact]
    public void DescribeOutcome_Failed_CarriesTheFailure() =>
        AgentWriteGate.DescribeOutcome(OperationState.Failed, 0, [], "disk full").Should().Be("disk full");

    // ── English from the AI layer is translated before it is shown ────────────

    [Fact]
    public void LlmErrorText_KnownMessages_BecomeLocalizationKeys()
    {
        // Without a running application LocalizationManager answers «[key]», which is what shows the
        // English sentence was recognised and replaced.
        LlmErrorText.Localize(TCV.Ai.Llm.LlmClient.NotConfigured).Should().Be("[Llm_NotConfigured]");
        LlmErrorText.Localize(TCV.Ai.Llm.LlmClient.TimedOut(60)).Should().Be("[Llm_TimedOut]");
        LlmErrorText.Localize("503: overloaded").Should().Be("503: overloaded");
    }

    [Fact]
    public void AiRenameNoteText_EverySentenceOfANote_IsReplaced()
    {
        var note = TCV.Ai.Rename.AiRenameNotes.FolderDropped + " "
                   + TCV.Ai.Rename.AiRenameNotes.ExtensionKept(".jpg") + " "
                   + TCV.Ai.Rename.AiRenameNotes.CollisionSuffixed;

        var text = AiRenameNoteText.Localize(note);

        text.Should().Be("[AiRename_NoteFolderDropped] [AiRename_NoteExtension] [AiRename_NoteCollision]");
    }
}
