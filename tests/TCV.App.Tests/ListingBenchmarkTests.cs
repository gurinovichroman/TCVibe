using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.Core.Domain;
using Xunit.Abstractions;

namespace TCV.App.Tests;

/// <summary>
/// The automated half of task 6.25: the «open a directory of 10 000 files in under 500 ms»
/// budget from <c>docs/VISION.md</c>, turned into something that fails when it regresses.
/// Cold start is measured by <c>tools/benchmark.ps1</c> — a process launch is not something a unit
/// test can time honestly.
/// </summary>
/// <remarks>
/// Two assertions, because an absolute millisecond budget alone is either flaky or meaningless:
/// <list type="bullet">
///   <item>the <b>relative</b> one — our listing may cost at most a small multiple of what the OS
///   itself charges to enumerate the same directory. That is the number a regression moves. It is
///   asserted only when the machine is quiet enough to measure it: on a warm cache the OS walk
///   costs single-digit milliseconds, so the margin is tens of milliseconds and a contended runner
///   can blow through it with no regression at all. The run-to-run spread of the repeated
///   measurements is the yardstick for «quiet enough».</item>
///   <item>the <b>absolute</b> 500 ms is asserted only when the machine's own enumeration is fast
///   enough for the product requirement to be reachable at all; otherwise the measurement is
///   reported and the budget is left to the benchmark script on real hardware.</item>
/// </list>
/// Both gates report what they skipped and why — a silent pass on a noisy box would read as a
/// clean run.
/// </remarks>
public class ListingBenchmarkTests : IDisposable
{
    private const int FileCount = 10_000;

    /// <summary>docs/VISION.md budget — opening a directory of 10 000 files.</summary>
    private const int BudgetMs = 500;

    /// <summary>
    /// Above this, the disk alone eats the budget: on such a machine the absolute assertion would
    /// be testing the hardware, not this code.
    /// </summary>
    private const int BaselineCeilingMs = 125;

    /// <summary>How much of a markup over the OS enumeration our listing may cost. It builds a
    /// <c>FileEntry</c> per row on top of the same <c>FindFirstFile</c> walk, so the honest number is
    /// small — measured at ×2.5 with the baseline below.</summary>
    private const double MaxOverheadFactor = 2.5;

    /// <summary>
    /// Scheduling noise the relative budget absorbs on top of the markup — and, by the same token,
    /// the most run-to-run jitter the baseline may show before the measurement is worth nothing:
    /// if repeating the identical OS walk already swings by more than this, so can our listing, and
    /// a failure would say «the runner was busy», not «the listing got slower».
    /// </summary>
    private const int NoiseAllowanceMs = 50;

    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-bench-" + Guid.NewGuid().ToString("N"));

    public ListingBenchmarkTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
        // Empty files: the requirement is about enumerating a directory, not about reading it.
        for (int i = 0; i < FileCount; i++)
            File.WriteAllBytes(Path.Combine(_dir, $"file-{i:D5}.txt"), []);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Listing_10_000_files_stays_within_the_product_budget()
    {
        var fs = new WindowsFileSystem(NullLogger<WindowsFileSystem>.Instance);
        var path = new VfsPath(_dir);

        // Warm-up: the first pass pays for the directory metadata cache, and no user meets a
        // stone-cold cache twice in a row on the folder they are working in.
        (await fs.ListAsync(path)).Should().HaveCount(FileCount);
        _ = Baseline();

        var baselineSamples = SamplesMs(() => Baseline());
        var listingSamples = SamplesMs(() => fs.ListAsync(path).GetAwaiter().GetResult());

        var baseline = Median(baselineSamples);
        var listing = Median(listingSamples);
        // Spread of each series (sorted: slowest − fastest). A regression is uniform — every run
        // pays it — so a wide spread is the machine talking, whichever series it shows up in.
        var jitter = Math.Max(Spread(baselineSamples), Spread(listingSamples));
        var allowance = (baseline * MaxOverheadFactor) + NoiseAllowanceMs;

        _output.WriteLine(
            $"listing {FileCount} entries: {listing:F1} ms · OS baseline {baseline:F1} ms · " +
            $"jitter {jitter:F1} ms · overhead ×{listing / Math.Max(baseline, 0.1):F2} · " +
            $"allowance {allowance:F1} ms · budget {BudgetMs} ms");

        if (jitter <= NoiseAllowanceMs)
        {
            listing.Should().BeLessThan(allowance,
                "the listing may only cost a small markup over what the OS charges for the same walk");
        }
        else
        {
            _output.WriteLine(
                $"relative budget not asserted: repeats of the same work swung by {jitter:F1} ms " +
                $"(> {NoiseAllowanceMs} ms), so this machine is too busy to tell a regression from noise");
        }

        if (baseline <= BaselineCeilingMs)
        {
            listing.Should().BeLessThan(BudgetMs,
                "docs/VISION.md requires a 10 000-file directory to open in under 500 ms");
        }
        else
        {
            _output.WriteLine(
                $"absolute budget not asserted: the OS itself needed {baseline:F1} ms " +
                $"(> {BaselineCeilingMs} ms), so this machine cannot meet it — run tools/benchmark.ps1 instead");
        }
    }

    /// <summary>
    /// The OS's own price for the same work: names <b>plus</b> attributes, size and timestamps.
    /// <c>GetFileSystemEntries</c> would be the cheaper and flattering comparison — it returns bare
    /// names, which is not what a file panel needs.
    /// </summary>
    private FileSystemInfo[] Baseline() => new DirectoryInfo(_dir).GetFileSystemInfos();

    /// <summary>
    /// Three timed runs, sorted. The caller takes the median — one slow run (a background indexer,
    /// a GC) must not decide the verdict — and reads the spread as how noisy the machine is.
    /// </summary>
    private static List<double> SamplesMs(Action action)
    {
        var samples = new List<double>();
        for (int i = 0; i < 3; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return samples;
    }

    private static double Median(List<double> sortedSamples) => sortedSamples[sortedSamples.Count / 2];

    private static double Spread(List<double> sortedSamples) => sortedSamples[^1] - sortedSamples[0];
}
