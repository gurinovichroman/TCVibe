using System.IO;
using FluentAssertions;
using TCV.App.Operations;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>Covers <see cref="OperationLog"/> — JSON round-trip, ordering, capping and clear.</summary>
public class OperationLogTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tcv-oplog-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static OperationLogEntry Entry(OperationKind kind, string source, OperationOutcome outcome) =>
        new()
        {
            Timestamp = new DateTime(2026, 7, 24, 10, 0, 0),
            Kind = kind,
            Source = source,
            Destination = "D:\\dest",
            ItemCount = 3,
            Outcome = outcome,
        };

    [Fact]
    public void LoadAll_MissingFile_ReturnsEmpty()
    {
        var log = new OperationLog(_path);
        log.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void Append_ThenLoadAll_RoundTripsAllFields()
    {
        var log = new OperationLog(_path);
        var entry = Entry(OperationKind.Copy, "C:\\src\\a.txt", OperationOutcome.Completed) with
        {
            Error = null,
        };

        log.Append(entry);

        log.LoadAll().Should().ContainSingle().Which.Should().BeEquivalentTo(entry);
    }

    [Fact]
    public void LoadAll_ReturnsNewestFirst()
    {
        var log = new OperationLog(_path);
        log.Append(Entry(OperationKind.Copy, "first", OperationOutcome.Completed));
        log.Append(Entry(OperationKind.Delete, "second", OperationOutcome.Failed));

        var all = log.LoadAll();

        all.Should().HaveCount(2);
        all[0].Source.Should().Be("second");
        all[1].Source.Should().Be("first");
    }

    [Fact]
    public void Append_PastCap_TrimsOldestEntries()
    {
        var log = new OperationLog(_path);
        for (int i = 0; i < 1050; i++)
            log.Append(Entry(OperationKind.Move, i.ToString(), OperationOutcome.Completed));

        var all = log.LoadAll();

        all.Should().HaveCount(1000);
        // Newest first: entry 1049 survived, entry 49 was trimmed.
        all[0].Source.Should().Be("1049");
        all.Should().NotContain(e => e.Source == "49");
    }

    [Fact]
    public void Clear_RemovesAllEntries()
    {
        var log = new OperationLog(_path);
        log.Append(Entry(OperationKind.Copy, "x", OperationOutcome.Completed));

        log.Clear();

        log.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void Append_RaisesChangedEvent()
    {
        var log = new OperationLog(_path);
        var raised = false;
        log.Changed += (_, _) => raised = true;

        log.Append(Entry(OperationKind.Copy, "x", OperationOutcome.Completed));

        raised.Should().BeTrue();
    }

    [Fact]
    public void Append_ChangedEvent_CarriesTheAppendedEntry()
    {
        var log = new OperationLog(_path);
        var entry = Entry(OperationKind.Copy, "x", OperationOutcome.Completed);
        OperationLogEntry? received = null;
        log.Changed += (_, e) => received = e;

        log.Append(entry);

        received.Should().BeEquivalentTo(entry);
    }

    [Fact]
    public void Clear_ChangedEvent_CarriesNull()
    {
        var log = new OperationLog(_path);
        log.Append(Entry(OperationKind.Copy, "x", OperationOutcome.Completed));
        var sawNull = false;
        log.Changed += (_, e) => sawNull = e is null;

        log.Clear();

        sawNull.Should().BeTrue();
    }

    [Fact]
    public void TwoInstances_SharingOneFile_KeepBothOfTheirEntries()
    {
        // Two portable instances append to the same file; neither may clobber the other's history.
        var a = new OperationLog(_path);
        var b = new OperationLog(_path);

        a.Append(Entry(OperationKind.Copy, "from-a", OperationOutcome.Completed));
        b.Append(Entry(OperationKind.Delete, "from-b", OperationOutcome.Failed));

        var all = new OperationLog(_path).LoadAll();

        all.Should().HaveCount(2);
        all.Should().Contain(e => e.Source == "from-a");
        all.Should().Contain(e => e.Source == "from-b");
    }
}
