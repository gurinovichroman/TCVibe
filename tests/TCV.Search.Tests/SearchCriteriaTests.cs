using FluentAssertions;
using TCV.Search;

namespace TCV.Search.Tests;

public class SearchCriteriaTests
{
    // A non-regex pattern is a mask list (6.17), so the name filter is asked through
    // NameMatches rather than through the single compiled regex a regex pattern still produces.

    [Fact]
    public void Compile_WildcardPattern_MatchesByMask()
    {
        var c = new SearchCriteria { NamePattern = "*.txt" };
        c.Compile();
        c.NameMasks.IsEmpty.Should().BeFalse();
        c.NameMatches("readme.txt").Should().BeTrue();
        c.NameMatches("readme.md").Should().BeFalse();
    }

    [Fact]
    public void Compile_QuestionMarkWildcard_MatchesSingleChar()
    {
        var c = new SearchCriteria { NamePattern = "file?.txt" };
        c.Compile();
        c.NameMatches("file1.txt").Should().BeTrue();
        c.NameMatches("file10.txt").Should().BeFalse();
    }

    [Fact]
    public void Compile_RegexPattern_UsesPatternAsIs()
    {
        var c = new SearchCriteria { NamePattern = @"\d{3}\.txt", NameIsRegex = true };
        c.Compile();
        c.CompiledNameRegex!.IsMatch("123.txt").Should().BeTrue();
        c.CompiledNameRegex.IsMatch("abc.txt").Should().BeFalse();
    }

    [Fact]
    public void Compile_ContentPattern_CreatesContentRegex()
    {
        var c = new SearchCriteria { ContentText = "hello world" };
        c.Compile();
        c.CompiledContentRegex.Should().NotBeNull();
        c.CompiledContentRegex!.IsMatch("say hello world now").Should().BeTrue();
        c.CompiledContentRegex.IsMatch("goodbye").Should().BeFalse();
    }

    [Fact]
    public void Compile_CaseSensitiveOff_MatchesCaseInsensitive()
    {
        var c = new SearchCriteria { NamePattern = "*.TXT", CaseSensitive = false };
        c.Compile();
        c.NameMatches("readme.txt").Should().BeTrue();
        c.NameMatches("README.TXT").Should().BeTrue();
    }

    [Fact]
    public void Compile_NullPatterns_LeavesRegexNull()
    {
        var c = new SearchCriteria();
        c.Compile();
        c.CompiledNameRegex.Should().BeNull();
        c.CompiledContentRegex.Should().BeNull();
        c.NameMasks.IsEmpty.Should().BeTrue();
        c.NameMatches("anything").Should().BeTrue();
    }

    [Fact]
    public void Compile_WholeWord_DoesNotBreakSymbolPatterns()
    {
        // \b before "(" never matches, so the guard must not be applied there — otherwise
        // whole-word search would silently return nothing for punctuation patterns.
        var c = new SearchCriteria { ContentText = "(x)", WholeWord = true };
        c.Compile();
        c.CompiledContentRegex!.IsMatch("f(x);").Should().BeTrue();
    }

    [Fact]
    public void Compile_Recompile_ClearsThePreviousNameFilter()
    {
        var c = new SearchCriteria { NamePattern = "*.txt" };
        c.Compile();

        c.NamePattern = null;
        c.Compile();

        c.NameMatches("readme.md").Should().BeTrue();
    }
}
