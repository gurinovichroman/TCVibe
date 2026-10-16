using FluentAssertions;
using TCV.App.Commands;

namespace TCV.App.Tests;

public class FuzzyMatcherTests
{
    [Theory]
    [InlineData("Copy", "")]
    [InlineData("Copy", "   ")]
    public void EmptyPattern_MatchesEverything_WithZeroScore(string text, string pattern)
    {
        FuzzyMatcher.TryMatch(text, pattern, out var score).Should().BeTrue();
        score.Should().Be(0);
    }

    [Theory]
    [InlineData("Copy", "cp")]        // subsequence
    [InlineData("Copy", "copy")]      // exact
    [InlineData("Copy", "COPY")]      // case-insensitive
    [InlineData("New folder", "nf")]  // across a separator
    [InlineData("Multi-Rename…", "mr")]
    public void Subsequence_Matches(string text, string pattern)
    {
        FuzzyMatcher.TryMatch(text, pattern, out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("Copy", "px")]        // 'x' absent
    [InlineData("Copy", "yc")]        // out of order
    [InlineData("Copy", "copyy")]     // longer than text
    [InlineData("", "a")]             // empty text
    public void NonSubsequence_DoesNotMatch(string text, string pattern)
    {
        FuzzyMatcher.TryMatch(text, pattern, out var score).Should().BeFalse();
        score.Should().Be(0);
    }

    [Fact]
    public void PrefixMatch_RanksAboveMidStringSubstring()
    {
        FuzzyMatcher.TryMatch("copy files", "cop", out var prefix);
        FuzzyMatcher.TryMatch("recopy item", "cop", out var substring);
        prefix.Should().BeGreaterThan(substring);
    }

    [Fact]
    public void Positions_PointAtMatchedCharacters()
    {
        FuzzyMatcher.TryMatch("Copy", "cy", out _, out var positions).Should().BeTrue();
        positions.Should().Equal(0, 3);
    }

    [Theory]
    [InlineData("Copy", "cp")]
    [InlineData("Copy", "COPY")]
    [InlineData("src/ViewModels/MainViewModel.cs", "mvm")]
    [InlineData("src/app.cs", "APP")]
    [InlineData("New folder", "nf")]
    [InlineData("Copy", "px")]
    [InlineData("Copy", "yc")]
    [InlineData("", "a")]
    [InlineData("readme.md", "  ")]
    public void TryMatchLower_ScoresExactlyLikeTryMatch(string text, string pattern)
    {
        // The picker ranks through the span overload, the palette through the string one — they must
        // not drift apart, or the same query would order the same files differently.
        var expected = FuzzyMatcher.TryMatch(text, pattern, out var expectedScore);

        var actual = FuzzyMatcher.TryMatchLower(
            text, text.ToLowerInvariant(), pattern.Trim().ToLowerInvariant(), out var actualScore);

        actual.Should().Be(expected);
        actualScore.Should().Be(expectedScore);
    }

    [Fact]
    public void TryMatchLower_ScoresASliceAsIfItWereItsOwnString()
    {
        // What a scoped picker search does: match the tail of a path without cutting a substring
        // out of it on every keystroke.
        const string path = "src/deep/app.cs";
        FuzzyMatcher.TryMatch("deep/app.cs", "app", out var expected);

        FuzzyMatcher.TryMatchLower(
            path.AsSpan(4), path.ToLowerInvariant().AsSpan(4), "app", out var actual).Should().BeTrue();

        actual.Should().Be(expected);
    }
}
