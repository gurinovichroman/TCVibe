using FluentAssertions;
using TCV.App.Controls;

namespace TCV.App.Tests;

/// <summary>One width for every Brief column, as in Total Commander, from the folder's longest name.</summary>
public class BriefColumnsTests
{
    private static double SevenPerChar(string name) => name.Length * 7d;

    [Fact]
    public void ItemWidth_ShortNames_IsTheNarrowestRow()
    {
        BriefColumns.ItemWidth(["a.txt", "b.cs"], SevenPerChar)
            .Should().Be(BriefColumns.MinRowWidth + BriefColumns.ItemChrome);
    }

    [Fact]
    public void ItemWidth_ANameWiderThanTheNarrowestRow_WidensEveryColumnToIt()
    {
        var name = new string('x', 25);   // 175 px of text

        BriefColumns.ItemWidth(["a.txt", name], SevenPerChar)
            .Should().Be(BriefColumns.IconWidth + 175 + 1 + BriefColumns.ItemChrome);
    }

    [Fact]
    public void ItemWidth_AVeryLongName_StopsAtTheWidestRow()
    {
        BriefColumns.ItemWidth([new string('x', 200)], SevenPerChar)
            .Should().Be(BriefColumns.MaxRowWidth + BriefColumns.ItemChrome);
    }

    [Fact]
    public void ItemWidth_EmptyFolder_IsTheNarrowestRow()
    {
        BriefColumns.ItemWidth([], SevenPerChar).Should().Be(BriefColumns.MinRowWidth + BriefColumns.ItemChrome);
    }

    [Fact]
    public void ItemWidth_ManyNames_MeasuresOnlyTheLongest()
    {
        var names = Enumerable.Range(0, 1000).Select(i => new string('x', i % 20 + 1)).ToList();
        var measured = new List<string>();

        BriefColumns.ItemWidth(names, n => { measured.Add(n); return n.Length; });

        measured.Should().HaveCount(BriefColumns.MeasuredNames);
        measured.Should().OnlyContain(n => n.Length == 20);
    }

    [Fact]
    public void ItemWidth_TheWidestNameIsNotTheLongest_StillWins()
    {
        // A proportional font: "WWWW" is wider than "iiiiii".
        static double Font(string name) => name.Sum(c => c == 'W' ? 40d : 3d);

        BriefColumns.ItemWidth(["iiiiii", "WWWW"], Font)
            .Should().Be(BriefColumns.IconWidth + 160 + 1 + BriefColumns.ItemChrome);
    }
}
