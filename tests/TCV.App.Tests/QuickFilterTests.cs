using FluentAssertions;
using TCV.App.Navigation;

namespace TCV.App.Tests;

/// <summary>What the panel's quick filter lets through, and how well each name answers it.</summary>
public class QuickFilterTests
{
    [Theory]
    [InlineData("WinDevCleaner.App.dll", QuickFilterMatch.Start)]
    [InlineData("windbg.exe", QuickFilterMatch.Start)]
    [InlineData("Microsoft.Windows.AI.dll", QuickFilterMatch.WordStart)]
    [InlineData("MainWindow.xbf", QuickFilterMatch.WordStart)]
    [InlineData("old-windows.txt", QuickFilterMatch.WordStart)]
    [InlineData("my_wind.txt", QuickFilterMatch.WordStart)]
    [InlineData("rewind.mp3", QuickFilterMatch.Anywhere)]
    [InlineData("readme.md", QuickFilterMatch.None)]
    public void Match_RanksTheNameByWhereTheTextIs(string name, QuickFilterMatch expected)
    {
        QuickFilter.Parse("wind").Match(name).Should().Be(expected);
    }

    [Fact]
    public void Match_TakesTheBestOfSeveralPlacesInOneName()
    {
        // "rewind" holds it inside a word, "Windows" at the start of one: the better place counts.
        QuickFilter.Parse("wind").Match("rewind.Windows.txt").Should().Be(QuickFilterMatch.WordStart);
    }

    [Fact]
    public void Match_IgnoresCase_AndKeepsASpaceAsPartOfTheName()
    {
        var filter = QuickFilter.Parse("MY REP");

        filter.Match("my report.docx").Should().Be(QuickFilterMatch.Start);
        filter.Matches("my-report.docx").Should().BeFalse();
    }

    [Fact]
    public void Parse_TrimsTheText_AndBlankLetsEverythingThrough()
    {
        QuickFilter.Parse("  src ").Text.Should().Be("src");
        QuickFilter.Parse("   ").IsEmpty.Should().BeTrue();
        QuickFilter.Parse(null).Matches("anything").Should().BeTrue();
    }

    [Fact]
    public void Matches_AMask_TakesTheWholeName()
    {
        var filter = QuickFilter.Parse("*.cs");

        filter.Matches("Program.cs").Should().BeTrue();
        filter.Matches("App.csproj").Should().BeFalse("a mask is the whole name, as in Select by mask");
        filter.Matches("notes.cs.txt").Should().BeFalse();
    }

    [Fact]
    public void Matches_AMask_TakesQuestionMarksAndSeveralMasks()
    {
        QuickFilter.Parse("v?.txt").Matches("v1.txt").Should().BeTrue();
        QuickFilter.Parse("v?.txt").Matches("v10.txt").Should().BeFalse();

        var several = QuickFilter.Parse("*.cs;*.xaml");
        several.Matches("MainWindow.xaml").Should().BeTrue();
        several.Matches("App.cs").Should().BeTrue();
        several.Matches("App.json").Should().BeFalse();
    }

    [Fact]
    public void Matches_PlainTextWithACommaOrSemicolon_IsOneFragmentOfAName()
    {
        QuickFilter.Parse("a,b").Matches("a,b.txt").Should().BeTrue();
        QuickFilter.Parse("a,b").Matches("a.txt").Should().BeFalse();
    }

    // ── What is highlighted (6.87) ────────────────────────────────────────────

    [Theory]
    [InlineData("windbg.exe", 0)]                 // the start
    [InlineData("rewind.Windows.txt", 7)]         // the word start wins over the earlier "rewind"
    [InlineData("MainWindow.xbf", 4)]             // a capital after a small letter
    [InlineData("rewind.mp3", 2)]                 // only inside a word: the first place
    public void MatchSpan_IsThePlaceThatEarnedTheRank(string name, int start)
    {
        QuickFilter.Parse("WIND").MatchSpan(name).Should().Be((start, 4));
    }

    [Theory]
    [InlineData("", "readme.md")]
    [InlineData("wind", "readme.md")]
    [InlineData("*.md", "readme.md")]             // a mask matches the whole name: nothing to single out
    public void MatchSpan_EmptyMaskOrNoMatch_IsNull(string query, string name)
    {
        QuickFilter.Parse(query).MatchSpan(name).Should().BeNull();
    }

    // ── ↑ ↓ in the box while it jumps (6.87) ──────────────────────────────────

    [Fact]
    public void NextMatch_StepsOverTheNamesThatDoNotMatch_AndStopsAtTheEnds()
    {
        string?[] names = [null, "a1.txt", "b.txt", "a2.txt", "c.txt", "a3.txt"];   // null: the ".." row
        var filter = QuickFilter.Parse("a");

        filter.NextMatch(names, 1, forward: true).Should().Be(3);
        filter.NextMatch(names, 3, forward: true).Should().Be(5);
        filter.NextMatch(names, 5, forward: true).Should().Be(-1);
        filter.NextMatch(names, 5, forward: false).Should().Be(3);
        filter.NextMatch(names, 1, forward: false).Should().Be(-1, "the parent row never matches");
        filter.NextMatch(names, -1, forward: true).Should().Be(1, "no cursor: the first match");
        QuickFilter.Empty.NextMatch(names, 1, forward: true).Should().Be(-1);
    }
}
