using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>
/// The connections the palette offers (6.82): the ones opened last, the most recent first, and the
/// time they were opened kept in the book without a stale window writing over another's.
/// </summary>
public sealed class RecentConnectionsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-recent-" + Guid.NewGuid().ToString("N"));

    public RecentConnectionsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private string BookPath => Path.Combine(_dir, "connections.json");

    private static ConnectionInfo Server(string name, DateTime? lastUsed = null) => new()
    {
        Name = name, Protocol = NetworkProtocol.Sftp, Host = name + ".example", Port = 22, Username = "u", LastUsed = lastUsed,
    };

    [Fact]
    public void Pick_TheMostRecentFirst_OnlyThoseEverOpened_AHandful()
    {
        var t = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var all = new[]
        {
            Server("never"),
            Server("old", t),
            Server("newest", t.AddDays(3)),
            Server("middle", t.AddDays(1)),
        };

        RecentConnections.Pick(all).Select(c => c.Name).Should().Equal("newest", "middle", "old");
        RecentConnections.Pick(all, count: 2).Select(c => c.Name).Should().Equal("newest", "middle");
        RecentConnections.Pick(Enumerable.Range(0, 9).Select(i => Server("s" + i, t.AddHours(i)))).Should().HaveCount(RecentConnections.PaletteCount);
    }

    [Fact]
    public void CommandId_RoundTrips_AndOtherIdsAreNotConnections()
    {
        var server = Server("prod");

        RecentConnections.ParseCommandId(RecentConnections.CommandId(server)).Should().Be(server.Id);
        RecentConnections.ParseCommandId("UserTool:prod").Should().BeNull();
        RecentConnections.ParseCommandId("Connection:not-a-guid").Should().BeNull();
        RecentConnections.ParseCommandId("Reconnect").Should().BeNull();
    }

    [Fact]
    public void MarkUsed_IsKeptAcrossAReload()
    {
        var book = new ConnectionBook(BookPath);
        var server = Server("prod");
        book.Add(server);
        var when = new DateTime(2026, 10, 15, 9, 30, 0, DateTimeKind.Utc);

        book.MarkUsed(server.Id, when).Should().BeTrue();

        new ConnectionBook(BookPath).Connections.Single().LastUsed.Should().Be(when);
    }

    [Fact]
    public void MarkUsed_DoesNotWriteBackAStaleList()
    {
        // Two windows (TRAP-22): the guest loaded the book before the main window added "staging".
        var guest = new ConnectionBook(BookPath);
        var prod = Server("prod");
        guest.Add(prod);
        var main = new ConnectionBook(BookPath);
        main.Add(Server("staging"));

        guest.MarkUsed(prod.Id, DateTime.UtcNow).Should().BeTrue();

        new ConnectionBook(BookPath).Connections.Select(c => c.Name).Should().BeEquivalentTo("prod", "staging");
    }

    [Fact]
    public void MarkUsed_AnUnknownConnection_WritesNothing()
    {
        var book = new ConnectionBook(BookPath);
        book.Add(Server("prod"));
        var before = File.ReadAllText(BookPath);

        book.MarkUsed(Guid.NewGuid(), DateTime.UtcNow).Should().BeFalse();

        File.ReadAllText(BookPath).Should().Be(before);
    }

    [Fact]
    public void MarkUsed_ADamagedFile_IsLeftAlone()
    {
        var book = new ConnectionBook(BookPath);
        var prod = Server("prod");
        book.Add(prod);
        File.WriteAllText(BookPath, "{ not json");

        book.MarkUsed(prod.Id, DateTime.UtcNow).Should().BeFalse();

        File.ReadAllText(BookPath).Should().Be("{ not json");
    }

    [Fact]
    public void Update_FromTheEditor_KeepsWhenItWasLastOpened()
    {
        var book = new ConnectionBook(BookPath);
        var prod = Server("prod");
        book.Add(prod);
        var when = new DateTime(2026, 10, 15, 9, 30, 0, DateTimeKind.Utc);
        book.MarkUsed(prod.Id, when);

        // The editor builds a fresh copy of the fields: it knows nothing of the time.
        var edited = Server("prod renamed");
        edited.Id = prod.Id;
        book.Update(edited);

        new ConnectionBook(BookPath).Connections.Single().LastUsed.Should().Be(when);
    }

    [Fact]
    public void Load_AnOlderFileWithoutTheTime_ReadsAsNeverOpened()
    {
        File.WriteAllText(BookPath, """
            [ { "Id": "8a3f2c1e-0000-4000-8000-000000000001", "Name": "old", "Protocol": 2, "Host": "h", "Port": 22, "Username": "u", "RemotePath": "/" } ]
            """);

        var info = new ConnectionBook(BookPath).Connections.Single();

        info.Name.Should().Be("old");
        info.LastUsed.Should().BeNull();
    }

    [Fact]
    public void AddRange_AddsThemAll()
    {
        var book = new ConnectionBook(BookPath);

        book.AddRange([Server("a"), Server("b")]);

        new ConnectionBook(BookPath).Connections.Select(c => c.Name).Should().Equal("a", "b");
    }

    [Fact]
    public void KeepAlivePolicy_PingsAfterAMinuteOfSilence()
    {
        var last = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

        KeepAlivePolicy.IsDue(last, last.AddSeconds(59)).Should().BeFalse();
        KeepAlivePolicy.IsDue(last, last.AddSeconds(60)).Should().BeTrue();
        KeepAlivePolicy.CheckInterval.Should().BeLessThan(KeepAlivePolicy.IdleBeforePing);
    }
}
