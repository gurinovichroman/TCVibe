using System.IO;
using System.Text;
using FluentAssertions;
using TCV.App.Services;
using TCV.App.ViewModels;
using TCV.Search;

namespace TCV.App.Tests;

/// <summary>The search window's date range: parsing, whole-day bounds, presets through the form (6.55).</summary>
public sealed class SearchDateFilterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-searchdate-" + Guid.NewGuid().ToString("N"));

    public SearchDateFilterTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SearchDialogViewModel Create(InMemoryFileSystem? vfs = null) =>
        new(vfs ?? Project(), "/proj", archivePlugin: null,
            presetStore: new SearchPresetStore(Path.Combine(_dir, "presets.json")));

    private static InMemoryFileSystem Project()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/proj");
        fs.AddFile("/proj/before.txt", Encoding.UTF8.GetBytes("x"), new DateTime(2026, 2, 28, 23, 0, 0));
        fs.AddFile("/proj/first-day.txt", Encoding.UTF8.GetBytes("x"), new DateTime(2026, 3, 1, 0, 0, 0));
        fs.AddFile("/proj/last-day-noon.txt", Encoding.UTF8.GetBytes("x"), new DateTime(2026, 3, 4, 12, 0, 0));
        fs.AddFile("/proj/after.txt", Encoding.UTF8.GetBytes("x"), new DateTime(2026, 3, 5, 0, 0, 0));
        return fs;
    }

    // ── DateParser ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("2026-10-01")]
    [InlineData("2026-10-1")]
    [InlineData("01.10.2026")]
    [InlineData("1.10.2026")]
    [InlineData("  2026-10-01  ")]
    public void TryParse_DayInASupportedForm_IsThatDay(string text)
    {
        DateParser.TryParse(text, out var day).Should().BeTrue();
        day.Should().Be(new DateTime(2026, 10, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryParse_Blank_IsNoBound(string? text)
    {
        DateParser.TryParse(text, out var day).Should().BeTrue();
        day.Should().BeNull();
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("2026-13-01")]
    [InlineData("31.02.2026")]
    [InlineData("10MB")]
    public void TryParse_NotADate_IsAnError(string text)
    {
        DateParser.TryParse(text, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_DateWithATime_KeepsTheDayOnly()
    {
        DateParser.TryParse("2026-10-01", out var day).Should().BeTrue();
        day!.Value.TimeOfDay.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Format_IsIsoAndRoundTrips()
    {
        var text = DateParser.Format(new DateTime(2026, 3, 4, 23, 59, 59));

        text.Should().Be("2026-03-04");
        DateParser.TryParse(text, out var back).Should().BeTrue();
        back.Should().Be(new DateTime(2026, 3, 4));
        DateParser.Format(null).Should().BeEmpty();
    }

    [Fact]
    public void EndOfDay_IsTheLastInstantOfThatDay()
    {
        DateParser.EndOfDay(new DateTime(2026, 3, 4)).Should().Be(new DateTime(2026, 3, 5).AddTicks(-1));
        DateParser.EndOfDay(null).Should().BeNull();
        DateParser.EndOfDay(DateTime.MaxValue.Date).Should().Be(DateTime.MaxValue, "the last day must not overflow");
    }

    // ── The form ──────────────────────────────────────────────────────────────

    [Fact]
    public void BuildCriteria_TypedRange_RunsFromTheFirstDaysStartToTheLastDaysEnd()
    {
        var vm = Create();
        vm.ModifiedAfterText = "2026-03-01";
        vm.ModifiedBeforeText = "04.03.2026";

        var criteria = vm.BuildCriteria();

        criteria.ModifiedAfter.Should().Be(new DateTime(2026, 3, 1));
        criteria.ModifiedBefore.Should().Be(new DateTime(2026, 3, 5).AddTicks(-1));
    }

    [Fact]
    public async Task Start_DateRange_FindsTheFilesOfBothBoundaryDaysAndNothingOutside()
    {
        var vm = Create();
        vm.ModifiedAfterText = "2026-03-01";
        vm.ModifiedBeforeText = "2026-03-04";

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeFalse();
        vm.Results.Select(r => r.Name).Should().BeEquivalentTo(["first-day.txt", "last-day-noon.txt"],
            "a file saved at noon on the last day is inside a range that ends on that day");
    }

    [Fact]
    public async Task Start_TextThatIsNotADate_IsReportedAndNothingRuns()
    {
        var vm = Create();
        vm.ModifiedAfterText = "last week";

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Search_BadDate");
        vm.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task Start_StartAfterEnd_IsReported()
    {
        var vm = Create();
        vm.ModifiedAfterText = "2026-03-05";
        vm.ModifiedBeforeText = "2026-03-01";

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Search_DateRange");
    }

    [Fact]
    public void ModifiedAfter_FollowsTheText()
    {
        var vm = Create();
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.ModifiedAfterText = "2026-03-01";

        vm.ModifiedAfter.Should().Be(new DateTime(2026, 3, 1));
        changed.Should().Contain(nameof(SearchDialogViewModel.ModifiedAfter));
        vm.ModifiedAfterText = "nonsense";
        vm.ModifiedAfter.Should().BeNull();
    }

    // ── Presets through the form ──────────────────────────────────────────────

    [Fact]
    public void Preset_RangeTypedInTheForm_ComesBackIntoTheForm()
    {
        var vm = Create();
        vm.ModifiedAfterText = "01.03.2026";
        vm.ModifiedBeforeText = "2026-03-04";
        var saved = vm.BuildCriteria();

        vm.SavePreset("march").Should().BeTrue();
        vm.SelectedPreset = vm.Presets.First(p => p.Name != "march");
        vm.ModifiedAfterText = string.Empty;
        vm.ModifiedBeforeText = string.Empty;
        vm.SelectedPreset = vm.Presets.First(p => p.Name == "march");

        vm.ModifiedAfterText.Should().Be("2026-03-01");
        vm.ModifiedBeforeText.Should().Be("2026-03-04", "the stored end-of-day instant shows as the day it ends");
        var restored = vm.BuildCriteria();
        restored.ModifiedAfter.Should().Be(saved.ModifiedAfter);
        restored.ModifiedBefore.Should().Be(saved.ModifiedBefore);
    }

    [Fact]
    public void ApplyPreset_BoundWithATimeOfDay_ShowsItsDay()
    {
        var vm = Create();

        vm.ApplyPreset(new SearchPreset
        {
            Name = "hand-written",
            ModifiedAfter = new DateTime(2026, 3, 1, 15, 30, 0),
            ModifiedBefore = new DateTime(2026, 3, 4, 8, 0, 0),
        });

        vm.ModifiedAfterText.Should().Be("2026-03-01");
        vm.ModifiedBeforeText.Should().Be("2026-03-04");
    }

    [Fact]
    public void ApplyPreset_WithoutDates_ClearsTheFields()
    {
        var vm = Create();
        vm.ModifiedAfterText = "2026-03-01";
        vm.ModifiedBeforeText = "2026-03-04";

        vm.ApplyPreset(new SearchPreset { Name = "plain" });

        vm.ModifiedAfterText.Should().BeEmpty();
        vm.ModifiedBeforeText.Should().BeEmpty();
    }
}
