using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.App.Tests;

public class GitignoreRulesTests
{
    private static bool Ignored(string gitignore, string path, bool isDirectory = false) =>
        GitignoreRules.Parse(gitignore).Match(path, isDirectory) == true;

    [Fact]
    public void Parse_EmptyText_MatchesNothing()
    {
        GitignoreRules.Parse("").IsEmpty.Should().BeTrue();
        GitignoreRules.Parse("   \n\n").IsEmpty.Should().BeTrue();
    }

    [Fact]
    public void Parse_SkipsCommentsAndBlankLines()
    {
        GitignoreRules.Parse("# a comment\n\n   \n").IsEmpty.Should().BeTrue();
    }

    [Theory]
    [InlineData("app.log")]
    [InlineData("logs/app.log")]
    [InlineData("src/deep/nested/app.log")]
    public void Extension_MatchesAtAnyDepth(string path)
    {
        Ignored("*.log", path).Should().BeTrue();
    }

    [Fact]
    public void Extension_DoesNotMatchADifferentSuffix()
    {
        Ignored("*.log", "app.log.txt").Should().BeFalse();
    }

    [Fact]
    public void PlainName_MatchesAtAnyDepth_ButAnchoredNameOnlyAtTheRoot()
    {
        Ignored("build", "src/build").Should().BeTrue();
        Ignored("/build", "src/build").Should().BeFalse();
        Ignored("/build", "build").Should().BeTrue();
    }

    [Fact]
    public void MiddleSlash_AnchorsToTheGitignoreDirectory()
    {
        Ignored("src/generated", "src/generated").Should().BeTrue();
        Ignored("src/generated", "app/src/generated").Should().BeFalse();
    }

    [Fact]
    public void DirectoryOnlyPattern_IgnoresTheDirectoryAndItsContents()
    {
        Ignored("bin/", "bin", isDirectory: true).Should().BeTrue();
        Ignored("bin/", "bin/app.exe").Should().BeTrue();
    }

    [Fact]
    public void DirectoryOnlyPattern_LeavesAFileOfTheSameNameAlone()
    {
        // "bin/" must not hide a file called "bin" — that is the whole point of the trailing slash
        Ignored("bin/", "bin", isDirectory: false).Should().BeFalse();
    }

    [Fact]
    public void MatchedDirectory_AlsoCoversEverythingUnderneath()
    {
        Ignored("node_modules", "node_modules/pkg/index.js").Should().BeTrue();
    }

    [Fact]
    public void Negation_ReIncludesAPreviouslyIgnoredPath()
    {
        var rules = GitignoreRules.Parse("*.log\n!keep.log");

        rules.Match("app.log", false).Should().BeTrue();
        rules.Match("keep.log", false).Should().BeFalse("a ! rule re-includes it");
    }

    [Fact]
    public void LastMatchingPatternWins()
    {
        // Re-ignoring after a negation is the documented git order of precedence
        GitignoreRules.Parse("!keep.log\n*.log").Match("keep.log", false).Should().BeTrue();
    }

    [Fact]
    public void NoPatternApplies_YieldsNoOpinion()
    {
        GitignoreRules.Parse("*.log").Match("src/app.cs", false).Should().BeNull(
            "null lets the caller fall back to a parent directory's rules");
    }

    [Fact]
    public void QuestionMark_MatchesOneCharacterButNotASeparator()
    {
        Ignored("a?c.txt", "abc.txt").Should().BeTrue();
        Ignored("a?c.txt", "ac.txt").Should().BeFalse();
        Ignored("a?c", "a/c").Should().BeFalse();
    }

    [Fact]
    public void Star_DoesNotCrossDirectoryBoundaries()
    {
        Ignored("src/*.cs", "src/app.cs").Should().BeTrue();
        Ignored("src/*.cs", "src/deep/app.cs").Should().BeFalse();
    }

    [Fact]
    public void DoubleStar_CrossesAnyNumberOfDirectories()
    {
        Ignored("src/**/app.cs", "src/app.cs").Should().BeTrue();
        Ignored("src/**/app.cs", "src/a/b/app.cs").Should().BeTrue();
        Ignored("**/temp", "a/b/temp").Should().BeTrue();
        Ignored("logs/**", "logs/2026/app.log").Should().BeTrue();
    }

    [Fact]
    public void CharacterClass_IsSupported()
    {
        Ignored("file[0-9].txt", "file7.txt").Should().BeTrue();
        Ignored("file[0-9].txt", "filex.txt").Should().BeFalse();
        Ignored("file[!0-9].txt", "filex.txt").Should().BeTrue();
    }

