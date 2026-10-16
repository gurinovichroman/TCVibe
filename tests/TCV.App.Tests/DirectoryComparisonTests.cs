using System.IO;
using FluentAssertions;
using TCV.App.Sync;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>Covers the directory comparison verdicts added in task 6.23.</summary>
public class DirectoryComparisonTests
{
    private static IFileEntry File(string name, long size = 100, DateTime? modified = null) =>
        new StubEntry(name, false, size, modified ?? new DateTime(2026, 7, 27, 12, 0, 0));

    private static IFileEntry Dir(string name) =>
        new StubEntry(name, true, 0, new DateTime(2026, 7, 27, 12, 0, 0));

    private static readonly ComparisonOptions Options = new();

    // ── Pairwise verdicts ─────────────────────────────────────────────────────

    [Fact]
    public void Compare_SameSizeAndTime_IsSame() =>
        DirectoryComparison.Compare(File("a"), File("a"), Options)
            .Should().Be(ComparisonStatus.Same);

    [Fact]
    public void Compare_NewerOnTheLeft_SaysSo()
    {
        var left = File("a", modified: new DateTime(2026, 7, 27, 13, 0, 0));
        var right = File("a", modified: new DateTime(2026, 7, 27, 12, 0, 0));

        DirectoryComparison.Compare(left, right, Options).Should().Be(ComparisonStatus.LeftNewer);
        DirectoryComparison.Compare(right, left, Options).Should().Be(ComparisonStatus.RightNewer);
    }

    [Fact]
    public void Compare_SameTimeDifferentSize_IsDifferentRatherThanNewer()
    {
        // Neither side can be called newer, so nothing gets copied without the user saying so.
        DirectoryComparison.Compare(File("a", 100), File("a", 200), Options)
            .Should().Be(ComparisonStatus.Different);
    }

    [Fact]
    public void Compare_TimestampsWithinTolerance_CountAsEqual()
    {
        // FAT stores two-second resolution; an exact comparison would flag every file.
        var left = File("a", modified: new DateTime(2026, 7, 27, 12, 0, 0));
        var right = File("a", modified: new DateTime(2026, 7, 27, 12, 0, 1));

        DirectoryComparison.Compare(left, right, Options).Should().Be(ComparisonStatus.Same);
    }

    [Fact]
    public void Compare_UtcServerTimeAgainstLocalDiskTime_ComparesTheInstants()
    {
        // An FTP machine listing is UTC, the disk is local: the same moment must be "same", not
        // hours apart in whichever direction the time zone happens to push it.
        var local = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Local);
        var server = local.ToUniversalTime();

