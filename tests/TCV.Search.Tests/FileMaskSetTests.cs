using FluentAssertions;
using TCV.Search;

namespace TCV.Search.Tests;

public class FileMaskSetTests
{
    [Fact]
    public void Parse_Blank_MatchesEverything()
    {
        FileMaskSet.Parse(null).Matches("anything.cs").Should().BeTrue();
        FileMaskSet.Parse("   ").IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("*.cs;*.md", "a.cs", true)]
    [InlineData("*.cs;*.md", "a.md", true)]
    [InlineData("*.cs;*.md", "a.txt", false)]
    [InlineData("*.cs, *.md", "a.md", true)]
    [InlineData("*.cs\n*.md", "a.md", true)]
    public void Matches_SeveralMasks_MatchesAnyOfThem(string masks, string name, bool expected) =>
        FileMaskSet.Parse(masks).Matches(name).Should().Be(expected);

    [Fact]
    public void Parse_SpaceIsNotASeparator_SoNamesWithSpacesStayOneMask()
    {
        var set = FileMaskSet.Parse("my report*");
        set.Matches("my report 2026.docx").Should().BeTrue();
        set.Matches("report.docx").Should().BeFalse();
    }

    [Fact]
    public void Matches_StarDotStar_AlsoMatchesExtensionlessNames()
    {
        var set = FileMaskSet.Parse("*.*");
        set.Matches("README").Should().BeTrue();
        set.Matches("a.cs").Should().BeTrue();
    }

    [Fact]
    public void Matches_QuestionMark_StandsForOneCharacter()
    {
        var set = FileMaskSet.Parse("file?.txt");
        set.Matches("file1.txt").Should().BeTrue();
        set.Matches("file10.txt").Should().BeFalse();
    }

    [Fact]
    public void Matches_CaseInsensitiveByDefault_AndCaseSensitiveOnRequest()
    {
        FileMaskSet.Parse("*.CS").Matches("a.cs").Should().BeTrue();
        FileMaskSet.Parse("*.CS", caseSensitive: true).Matches("a.cs").Should().BeFalse();
    }

    [Fact]
    public void Matches_DotsAreLiteral_NotRegexWildcards()
    {
        FileMaskSet.Parse("a.cs").Matches("axcs").Should().BeFalse();
    }
}
