using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.Core.Domain;
using Xunit.Abstractions;

namespace TCV.App.Tests;

/// <summary>What the listing may cost: the rule, apart from the timing, so that it can be tested.</summary>
internal static class ListingBudget
{
    /// <summary>docs/VISION.md budget — opening a directory of 10 000 files.</summary>
    public const int BudgetMs = 500;

    /// <summary>
    /// OS walks in one run of the yardstick, which makes it about as long as a listing. It has to be:
    /// load does not stretch short work and long work alike. One walk (15 ms) can fit in a single
    /// scheduler quantum and costs 13 ms under any load, while the listing (50 ms and a collection) is
    /// preempted, so the ratio to one walk went from ×4 on a quiet machine to ×15 under CPU load. To four
    /// walks it stayed between ×0.4 and ×1.1 at every load tried.
    /// </summary>
    public const int WalksPerYardstick = 4;

    /// <summary>
    /// How much longer than the yardstick the listing may take: above the ×1.1 ever measured, while
    /// a listing grown by ×2.3 from its usual ×0.65 no longer fits, let alone one ten times slower.
    /// </summary>
    public const double MaxTimesYardstick = 1.5;

    /// <summary>
    /// Above this per walk, the disk alone eats the budget: on such a machine the absolute assertion
    /// would be testing the hardware, not this code.
    /// </summary>
    public const int WalkCeilingMs = 125;

    public static double RelativeAllowance(double yardstickMs) => yardstickMs * MaxTimesYardstick;

    public static bool AbsoluteBudgetApplies(double yardstickMs) => yardstickMs / WalksPerYardstick <= WalkCeilingMs;

    public static bool Fits(double yardstickMs, double listingMs) =>
        listingMs < RelativeAllowance(yardstickMs)
        && (!AbsoluteBudgetApplies(yardstickMs) || listingMs < BudgetMs);
}

/// <summary>
/// The benchmark runs by itself, after the parallel tests of this assembly: a listing goes through the
/// thread pool and pays for collections, so tests running beside it in the same process charge it for
/// their own work. Its 10 000 files, in turn, do not get in the way of theirs.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ListingBenchmarkCollection
{
    public const string Name = "Listing benchmark";
}

/// <summary>A directory of 10 000 empty files: the requirement is about enumerating it, not reading it.</summary>
/// <remarks>Creating them takes 6 s on a quiet machine and 20 s on a busy one, and none of it is timed.</remarks>
public sealed class TenThousandFiles : IDisposable
{
    public const int Count = 10_000;

    public TenThousandFiles()
    {
        Directory.CreateDirectory(Dir);
        for (var i = 0; i < Count; i++)
        {
            File.WriteAllBytes(Path.Combine(Dir, $"file-{i:D5}.txt"), []);
        }
    }

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "tcv-bench-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup.
        }
    }
}

/// <summary>
/// The automated half of task 6.25: the «open a directory of 10 000 files in under 500 ms»
/// budget from <c>docs/VISION.md</c>, turned into something that fails when it regresses.
/// Cold start is measured by <c>tools/benchmark.ps1</c> — a process launch is not something a unit
/// test can time honestly.
/// </summary>
/// <remarks>
/// Two assertions, because an absolute millisecond budget alone is either flaky or meaningless:
/// the <b>relative</b> one, against a yardstick of the OS walking the same directory, is the number a
/// regression moves; the <b>absolute</b> 500 ms is asserted when the machine's own walk is fast enough
/// for it to be reachable at all, and otherwise reported, leaving it to the benchmark script on real
/// hardware. How the two are timed so that neither depends on what else the machine is doing:
/// <see cref="PairedBenchmark"/>.
/// </remarks>
[Collection(ListingBenchmarkCollection.Name)]
public sealed class ListingBenchmarkTests(TenThousandFiles files, ITestOutputHelper output) : IClassFixture<TenThousandFiles>
{
    /// <summary>Rounds a verdict rests on.</summary>
    private const int Window = 5;

    /// <summary>
    /// With the pauses, several seconds of giving a burst the time to pass. Only a run that keeps
    /// failing gets that far: a machine under steady load stretches the yardstick as well, and fits.
    /// </summary>
    private const int MaxRounds = 30;

    private const int PauseBetweenRoundsMs = 200;

    private readonly WindowsFileSystem _fs = new(NullLogger<WindowsFileSystem>.Instance);
    private readonly VfsPath _path = new(files.Dir);

    [Fact]
    public async Task Listing_10_000_files_stays_within_the_product_budget()
    {
        // Something that lists nothing would be very fast.
        (await _fs.ListAsync(_path)).Should().HaveCount(TenThousandFiles.Count);

        var result = PairedBenchmark.Run(
            Yardstick, () => TimeMs(ListOnce), ListingBudget.Fits, Window, MaxRounds,
            () => Thread.Sleep(PauseBetweenRoundsMs));

        output.WriteLine(
            $"listing {TenThousandFiles.Count} entries: {result.SubjectMs:F1} ms · yardstick of " +
            $"{ListingBudget.WalksPerYardstick} OS walks: {result.YardstickMs:F1} ms · " +
            $"×{result.SubjectMs / result.YardstickMs:F2} (may be ×{ListingBudget.MaxTimesYardstick}) · " +
            $"budget {ListingBudget.BudgetMs} ms · {result.Rounds} rounds");

        result.SubjectMs.Should().BeLessThan(ListingBudget.RelativeAllowance(result.YardstickMs),
            "the listing may only cost a small markup over what the OS charges for the same walk");

        if (ListingBudget.AbsoluteBudgetApplies(result.YardstickMs))
        {
            result.SubjectMs.Should().BeLessThan(ListingBudget.BudgetMs,
                "docs/VISION.md requires a 10 000-file directory to open in under 500 ms");
        }
        else
        {
            output.WriteLine(
                $"absolute budget not asserted: the OS itself needed {result.YardstickMs / ListingBudget.WalksPerYardstick:F1} ms " +
                $"a walk (> {ListingBudget.WalkCeilingMs} ms), so this machine cannot meet it — run tools/benchmark.ps1 instead");
        }
    }

    [Fact]
    public void Run_ListingTenTimesSlower_DoesNotFit()
    {
        // The other half of a benchmark that does not fail under load: it still fails when it should,
        // on this machine, right now, with the same timers. One window is enough to know.
        var result = PairedBenchmark.Run(
            Yardstick,
            () => TimeMs(() =>
            {
                for (var i = 0; i < 10; i++)
                {
                    ListOnce();
                }
            }),
            ListingBudget.Fits, Window, maxRounds: Window);

        output.WriteLine($"ten listings: {result.SubjectMs:F1} ms · yardstick {result.YardstickMs:F1} ms");
        result.Fits.Should().BeFalse("a listing ten times slower is the regression this benchmark exists for");
    }

    private void ListOnce() => _fs.ListAsync(_path).GetAwaiter().GetResult();

    /// <summary>
    /// The OS's own price for the same work: names <b>plus</b> attributes, size and timestamps.
    /// <c>GetFileSystemEntries</c> would be the cheaper and flattering comparison — it returns bare
    /// names, which is not what a file panel needs.
    /// </summary>
    private double Yardstick() => TimeMs(() =>
    {
        for (var i = 0; i < ListingBudget.WalksPerYardstick; i++)
        {
            _ = new DirectoryInfo(files.Dir).GetFileSystemInfos();
        }
    });

    private static double TimeMs(Action action)
    {
        var sw = Stopwatch.StartNew();
        action();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }
}
