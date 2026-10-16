using System.IO;
using FluentAssertions;
using TCV.App.Navigation;

namespace TCV.App.Tests;

public class FileUsageStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    public FileUsageStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tcv-usage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "file-usage.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private FileUsageStore Store() => new(_file);

    [Fact]
    public void LoadAll_MissingFile_ReturnsEmpty()
    {
        Store().LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadAll_CorruptFile_DegradesToEmpty()
    {
        File.WriteAllText(_file, "{ not json at all");

        Store().LoadAll().Should().BeEmpty("ranking data is never worth failing a keystroke over");
    }

    [Fact]
    public void Record_NewPath_StartsAtOneHit()
    {
        var store = Store();

        store.Record(@"D:\repo\app.cs", Now);

        var entry = store.LoadAll().Should().ContainSingle().Subject;
        entry.Path.Should().Be(@"D:\repo\app.cs");
        entry.Hits.Should().Be(1);
        entry.LastUsedUtc.Should().Be(Now);
    }

    [Fact]
    public void Record_SamePathTwice_CountsHitsAndMovesTheTimestamp()
    {
        var store = Store();
        store.Record(@"D:\repo\app.cs", Now.AddDays(-3));
        store.Record(@"D:\repo\app.cs", Now);

        var entry = store.LoadAll().Should().ContainSingle().Subject;
        entry.Hits.Should().Be(2);
        entry.LastUsedUtc.Should().Be(Now);
    }

    [Fact]
    public void Record_IsCaseInsensitiveOnPaths()
    {
        var store = Store();
        store.Record(@"D:\repo\App.cs", Now);
        store.Record(@"d:\REPO\app.cs", Now);

        store.LoadAll().Should().ContainSingle("Windows paths differ only in case");
    }

    [Fact]
    public void Record_BlankPath_IsIgnored()
    {
        var store = Store();

        store.Record("   ", Now);

        store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void Record_BeyondTheCap_KeepsTheStrongestEntries()
    {
        var store = Store();

        // One heavily used file plus enough one-hit stragglers to overflow the cap
        store.Record(@"D:\repo\hot.cs", Now);
        store.Record(@"D:\repo\hot.cs", Now);
        store.Record(@"D:\repo\hot.cs", Now);
        for (var i = 0; i < FileUsageStore.MaxEntries; i++)
            store.Record($@"D:\repo\cold{i}.cs", Now.AddDays(-200));

        var all = store.LoadAll();
        all.Should().HaveCount(FileUsageStore.MaxEntries);
        all.Should().Contain(e => e.Path.EndsWith("hot.cs"));
    }

    [Fact]
    public void LoadIndex_ScoresRecordedPaths_AndIgnoresUnknownOnes()
    {
        var store = Store();
        store.Record(@"D:\repo\app.cs", Now);

        var index = store.LoadIndex(Now);

        index.Score(@"D:\repo\app.cs").Should().BeGreaterThan(0);
        index.Score(@"D:\repo\other.cs").Should().Be(0);
    }

    [Fact]
    public void Index_MatchesPathsAcrossSeparatorStyle()
    {
        var index = new FileUsageIndex(
            [new FileUsageEntry { Path = @"D:\repo\app.cs", Hits = 1, LastUsedUtc = Now }], Now);

        index.Score("D:/repo/app.cs").Should().BeGreaterThan(0,
            "the picker reports VFS paths with forward slashes");
    }
}

public class FileUsageIndexTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Frecency_NoHits_IsZero()
    {
        FileUsageIndex.Frecency(0, Now, Now).Should().Be(0);
    }

    [Fact]
    public void Frecency_GrowsWithHits()
    {
        FileUsageIndex.Frecency(3, Now, Now).Should()
            .BeGreaterThan(FileUsageIndex.Frecency(1, Now, Now));
    }

    [Fact]
    public void Frecency_DecaysWithAge()
    {
        var fresh = FileUsageIndex.Frecency(2, Now.AddMinutes(-10), Now);
        var yesterday = FileUsageIndex.Frecency(2, Now.AddDays(-1).AddHours(-1), Now);
        var lastYear = FileUsageIndex.Frecency(2, Now.AddDays(-300), Now);

        fresh.Should().BeGreaterThan(yesterday);
        yesterday.Should().BeGreaterThan(lastYear);
    }

    [Fact]
    public void Frecency_RecentSingleHit_BeatsAnOldPairOfHits()
    {
        FileUsageIndex.Frecency(1, Now.AddMinutes(-5), Now).Should()
            .BeGreaterThan(FileUsageIndex.Frecency(1, Now.AddDays(-90), Now));
    }

    [Fact]
    public void HabitBonus_IsZeroWithoutHistory_AndCappedWellBelowThePrefixBonus()
    {
        FileUsageIndex.HabitBonus(0).Should().Be(0);
        FileUsageIndex.HabitBonus(100_000).Should().BeLessThan(100,
            "habit must never outrank a clearly better textual match");
    }

    [Fact]
    public void HabitBonus_RisesWithFrecency_ThenFlattensAtTheCap()
    {
        FileUsageIndex.HabitBonus(1).Should().BeLessThan(FileUsageIndex.HabitBonus(10));
        FileUsageIndex.HabitBonus(10).Should().BeLessThan(FileUsageIndex.HabitBonus(100));
        FileUsageIndex.HabitBonus(1_000).Should().Be(FileUsageIndex.HabitBonus(1_000_000));
    }

    [Fact]
    public void Empty_ScoresNothing()
    {
        FileUsageIndex.Empty.Score(@"D:\repo\app.cs").Should().Be(0);
    }
}