        DirectoryComparison.Compare(File("a", modified: local), File("a", modified: server), Options)
            .Should().Be(ComparisonStatus.Same);
    }

    [Fact]
    public void Compare_NewerServerFileInUtc_IsNewerWhateverTheTimeZone()
    {
        var local = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Local);
        var serverNewer = local.ToUniversalTime().AddMinutes(30);

        DirectoryComparison.Compare(File("a", modified: local), File("a", modified: serverNewer), Options)
            .Should().Be(ComparisonStatus.RightNewer, "a sync must never push the older local copy over it");
    }

    [Fact]
    public void Compare_ToleranceCanBeWidened()
    {
        var left = File("a", modified: new DateTime(2026, 7, 27, 12, 0, 0));
        var right = File("a", modified: new DateTime(2026, 7, 27, 12, 0, 45));
        var lenient = new ComparisonOptions { TimeTolerance = TimeSpan.FromMinutes(1) };

        DirectoryComparison.Compare(left, right, Options).Should().Be(ComparisonStatus.RightNewer);
        DirectoryComparison.Compare(left, right, lenient).Should().Be(ComparisonStatus.Same);
    }

    [Fact]
    public void Compare_TwoDirectories_IsSameAtThisLevel() =>
        DirectoryComparison.Compare(Dir("src"), Dir("src"), Options)
            .Should().Be(ComparisonStatus.Same);

    [Fact]
    public void Compare_FileVersusDirectory_IsDifferent() =>
        DirectoryComparison.Compare(File("x"), Dir("x"), Options)
            .Should().Be(ComparisonStatus.Different);

    // ── Level comparison ──────────────────────────────────────────────────────

    [Fact]
    public void CompareLevel_ReportsWhatEachSideIsMissing()
    {
        var rows = DirectoryComparison.CompareLevel(
            [File("only-left"), File("both")],
            [File("only-right"), File("both")],
            Options);

        rows.Should().Contain(r => r.Name == "only-left" && r.Status == ComparisonStatus.OnlyLeft);
        rows.Should().Contain(r => r.Name == "only-right" && r.Status == ComparisonStatus.OnlyRight);
        // "both" is identical and identical rows are hidden by default.
        rows.Should().NotContain(r => r.Name == "both");
    }

    [Fact]
    public void CompareLevel_IncludeSame_KeepsIdenticalRows()
    {
        var rows = DirectoryComparison.CompareLevel(
            [File("both")], [File("both")], new ComparisonOptions { IncludeSame = true });

        rows.Should().ContainSingle().Which.Status.Should().Be(ComparisonStatus.Same);
    }

    [Fact]
    public void CompareLevel_MatchesNamesCaseInsensitively()
    {
        var rows = DirectoryComparison.CompareLevel(
            [File("ReadMe.md")], [File("readme.md")], new ComparisonOptions { IncludeSame = true });

        rows.Should().ContainSingle().Which.Status.Should().Be(ComparisonStatus.Same);
    }

    [Fact]
    public void CompareLevel_PrefixesRelativePaths()
    {
        var rows = DirectoryComparison.CompareLevel([File("a.cs")], [], Options, prefix: "src/app");

        rows[0].RelativePath.Should().Be("src/app/a.cs");
        rows[0].RelativeDirectory.Should().Be("src/app");
        rows[0].Name.Should().Be("a.cs");
    }

    [Fact]
    public void CompareLevel_PutsDirectoriesFirstThenSortsByName()
    {
        var rows = DirectoryComparison.CompareLevel(
            [File("z.cs"), Dir("beta"), File("a.cs"), Dir("alpha")], [], Options);

        rows.Select(r => r.Name).Should().BeEquivalentTo(
            ["alpha", "beta", "a.cs", "z.cs"], o => o.WithStrictOrdering());
    }

    // ── Default directions ────────────────────────────────────────────────────

    [Theory]
    [InlineData(ComparisonStatus.OnlyLeft, SyncDirection.ToRight)]
    [InlineData(ComparisonStatus.OnlyRight, SyncDirection.ToLeft)]
    [InlineData(ComparisonStatus.LeftNewer, SyncDirection.ToRight)]
    [InlineData(ComparisonStatus.RightNewer, SyncDirection.ToLeft)]
    [InlineData(ComparisonStatus.Different, SyncDirection.None)]
    [InlineData(ComparisonStatus.Same, SyncDirection.None)]
    public void DefaultDirection_FollowsTheVerdict(ComparisonStatus status, SyncDirection expected) =>
        new ComparisonRow("a", null, null, status, false).DefaultDirection.Should().Be(expected);

    [Fact]
    public void Summarize_CountsByStatus()
    {
        var rows = new[]
        {
            new ComparisonRow("a", null, null, ComparisonStatus.OnlyLeft, false),
            new ComparisonRow("b", null, null, ComparisonStatus.OnlyLeft, false),
            new ComparisonRow("c", null, null, ComparisonStatus.RightNewer, false),
        };

        var summary = DirectoryComparison.Summarize(rows);

        summary[ComparisonStatus.OnlyLeft].Should().Be(2);
        summary[ComparisonStatus.RightNewer].Should().Be(1);
        summary.GetValueOrDefault(ComparisonStatus.Same).Should().Be(0);
    }

    private sealed record StubEntry(string Name, bool IsDirectory, long Size, DateTime LastModified)
        : IFileEntry
    {
        public VfsPath Path => new("/" + Name);
        public FileAttributes Attributes =>
            IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
    }
}
