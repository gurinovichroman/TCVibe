using FluentAssertions;
using TCV.Ai.Agents;
using TCV.Ai.Mcp;
using TCV.Ai.Rename;

namespace TCV.Ai.Tests;

/// <summary>Small pure pieces added when the agent layer was hardened; each pins one behaviour.</summary>
public sealed class AgentLayerHardeningTests : IDisposable
{
    private readonly string _temp =
        Path.Combine(Path.GetTempPath(), "tcv-hard-" + Guid.NewGuid().ToString("n")[..8]);

    public AgentLayerHardeningTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* best effort */ }
    }

    // ── panel:// paths ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/logs/app.log", "/logs/app.log")]
    [InlineData("logs/app.log", "/logs/app.log")]
    [InlineData("/", "/")]
    [InlineData("", "/")]
    [InlineData("/a/./b//c/", "/a/b/c")]
    [InlineData("/a/b/../c", "/a/c")]
    [InlineData("/../../etc/passwd", "/etc/passwd")]        // climbing past the root stays at the root
    [InlineData("\\pub\\x.txt", "/pub/x.txt")]
    public void MountPath_IsAbsoluteInsideTheMountAndNeverClimbsAboveIt(string inner, string expected) =>
        AgentPathRef.MountPath(inner).Should().Be(expected);

    [Fact]
    public void MountPath_OfWhatTheServerPrints_RoundTrips()
    {
        // tcv_list prints panel://active/logs/app.log; passing it back must name the same file, not
        // /logs/logs/app.log as it did when paths were resolved against the panel's own directory.
        var printed = AgentPathRef.Parse("panel://active/logs/app.log");

        AgentPathRef.MountPath(printed.Inner).Should().Be("/logs/app.log");
    }

    // ── The discovery file ────────────────────────────────────────────────────

    [Fact]
    public void DeleteIfOwnedBy_LeavesAFileAnotherProcessWrote()
    {
        var path = Path.Combine(_temp, "mcp-endpoint.json");
        McpEndpointFile.Write(
            new McpEndpointInfo("http://127.0.0.1:1/mcp", "t", Environment.ProcessId + 1, DateTime.UtcNow, []), path);

        McpEndpointFile.DeleteIfOwnedBy(Environment.ProcessId, path).Should().BeFalse();

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void DeleteIfOwnedBy_RemovesOurOwnFile()
    {
        var path = Path.Combine(_temp, "mcp-endpoint.json");
        McpEndpointFile.Write(
            new McpEndpointInfo("http://127.0.0.1:1/mcp", "t", Environment.ProcessId, DateTime.UtcNow, []), path);

        McpEndpointFile.DeleteIfOwnedBy(Environment.ProcessId, path).Should().BeTrue();

        File.Exists(path).Should().BeFalse();
    }

    // ── Approving a folder ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData(@"D:\", true)]
    [InlineData(@"C:\Users\me", true)]         // the profile itself
    [InlineData(@"C:\Users", true)]            // the folder above it
    [InlineData(@"C:\Users\me\src\repo", false)]
    [InlineData(@"C:\Users\other\repo", false)]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"C:\Windows\Temp", true)]
    [InlineData(@"D:\work\repo", false)]
    public void IsBroad_FlagsWhatWouldMakeTheApprovedListMeaningless(string path, bool expected) =>
        ApprovedRootAdvisor.IsBroad(path, @"C:\Users\me", @"C:\Windows").Should().Be(expected);

    // ── AI rename: names typed into the table ─────────────────────────────────

    [Theory]
    [InlineData("good-name.pdf", AiRenameProblem.None)]
    [InlineData("", AiRenameProblem.Empty)]
    [InlineData("  ", AiRenameProblem.Empty)]
    [InlineData(@"..\..\evil.pdf", AiRenameProblem.InvalidCharacters)]
    [InlineData(@"C:\Windows\evil.dll", AiRenameProblem.InvalidCharacters)]
    [InlineData("sub/dir.pdf", AiRenameProblem.InvalidCharacters)]
    [InlineData("stream.txt:hidden", AiRenameProblem.InvalidCharacters)]
    [InlineData("name.", AiRenameProblem.TrailingDotOrSpace)]
    [InlineData("name ", AiRenameProblem.TrailingDotOrSpace)]
    [InlineData("CON.txt", AiRenameProblem.ReservedName)]
    public void Validate_RefusesWhatWouldMoveAFileOrConfuseWindows(string name, AiRenameProblem expected) =>
        AiRenamePlan.Validate(name).Should().Be(expected);

    [Fact]
    public void ValidateBatch_TwoFilesGettingOneName_IsRefused()
    {
        var problems = AiRenamePlan.ValidateBatch(
            [("a.pdf", "same.pdf"), ("b.pdf", "SAME.pdf")], ["a.pdf", "b.pdf"]);

        problems.Should().ContainSingle().Which.Value.Should().Be(AiRenameProblem.DuplicateInBatch);
    }

    [Fact]
    public void ValidateBatch_NameOfAFileNotInTheBatch_IsRefused()
    {
        var problems = AiRenamePlan.ValidateBatch(
            [("a.pdf", "taken.pdf")], ["a.pdf", "taken.pdf"]);

        problems["a.pdf"].Should().Be(AiRenameProblem.AlreadyExists);
    }

    [Fact]
    public void ValidateBatch_NameFreedByTheBatchItself_IsFine()
    {
        var problems = AiRenamePlan.ValidateBatch(
            [("a.pdf", "b.pdf"), ("b.pdf", "c.pdf")], ["a.pdf", "b.pdf"]);

        problems.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ProposalMatchingAFileOutsideTheSelection_GetsASuffix()
    {
        var reply = """[{"from":"scan1.pdf","to":"invoice.pdf"}]""";

        var items = AiRenamePlan.Parse(reply, ["scan1.pdf"], existingNames: ["invoice.pdf"]);

        items.Single().Proposed.Should().Be("invoice (2).pdf");
    }

    [Fact]
    public void NeedsReview_IsTrueOnlyForARowThatWasCorrected()
    {
        var plain = new AiRenameItem("a.pdf", "b.pdf", string.Empty);
        var fixedUp = new AiRenameItem("a.pdf", "b (2).pdf", "A numeric suffix resolved a name collision.");
        var untouched = new AiRenameItem("a.pdf", "a.pdf", "The model proposed no name.");

        plain.NeedsReview.Should().BeFalse();
        fixedUp.NeedsReview.Should().BeTrue();
        untouched.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_GivesACodeTheUiCanLocalise()
    {
        JunkCornerGuard.Evaluate("repo", ["a.txt"], insideRepository: true).Code
            .Should().Be(JunkCornerReason.InsideRepository);

        JunkCornerGuard.Evaluate("src", ["a.txt"], insideRepository: false).Code
            .Should().Be(JunkCornerReason.ProjectDirectory);

        var source = JunkCornerGuard.Evaluate("Downloads", ["a.cs", "b.cs", "c.pdf"], insideRepository: false);
        source.Code.Should().Be(JunkCornerReason.SourceFiles);
        source.CodeFiles.Should().Be(2);
        source.Total.Should().Be(3);
    }

    // ── Sessions a crash left open (TRAP-31) ──────────────────────────────────

    [Fact]
    public void CloseStale_EndsAnOrphanedSessionAtTheLastHeartbeat_NotAtRestart()
    {
        var store = new AgentSessionStore(Path.Combine(_temp, "sessions.json"));
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var session = store.Start("claude", @"D:\repo", "task", start);

        store.Heartbeat(start.AddHours(1));
        var restart = start.AddDays(3);

        store.CloseStale(restart).Should().Be(1);

        var closed = store.Load().Single(s => s.Id == session.Id);
        closed.EndedUtc.Should().Be(start.AddHours(1));
        // Edits made while the app was not running are not this session's.
        closed.Covers(start.AddDays(2), restart).Should().BeFalse();
    }

    [Fact]
    public void CloseStale_WithAFreshHeartbeat_LeavesSessionsOfAWindowStillRunning()
    {
        var store = new AgentSessionStore(Path.Combine(_temp, "sessions.json"));
        var now = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        store.Start("claude", @"D:\repo", "task", now);
        store.Heartbeat(now.AddSeconds(-30));

        store.CloseStale(now).Should().Be(0);

        store.LoadOpen().Should().ContainSingle();
    }

    [Fact]
    public void CloseStale_WithNoHeartbeatAtAll_EndsTheSessionWhereItStarted()
    {
        var store = new AgentSessionStore(Path.Combine(_temp, "sessions.json"));
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var session = store.Start("claude", @"D:\repo", "task", start);

        store.CloseStale(start.AddDays(1));

        store.Load().Single(s => s.Id == session.Id).EndedUtc.Should().Be(start);
    }
}
