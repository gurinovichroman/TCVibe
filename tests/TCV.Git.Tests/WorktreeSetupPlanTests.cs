using FluentAssertions;

namespace TCV.Git.Tests;

public class WorktreeSetupPlanTests
{
    private static readonly string[] Paths =
    {
        ".env",
        ".env.local",
        "src/api/.env",
        "src/api/appsettings.Development.json",
        "config/db.local.json",
        "README.md",
        "src/Program.cs",
        "TCV.App.csproj.user",
    };

    [Fact]
    public void SelectCarryFiles_BarePattern_MatchesTheFileNameAtAnyDepth()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(Paths, new[] { ".env" });

        selected.Should().BeEquivalentTo(new[] { ".env", "src/api/.env" });
    }

    [Fact]
    public void SelectCarryFiles_PatternWithSeparator_StaysAnchoredToThePath()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(Paths, new[] { "config/*.json" });

        selected.Should().BeEquivalentTo(new[] { "config/db.local.json" });
    }

    [Fact]
    public void SelectCarryFiles_Wildcards_MatchAsGlobs()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(Paths, new[] { ".env.*", "*.local.json" });

        selected.Should().BeEquivalentTo(new[] { ".env.local", "config/db.local.json" });
    }

    [Fact]
    public void SelectCarryFiles_TwoPatternsMatchingOneFile_CopyItOnce()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(
            new[] { ".env.local" }, new[] { ".env.*", "*.local" });

        selected.Should().ContainSingle().Which.Should().Be(".env.local");
    }

    [Fact]
    public void SelectCarryFiles_DefaultPatterns_PickTheUsualSuspectsAndNothingElse()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(Paths, WorktreeSetupPlan.DefaultCarryPatterns);

        selected.Should().Contain(new[]
        {
            ".env", ".env.local", "src/api/.env",
            "src/api/appsettings.Development.json",
            "config/db.local.json",
            "TCV.App.csproj.user",
        });
        selected.Should().NotContain("README.md");
        selected.Should().NotContain("src/Program.cs");
    }

    [Fact]
    public void SelectCarryFiles_BackslashPaths_AreNormalised()
    {
        // Windows enumeration produces backslashes; patterns are written with forward ones.
        var selected = WorktreeSetupPlan.SelectCarryFiles(
            new[] { @"config\db.local.json" }, new[] { "config/*.json" });

        selected.Should().ContainSingle().Which.Should().Be("config/db.local.json");
    }

    [Fact]
    public void SelectCarryFiles_MatchingIsCaseInsensitive()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(
            new[] { "AppSettings.Development.JSON" },
            new[] { "appsettings.development.json" });

        selected.Should().ContainSingle();
    }

    [Fact]
    public void SelectCarryFiles_NullPatterns_SelectsNothing()
    {
        WorktreeSetupPlan.SelectCarryFiles(Paths, null).Should().BeEmpty();
    }

    [Fact]
    public void SelectCarryFiles_EmptyPatternList_SelectsNothing()
    {
        WorktreeSetupPlan.SelectCarryFiles(Paths, Array.Empty<string>()).Should().BeEmpty();
    }

    [Fact]
    public void SelectCarryFiles_NoPaths_SelectsNothing()
    {
        WorktreeSetupPlan.SelectCarryFiles(null, WorktreeSetupPlan.DefaultCarryPatterns).Should().BeEmpty();
    }

    [Fact]
    public void SelectCarryFiles_BlankEntries_AreIgnored()
    {
        var selected = WorktreeSetupPlan.SelectCarryFiles(
            new[] { "", "   ", ".env" }, new[] { "", ".env" });

        selected.Should().ContainSingle().Which.Should().Be(".env");
    }

    [Fact]
    public void SelectCarryFiles_StarSpansSeparators()
    {
        // A single '*' deliberately matches across directories: "src/*.cs" written by a user who
        // means "the sources" should not silently select nothing on a nested layout.
        var selected = WorktreeSetupPlan.SelectCarryFiles(
            new[] { "src/api/deep/.env" }, new[] { "src/*.env" });

        selected.Should().ContainSingle();
    }
}
