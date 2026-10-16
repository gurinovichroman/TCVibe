using FluentAssertions;
using TCV.Ai.Agents;

namespace TCV.Ai.Tests;

/// <summary>
/// The provenance rules behind the «Agent changes» dashboard. These are the tests that keep the
/// verdict honest: the whole design admits that attribution is partly circumstantial, so what must be
/// pinned down is exactly <em>when</em> a stronger claim is allowed and when it degrades.
/// </summary>
public class ChangeAttributionTests
{
    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static ChangedFileFact File(string relative, DateTime? modified, string root = @"D:\repo") =>
        new(relative, System.IO.Path.Combine(root, relative.Replace('/', '\\')), modified);

    private static AgentSession Session(
        DateTime started, DateTime? ended = null, string root = @"D:\repo", string tool = "Claude Code") =>
        new("s1", tool, root, started, ended, "fix the parser");

    [Fact]
    public void Attribute_NoEvidence_ReportsExternal()
    {
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-1)) };

        var result = ChangeAttribution.Attribute(changes, [], [], Now);

        result.Should().HaveCount(1);
        result[0].Origin.Should().Be(ChangeOrigin.External);
        result[0].Actor.Should().BeEmpty();
    }

    [Fact]
    public void Attribute_InsideSessionWindowAndRoot_ReportsAgent()
    {
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-5)) };
        var sessions = new[] { Session(Now.AddMinutes(-30)) };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.Agent);
        result[0].Actor.Should().Be("Claude Code: fix the parser");
    }

    [Fact]
    public void Attribute_ChangeBeforeSessionStarted_StaysExternal()
    {
        // The file was written before the agent ever ran, so the session cannot own it.
        var changes = new[] { File("src/a.cs", Now.AddHours(-3)) };
        var sessions = new[] { Session(Now.AddMinutes(-30)) };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_ClosedSession_DoesNotClaimLaterChange()
    {
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-2)) };
        var sessions = new[] { Session(Now.AddHours(-2), ended: Now.AddMinutes(-30)) };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_SessionRootElsewhere_StaysExternal()
    {
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-5)) };
        var sessions = new[] { Session(Now.AddHours(-1), root: @"D:\other") };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_SiblingRootWithSharedPrefix_StaysExternal()
    {
        // The segment-aware containment test: D:\repo-old must not count as inside D:\repo.
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-5), root: @"D:\repo-old") };
        var sessions = new[] { Session(Now.AddHours(-1), root: @"D:\repo") };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_LoggedOperationOnTheFile_ReportsManager()
    {
        var at = Now.AddMinutes(-2);
        var changes = new[] { File("src/a.cs", at) };
        var writes = new[] { new ManagerWriteFact(at, [@"D:\repo\src\a.cs"], string.Empty) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Manager);
        result[0].Actor.Should().Be(ChangeAttribution.ManagerActor);
    }

    [Fact]
    public void Attribute_LoggedOperationWithAgentTag_ReportsSupervised()
    {
        var at = Now.AddMinutes(-2);
        var changes = new[] { File("src/a.cs", at) };
        var writes = new[] { new ManagerWriteFact(at, [@"D:\repo\src\a.cs"], "claude-code") };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Supervised);
        result[0].Actor.Should().Be("claude-code");
    }

    [Fact]
    public void Attribute_LogBeatsSession_BecauseItIsHardEvidence()
    {
        var at = Now.AddMinutes(-2);
        var changes = new[] { File("src/a.cs", at) };
        var writes = new[] { new ManagerWriteFact(at, [@"D:\repo\src\a.cs"], "claude-code") };
        var sessions = new[] { Session(Now.AddHours(-1)) };

        var result = ChangeAttribution.Attribute(changes, writes, sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.Supervised);
    }

    [Fact]
    public void Attribute_LoggedDirectoryOfABatch_ClaimsFilesDirectlyInside()
    {
        // A multi-item operation is logged as its containing directory (DescribeEntries).
        var at = Now.AddMinutes(-1);
        var changes = new[] { File("src/a.cs", at) };
        var writes = new[] { new ManagerWriteFact(at, [], string.Empty, [@"D:\repo\src"]) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Manager);
    }

    [Fact]
    public void Attribute_LoggedDirectoryOfABatch_IsReportedAsFolderEvidenceNotProof()
    {
        var at = Now.AddMinutes(-1);
        var changes = new[] { File("src/a.cs", at) };
        var writes = new[] { new ManagerWriteFact(at, [], "claude-code", [@"D:\repo\src"]) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Supervised);
        result[0].ByDirectory.Should().BeTrue();
    }

    [Fact]
    public void Attribute_NamedFileInTheSameFolder_DoesNotClaimItsNeighbour()
    {
        // A one-file copy into src\ names the copied file. It used to claim, as proof, an unrelated
        // edit of the user's in src\ made a few minutes before.
        var at = Now.AddMinutes(-1);
        var changes = new[] { File("src/users-own.cs", at.AddMinutes(-3)) };
        var writes = new[] { new ManagerWriteFact(at, [@"D:\repo\src\copied.cs"], "claude-code") };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_NameMatchBeatsANewerFolderMatch()
    {
        var at = Now.AddMinutes(-2);
        var changes = new[] { File("src/a.cs", at) };
        var writes = new[]
        {
            new ManagerWriteFact(at, [@"D:\repo\src\a.cs"], "claude-code"),
            new ManagerWriteFact(at.AddSeconds(1), [], string.Empty, [@"D:\repo\src"]),
        };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Supervised);
        result[0].ByDirectory.Should().BeFalse();
    }

    [Fact]
    public void Attribute_LoggedDirectory_DoesNotClaimDeeperSubtree()
    {
        // Only the immediate parent counts — otherwise one copy would account for everything below it.
        var at = Now.AddMinutes(-1);
        var changes = new[] { File("src/deep/a.cs", at) };
        var writes = new[] { new ManagerWriteFact(at, [], string.Empty, [@"D:\repo\src"]) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_LoggedOperationLongBefore_DoesNotClaimTheChange()
    {
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-1)) };
        var writes = new[] { new ManagerWriteFact(Now.AddHours(-4), [@"D:\repo\src\a.cs"], string.Empty) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_DeletedFile_StillMatchesALoggedDelete()
    {
        // No mtime to place in time, but a logged delete names what it removed.
        var changes = new[] { File("src/gone.cs", null) };
        var writes = new[] { new ManagerWriteFact(Now.AddMinutes(-1), [@"D:\repo\src\gone.cs"], string.Empty) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Manager);
    }

    [Fact]
    public void Attribute_DeletedFile_IsNotClaimedByAnAncientLogEntry()
    {
        // The recency check for a deletion has to be against the clock: comparing the entry with the
        // absent mtime reduced to "always true", so a weeks-old copy naming the parent directory
        // claimed today's manual deletion as ours.
        var changes = new[] { File("src/gone.cs", null) };
        var writes = new[] { new ManagerWriteFact(Now.AddDays(-14), [], string.Empty, [@"D:\repo\src"]) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_DeletedFile_IsStillClaimedHoursLater()
    {
        // git keeps reporting the deletion until it is committed, so "today" has to keep counting.
        var changes = new[] { File("src/gone.cs", null) };
        var writes = new[] { new ManagerWriteFact(Now.AddHours(-6), [@"D:\repo\src\gone.cs"], string.Empty) };

        var result = ChangeAttribution.Attribute(changes, writes, [], Now);

        result[0].Origin.Should().Be(ChangeOrigin.Manager);
    }

    [Fact]
    public void Attribute_DeletedFile_CannotBeClaimedByASession()
    {
        // A session needs a moment to test against its window; a deletion has none.
        var changes = new[] { File("src/gone.cs", null) };
        var sessions = new[] { Session(Now.AddHours(-1)) };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Origin.Should().Be(ChangeOrigin.External);
    }

    [Fact]
    public void Attribute_SeveralSessionsOnOneRoot_PrefersTheMostRecent()
    {
        var changes = new[] { File("src/a.cs", Now.AddMinutes(-2)) };
        var sessions = new[]
        {
            new AgentSession("old", "Aider", @"D:\repo", Now.AddHours(-3), null, string.Empty),
            new AgentSession("new", "Codex CLI", @"D:\repo", Now.AddMinutes(-10), null, string.Empty),
        };

        var result = ChangeAttribution.Attribute(changes, [], sessions, Now);

        result[0].Actor.Should().Be("Codex CLI");
    }

    [Fact]
    public void Attribute_EveryChangeGetsExactlyOneVerdict()
    {
        var changes = new[]
        {
            File("a.cs", Now.AddMinutes(-1)),
            File("b.cs", null),
            File("c.cs", Now.AddHours(-9)),
        };

        var result = ChangeAttribution.Attribute(changes, [], [], Now);

        result.Should().HaveCount(3);
        result.Select(r => r.RelativePath).Should().Equal("a.cs", "b.cs", "c.cs");
    }

    [Fact]
    public void Covers_OpenSession_IsBoundedByNow()
    {
        var session = Session(Now.AddHours(-1));

        session.Covers(Now.AddMinutes(-30), Now).Should().BeTrue();
        session.Covers(Now.AddDays(7), Now).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"D:\repo", @"D:\repo\src\a.cs", true)]
    [InlineData(@"D:\repo", @"D:\repo", true)]
    [InlineData(@"D:\repo\", @"D:\repo\src", true)]
    [InlineData(@"D:\repo", @"D:\repo-old\src", false)]
    [InlineData(@"D:\repo", @"D:\other", false)]
    [InlineData("", @"D:\repo", false)]
    public void Contains_ComparesWholeSegments(string root, string path, bool expected) =>
        ChangeAttribution.Contains(root, path).Should().Be(expected);
}