    [Fact]
    public void MatchingIsCaseInsensitive_LikeGitOnWindows()
    {
        Ignored("*.LOG", "app.log").Should().BeTrue();
        Ignored("Bin/", "bin/app.exe").Should().BeTrue();
    }

    [Fact]
    public void EscapedHashAndBang_AreLiteralNames()
    {
        Ignored("\\#notacomment", "#notacomment").Should().BeTrue();
        Ignored("\\!bang", "!bang").Should().BeTrue();
    }

    [Fact]
    public void TrailingSpaces_AreIgnoredUnlessEscaped()
    {
        Ignored("app.log   ", "app.log").Should().BeTrue();
        Ignored("space\\ ", "space ").Should().BeTrue();
    }

    [Fact]
    public void CrLfLineEndings_ParseTheSameAsLf()
    {
        var rules = GitignoreRules.Parse("*.log\r\nbin/\r\n");

        rules.Match("app.log", false).Should().BeTrue();
        rules.Match("bin", true).Should().BeTrue();
    }
}

public class GitignoreScopeTests
{
    [Fact]
    public void EmptyScope_IgnoresNothing()
    {
        GitignoreScope.Empty.IsIgnored("anything/at/all.txt", false).Should().BeFalse();
    }

    [Fact]
    public void RootRules_ApplyToTheWholeTree()
    {
        var scope = GitignoreScope.Empty.Push("", GitignoreRules.Parse("*.log"));

        scope.IsIgnored("app.log", false).Should().BeTrue();
        scope.IsIgnored("src/deep/app.log", false).Should().BeTrue();
    }

    [Fact]
    public void NestedRules_AreRelativeToTheirOwnDirectory()
    {
        var scope = GitignoreScope.Empty.Push("src/app", GitignoreRules.Parse("/local.txt"));

        scope.IsIgnored("src/app/local.txt", false).Should().BeTrue();
        scope.IsIgnored("local.txt", false).Should().BeFalse("the rule is anchored to src/app");
    }

    [Fact]
    public void DeeperRules_OverrideShallowerOnes()
    {
        var scope = GitignoreScope.Empty
            .Push("", GitignoreRules.Parse("*.log"))
            .Push("src", GitignoreRules.Parse("!*.log"));

        scope.IsIgnored("app.log", false).Should().BeTrue();
        scope.IsIgnored("src/app.log", false).Should().BeFalse("the nested .gitignore re-includes it");
    }

    [Fact]
    public void ShallowerRules_StillApplyWhereTheDeeperOneIsSilent()
    {
        var scope = GitignoreScope.Empty
            .Push("", GitignoreRules.Parse("*.log"))
            .Push("src", GitignoreRules.Parse("*.tmp"));

        scope.IsIgnored("src/app.log", false).Should().BeTrue();
        scope.IsIgnored("src/app.tmp", false).Should().BeTrue();
    }

    [Fact]
    public void ReIncludedDirectory_DoesNotReIncludeFilesItsParentRulesStillIgnore()
    {
        var scope = GitignoreScope.Empty.Push("", GitignoreRules.Parse("*\n!keep/"));

        scope.IsIgnored("keep", true).Should().BeFalse("!keep/ re-includes the directory itself");
        scope.IsIgnored("keep/a.txt", false).Should().BeTrue("'*' still matches a.txt, as in git");
    }

    [Fact]
    public void NestedReInclusion_OfAnIgnoredDirectoryName_KeepsItsContentsVisible()
    {
        var scope = GitignoreScope.Empty
            .Push("", GitignoreRules.Parse("bin/"))
            .Push("tools", GitignoreRules.Parse("!bin/"));

        scope.IsIgnored("bin/app.dll", false).Should().BeTrue();
        scope.IsIgnored("tools/bin/run.cmd", false).Should().BeFalse();
    }

    [Fact]
    public void IsIgnoredEntry_LooksAtThePathItselfOnly()
    {
        var scope = GitignoreScope.Empty.Push("", GitignoreRules.Parse("bin/"));

        scope.IsIgnoredEntry("bin", true).Should().BeTrue();
        scope.IsIgnoredEntry("bin/app.dll", false).Should().BeFalse("a pruning walk never gets this far");
        scope.IsIgnored("bin/app.dll", false).Should().BeTrue();
    }

    [Fact]
    public void Push_WithEmptyRules_LeavesTheScopeUnchanged()
    {
        var scope = GitignoreScope.Empty.Push("", GitignoreRules.Parse("*.log"));

        scope.Push("src", GitignoreRules.Empty).Should().BeSameAs(scope);
    }
}
