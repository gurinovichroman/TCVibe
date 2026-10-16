using FluentAssertions;
using TCV.Search;

namespace TCV.Search.Tests;

public class SearchPresetTests
{
    [Fact]
    public void ToCriteria_RoundTripsEveryKnob()
    {
        var original = new SearchCriteria
        {
            NamePattern = "*.cs;*.md",
            NameIsRegex = false,
            CaseSensitive = true,
            ContentText = "TODO",
            ContentIsRegex = true,
            WholeWord = true,
            ContentCodePage = 1251,
            SkipBinary = false,
            ContextLines = 3,
            ExcludePatterns = "bin/;obj/",
            RespectGitignore = false,
            IncludeHidden = true,
            IncludeSystem = true,
            FilesOnly = true,
            MinSizeBytes = 10,
            MaxSizeBytes = 2048,
        };

        var restored = SearchPreset.FromCriteria("p", original).ToCriteria();

        restored.Should().BeEquivalentTo(original, o => o
            .Including(c => c.NamePattern)
            .Including(c => c.NameIsRegex)
            .Including(c => c.CaseSensitive)
            .Including(c => c.ContentText)
            .Including(c => c.ContentIsRegex)
            .Including(c => c.WholeWord)
            .Including(c => c.ContentCodePage)
            .Including(c => c.SkipBinary)
            .Including(c => c.ContextLines)
            .Including(c => c.ExcludePatterns)
            .Including(c => c.RespectGitignore)
            .Including(c => c.IncludeHidden)
            .Including(c => c.IncludeSystem)
            .Including(c => c.FilesOnly)
            .Including(c => c.MinSizeBytes)
            .Including(c => c.MaxSizeBytes));
    }

    [Fact]
    public void ToCriteria_ZeroSizeBounds_MeanNoBound()
    {
        var criteria = new SearchPreset { MinSizeBytes = 0, MaxSizeBytes = 0 }.ToCriteria();

        criteria.MinSizeBytes.Should().BeNull();
        criteria.MaxSizeBytes.Should().BeNull();
    }

    [Fact]
    public void ToCriteria_EmptyContent_LeavesContentSearchOff()
    {
        new SearchPreset { ContentText = string.Empty }.ToCriteria().ContentText.Should().BeNull();
    }

    [Fact]
    public void Builtin_PresetsAreUsable()
    {
        SearchPreset.Builtin.Should().NotBeEmpty();
        foreach (var preset in SearchPreset.Builtin)
        {
            preset.Name.Should().NotBeNullOrWhiteSpace();
            // A preset that throws on Compile would blow up the moment it is selected.
            var act = () => preset.ToCriteria().Compile();
            act.Should().NotThrow();
        }
    }
}
