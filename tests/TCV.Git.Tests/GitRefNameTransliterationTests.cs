using FluentAssertions;
using TCV.Git;

namespace TCV.Git.Tests;

/// <summary>Russian task titles (the main audience) must still yield a usable branch name.</summary>
public class GitRefNameTransliterationTests
{
    [Theory]
    [InlineData("Исправить вход", "ispravit-vkhod")]
    [InlineData("Ёжик в тумане", "yozhik-v-tumane")]
    [InlineData("Щука, чай и шайба", "shchuka-chay-i-shayba")]
    [InlineData("Fix баг #42", "fix-bag-42")]
    [InlineData("Сделать подъезд", "sdelat-podezd")]
    public void FromTitle_CyrillicTitle_IsTransliteratedToAValidBranch(string title, string expected)
    {
        var branch = GitRefName.FromTitle(title);

        branch.Should().Be(expected);
        GitRefName.IsValidBranch(branch).Should().BeTrue();
    }

    [Fact]
    public void FromTitle_CyrillicWithPrefix_KeepsTheNamespace()
    {
        GitRefName.FromTitle("Новая страница", "feature").Should().Be("feature/novaya-stranitsa");
    }

    [Fact]
    public void FromTitle_TitleWithNoLettersAtAll_StaysEmpty()
    {
        GitRefName.FromTitle("日本語のタイトル").Should().BeEmpty();
        GitRefName.FromTitle("🚀🚀").Should().BeEmpty();
    }

    [Fact]
    public void FromTitle_TitleEndingInDotLock_IsStillAValidRef()
    {
        // "notes.lock" is a fine sentence and an illegal ref name: git reserves the suffix.
        var branch = GitRefName.FromTitle("update notes.lock");

        GitRefName.IsValidBranch(branch).Should().BeTrue();
    }

    [Fact]
    public void DirectoryName_FromTransliteratedBranch_IsAscii()
    {
        GitRefName.DirectoryName(GitRefName.FromTitle("Исправить вход", "fix")).Should().Be("fix-ispravit-vkhod");
    }
}
