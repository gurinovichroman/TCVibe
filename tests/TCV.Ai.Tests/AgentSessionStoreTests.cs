using FluentAssertions;
using TCV.Ai.Agents;

namespace TCV.Ai.Tests;

/// <summary>The session store: round-trip, closing, trimming, and never throwing.</summary>
public sealed class AgentSessionStoreTests : IDisposable
{
    private readonly string _temp;
    private readonly string _file;

    public AgentSessionStoreTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "tcv-sessions-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_temp);
        _file = Path.Combine(_temp, "agent-sessions.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); }
        catch { /* leftover temp directory */ }
    }

    private AgentSessionStore NewStore() => new(_file);

    [Fact]
    public void Start_PersistsTheSessionAndItReadsBack()
    {
        var store = NewStore();

        var started = store.Start("Claude Code", @"D:\repo", "fix the parser");

        // A second instance: the point is that it survives a restart.
        var sessions = NewStore().Load();

        sessions.Should().ContainSingle();
        sessions[0].Id.Should().Be(started.Id);
        sessions[0].Tool.Should().Be("Claude Code");
        sessions[0].Task.Should().Be("fix the parser");
        sessions[0].IsOpen.Should().BeTrue();
    }

    [Fact]
    public void Start_NormalisesTheRoot()
    {
        var store = NewStore();

        var session = store.Start("Aider", @"D:\repo\", string.Empty);

        session.Root.Should().Be(@"D:\repo");
    }

    [Fact]
    public void Load_NoFileYet_IsEmpty() => NewStore().Load().Should().BeEmpty();

    [Fact]
    public void Load_ReturnsNewestFirst()
    {
        var store = NewStore();
        store.Start("Old", @"D:\a", string.Empty, DateTime.UtcNow.AddHours(-2));
        store.Start("New", @"D:\b", string.Empty, DateTime.UtcNow);

        store.Load()[0].Tool.Should().Be("New");
    }

    [Fact]
    public void Close_MarksTheSessionFinished()
    {
        var store = NewStore();
        var session = store.Start("Claude Code", @"D:\repo", string.Empty);

        store.Close(session.Id).Should().BeTrue();

        var reloaded = NewStore().Load().Single();
        reloaded.IsOpen.Should().BeFalse();
        reloaded.EndedUtc.Should().NotBeNull();
    }

    [Fact]
    public void Close_UnknownId_ReportsFalseRatherThanThrowing()
    {
        NewStore().Close("nope").Should().BeFalse();
    }

    [Fact]
    public void LoadOpen_ReturnsOnlyLiveSessions()
    {
        var store = NewStore();
        var first = store.Start("A", @"D:\a", string.Empty);
        store.Start("B", @"D:\b", string.Empty);
        store.Close(first.Id);

        store.LoadOpen().Should().ContainSingle().Which.Tool.Should().Be("B");
    }

    [Fact]
    public void CloseAll_ClosesEveryOpenSessionAndReportsHowMany()
    {
        var store = NewStore();
        store.Start("A", @"D:\a", string.Empty);
        store.Start("B", @"D:\b", string.Empty);

        store.CloseAll().Should().Be(2);
        store.LoadOpen().Should().BeEmpty();

        // Idempotent: a second pass has nothing left to close.
        store.CloseAll().Should().Be(0);
    }

    [Fact]
    public void Start_BeyondTheCap_TrimsTheOldest()
    {
        var store = NewStore();
        for (int i = 0; i < AgentSessionStore.MaxSessions + 10; i++)
            store.Start($"tool{i}", @"D:\a", string.Empty, DateTime.UtcNow.AddSeconds(i));

        var sessions = store.Load();

        sessions.Should().HaveCount(AgentSessionStore.MaxSessions);
        sessions[0].Tool.Should().Be($"tool{AgentSessionStore.MaxSessions + 9}");
    }

    [Fact]
    public void Load_CorruptFile_ReadsAsEmptyRatherThanThrowing()
    {
        File.WriteAllText(_file, "{ this is not json");

        NewStore().Load().Should().BeEmpty();
    }

    [Fact]
    public void Start_UnwritableLocation_DoesNotThrow()
    {
        // Provenance hints are a convenience; a write failure must never break a launch.
        var store = new AgentSessionStore(Path.Combine(_temp, "no", "such", "dir", "s.json"));

        var act = () => store.Start("A", @"D:\a", string.Empty);

        act.Should().NotThrow();
    }

    [Fact]
    public void Label_CombinesToolAndTask()
    {
        new AgentSession("i", "Claude Code", @"D:\a", DateTime.UtcNow, null, "fix parser")
            .Label.Should().Be("Claude Code: fix parser");

        new AgentSession("i", "Claude Code", @"D:\a", DateTime.UtcNow, null, string.Empty)
            .Label.Should().Be("Claude Code");
    }
}
