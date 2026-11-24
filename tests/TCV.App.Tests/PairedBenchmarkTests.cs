using FluentAssertions;

namespace TCV.App.Tests;

/// <summary>
/// TRAP-112, on numbers instead of a stopwatch: what load does to a measurement must not fail it, and
/// what a regression does must. The series are shaped after real runs of the listing benchmark.
/// </summary>
public sealed class PairedBenchmarkTests
{
    private const int Window = 5;
    private const int MaxRounds = 30;

    private static PairedMeasurement Measure(IEnumerable<double> yardstick, IEnumerable<double> listing)
    {
        return PairedBenchmark.Run(Series(yardstick), Series(listing), ListingBudget.Fits, Window, MaxRounds);
    }

    /// <summary>Hands out the series, then keeps repeating its last value.</summary>
    private static Func<double> Series(IEnumerable<double> values)
    {
        var list = values.ToList();
        var next = 0;
        return () => list[Math.Min(next++, list.Count - 1)];
    }

    [Fact]
    public void Run_BurstOnTheListingAlone_FitsOnceItIsOver()
    {
        // Timed one series after the other, this was the failure: a quiet baseline, a burst on the listing.
        var result = Measure([80], [600, 600, 600, 600, 600, 600, 60]);

        result.Fits.Should().BeTrue();
        result.Rounds.Should().Be(7);
        result.SubjectMs.Should().Be(60);
    }

    [Fact]
    public void Run_SteadyLoadStretchingBoth_FitsAtOnce()
    {
        // 24 CPU burners on 12 logical processors: everything takes five times as long.
        var result = Measure([340, 500, 420, 610, 380], [214, 338, 290, 410, 300]);

        result.Fits.Should().BeTrue();
        result.Rounds.Should().Be(Window);
    }

    [Fact]
    public void Run_BurstStartingInTheFirstRound_ItsWindowSlidesPast()
    {
        // The only quiet yardstick is the very first sample; the first window judges the loaded listing by it.
        var result = Measure([80, 600], [400]);

        result.Fits.Should().BeTrue();
        result.Rounds.Should().Be(Window + 1);
    }

    [Fact]
    public void Run_ListingTenTimesSlowerThroughTheNoise_NeverFits()
    {
        var result = Measure([80, 300, 90, 700, 85, 150], [600, 2000, 650, 900, 3000, 620]);

        result.Fits.Should().BeFalse();
        result.Rounds.Should().Be(MaxRounds);
    }

    [Fact]
    public void Run_SlowYardstickRoundsAlone_DoNotBuyARegressionAPass()
    {
        // One quiet yardstick in the window is enough to tell what the walk really costs.
        var slow = Enumerable.Range(0, MaxRounds).Select(i => i % Window == 0 ? 80.0 : 800.0);

        var result = Measure(slow, [600]);

        result.Fits.Should().BeFalse();
        result.YardstickMs.Should().Be(80);
    }

    [Fact]
    public void Run_FirstRoundLooksFine_StillMeasuresTheWholeWindow()
    {
        var rounds = 0;

        var result = PairedBenchmark.Run(
            () =>
            {
                rounds++;
                return 80;
            },
            () => 60,
            ListingBudget.Fits,
            Window,
            MaxRounds);

        result.Rounds.Should().Be(Window);
        rounds.Should().Be(Window);
    }

    [Fact]
    public void Run_WindowDoesNotFit_PausesBeforeTheNextRoundOnly()
    {
        var pauses = 0;

        PairedBenchmark.Run(Series([80]), Series([600, 600, 600, 600, 600, 600, 60]), ListingBudget.Fits, Window, MaxRounds, () => pauses++);

        pauses.Should().Be(2, "rounds six and seven are the ones that had to be measured again");
    }

    [Theory]
    [InlineData(80, 60, true)] // The usual picture: the listing takes ×0.75 of four walks.
    [InlineData(80, 119, true)]
    [InlineData(80, 120, false)] // ×1.5 is the limit.
    [InlineData(80, 600, false)] // Ten times slower.
    [InlineData(480, 510, false)] // Within ×1.5, but the machine is fast enough for 500 ms to count.
    [InlineData(600, 510, true)] // Over 125 ms a walk the absolute budget says nothing about the code.
    [InlineData(600, 900, false)] // The relative budget holds on any machine.
    public void Fits_YardstickAndListing_FollowsBothBudgets(double yardstick, double listing, bool expected)
    {
        ListingBudget.Fits(yardstick, listing).Should().Be(expected);
    }
}
