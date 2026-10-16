using FluentAssertions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.PluginSdk;

namespace TCV.App.Tests;

/// <summary>
/// F5/F6 into an open archive: names already in it are settled by the copy dialog's choice and "only
/// if newer", file by file, before the one rewrite (6.55).
/// </summary>
public sealed class ArchiveAddPlannerTests
{
    private static readonly DateTime Stamp = new(2025, 3, 1, 12, 0, 0);

    private static ArchiveEntry InArchive(string path, DateTime? modified = null) =>
        new(path, path[(path.LastIndexOf('/') + 1)..], false, 3, modified ?? Stamp);

    private static readonly IReadOnlyList<ArchiveEntry> Existing =
    [
        new("docs", "docs", true, 0, Stamp),
        InArchive("docs/a.txt"),
        InArchive("docs/a (1).txt"),
        InArchive("top.txt"),
    ];

    /// <summary>Two selected items: «docs» (owner 0, two files) and «b.txt» (owner 1).</summary>
    private static readonly IReadOnlyList<ArchiveAddFile> Files =
    [
        new(@"C:\src\docs\a.txt", "docs/a.txt", 0),
        new(@"C:\src\docs\new.txt", "docs/new.txt", 0),
        new(@"C:\src\b.txt", "b.txt", 1),
    ];

    private static (long, DateTime) LocalAt(DateTime modified) => (3, modified);

    private static Task<ArchiveAddPlan> PlanAsync(
        IConflictStrategy conflicts, bool onlyIfNewer = false, DateTime? local = null,
        IReadOnlyList<ArchiveAddFile>? files = null, IReadOnlyList<ArchiveEntry>? existing = null) =>
        ArchiveAddPlanner.PlanAsync(files ?? Files, existing ?? Existing, conflicts, onlyIfNewer,
            _ => LocalAt(local ?? Stamp.AddDays(1)), CancellationToken.None);

    private sealed class Counting(ConflictResolution answer) : IConflictStrategy
    {
        public List<string> Asked { get; } = [];

        public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct)
        {
            Asked.Add(conflict.Destination.Value);
            return Task.FromResult(answer);
        }
    }

    [Fact]
    public async Task PlanAsync_NothingTaken_AddsEverythingWithoutAsking()
    {
        var strategy = new Counting(ConflictResolution.Cancel);
        ArchiveAddFile[] files = [new(@"C:\src\b.txt", "b.txt", 0), new(string.Empty, "empty/", 0)];

        var plan = await PlanAsync(strategy, files: files);

        strategy.Asked.Should().BeEmpty();
        plan.Files.Should().Equal((@"C:\src\b.txt", "b.txt"), (string.Empty, "empty/"));
        plan.Skipped.Should().Be(0);
        plan.IncompleteOwners.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_Skip_LeavesTheTakenFileOutAndItsItemIncomplete()
    {
        var strategy = new Counting(ConflictResolution.Skip);

        var plan = await PlanAsync(strategy);

        strategy.Asked.Should().Equal("/docs/a.txt");
        plan.Files.Select(f => f.entryName).Should().Equal("docs/new.txt", "b.txt");
        plan.Skipped.Should().Be(1);
        plan.IncompleteOwners.Should().Equal(0);
    }

    [Fact]
    public async Task PlanAsync_Overwrite_KeepsTheName()
    {
        var plan = await PlanAsync(new OverwriteStrategy());

        plan.Files.Select(f => f.entryName).Should().Equal("docs/a.txt", "docs/new.txt", "b.txt");
        plan.IncompleteOwners.Should().BeEmpty();
    }

    [Fact]
    public async Task PlanAsync_Rename_TakesAFreeNameInTheSameArchiveFolder()
    {
        var plan = await PlanAsync(new RenameAutoStrategy());

        // «a (1).txt» is in the archive already.
        plan.Files.Select(f => f.entryName).Should().Equal("docs/a (2).txt", "docs/new.txt", "b.txt");
        plan.Skipped.Should().Be(0);
    }

    [Fact]
    public async Task PlanAsync_Cancel_Throws()
    {
        var act = () => PlanAsync(new Counting(ConflictResolution.Cancel));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PlanAsync_NameDiffersOnlyByCase_IsTaken()
    {
        var strategy = new Counting(ConflictResolution.Skip);

        var plan = await PlanAsync(strategy, files: [new(@"C:\src\Top.TXT", "TOP.txt", 0)]);

        strategy.Asked.Should().ContainSingle();
        plan.Files.Should().BeEmpty();
    }

    [Theory]
    [InlineData(60, true)]     // newer: replaced without asking
    [InlineData(1, false)]     // within the two seconds a ZIP rounds to: the same file
    [InlineData(0, false)]
    [InlineData(-60, false)]   // older: left alone
    public async Task PlanAsync_OnlyIfNewer_ReplacesOnlyWhatIsNewerWithoutAsking(int seconds, bool replaced)
    {
        var strategy = new Counting(ConflictResolution.Cancel);

        var plan = await PlanAsync(strategy, onlyIfNewer: true, local: Stamp.AddSeconds(seconds),
            files: [new(@"C:\src\docs\a.txt", "docs/a.txt", 0)]);

        strategy.Asked.Should().BeEmpty();
        plan.Files.Any().Should().Be(replaced);
        plan.IncompleteOwners.Should().HaveCount(replaced ? 0 : 1);
    }
}
