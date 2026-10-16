using FluentAssertions;

namespace TCV.Git.Tests;

public class GitRefNameTests
{
    [Theory]
    [InlineData("Fix login redirect", "fix-login-redirect")]
    [InlineData("  Trim   me  ", "trim-me")]
    [InlineData("Issue #412: crash on save", "issue-412-crash-on-save")]
    [InlineData("ALREADY-DASHED", "already-dashed")]
    [InlineData("dots.stay.in", "dots.stay.in")]
    public void FromTitle_NormalisesToABranchName(string title, string expected)
    {
        GitRefName.FromTitle(title).Should().Be(expected);
    }

    [Fact]
    public void FromTitle_WithPrefix_NamespacesTheBranch()
    {
        GitRefName.FromTitle("login redirect", "feature").Should().Be("feature/login-redirect");
    }

    [Fact]
    public void FromTitle_BlankPrefix_LeavesTheBranchUnnamespaced()
    {
        GitRefName.FromTitle("login", "   ").Should().Be("login");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("???")]
    [InlineData(null)]
    public void FromTitle_NothingUsable_ReturnsEmpty(string? title)
    {
        GitRefName.FromTitle(title).Should().BeEmpty();
    }

    [Fact]
    public void FromTitle_DerivedNameIsAlwaysAValidBranch()
    {
        // The whole point of deriving is that the result can be handed straight to git.
        var derived = GitRefName.FromTitle("Weird: name~with^bad [chars] ..and dots..");
        GitRefName.IsValidBranch(derived).Should().BeTrue();
    }

    [Fact]
    public void FromTitle_NonAsciiLettersWithoutATransliteration_AreDropped()
    {
        // A non-ASCII branch is legal in git and broken everywhere downstream — see GitRefName.
        // Russian and Ukrainian are spelled out instead (see GitRefNameTransliterationTests).
        GitRefName.FromTitle("日本 bug 7").Should().Be("bug-7");
    }

    [Theory]
    [InlineData("feature/login")]
    [InlineData("fix-412")]
    [InlineData("release/2.0.1")]
    [InlineData("a")]
    public void IsValidBranch_AcceptsOrdinaryNames(string name)
    {
        GitRefName.IsValidBranch(name).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading-space")]
    [InlineData("trailing-space ")]
    [InlineData("-starts-with-dash")]
    [InlineData("/leading-slash")]
    [InlineData("trailing-slash/")]
    [InlineData("double//slash")]
    [InlineData("dot..dot")]
    [InlineData(".hidden")]
    [InlineData("ends.")]
    [InlineData("thing.lock")]
    [InlineData("feature/.hidden")]
    [InlineData("feature/thing.lock")]
    [InlineData("has space")]
    [InlineData("tilde~here")]
    [InlineData("caret^here")]
    [InlineData("colon:here")]
    [InlineData("question?here")]
    [InlineData("star*here")]
    [InlineData("bracket[here")]
    [InlineData("back\\slash")]
    [InlineData("@")]
    [InlineData("at@{brace")]
    public void IsValidBranch_RejectsWhatGitRejects(string? name)
    {
        GitRefName.IsValidBranch(name).Should().BeFalse();
    }

    [Fact]
    public void DirectoryName_FlattensTheNamespace()
    {
        // A worktree root holds sibling folders; a namespaced branch must not grow a tree there.
        GitRefName.DirectoryName("feature/login-redirect").Should().Be("feature-login-redirect");
    }

    [Theory]
    [InlineData("con")]
    [InlineData("CON")]
    [InlineData("com1")]
    [InlineData("nul")]
    public void DirectoryName_ReservedDeviceNames_GetASuffix(string branch)
    {
        // "con" cannot be created as a folder on Windows at all, and the failure surfaces far from
        // the place that chose the name.
        var directory = GitRefName.DirectoryName(branch);
        directory.Should().Be(branch.ToLowerInvariant() + "-wt");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("///")]
    public void DirectoryName_NothingUsable_FallsBackToAName(string? branch)
    {
        GitRefName.DirectoryName(branch).Should().Be("worktree");
    }
}
