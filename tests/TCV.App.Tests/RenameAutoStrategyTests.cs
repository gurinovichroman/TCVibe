using FluentAssertions;
using TCV.App.Operations.Conflict;

namespace TCV.App.Tests;

public class RenameAutoStrategyTests
{
    [Fact]
    public void GenerateNewName_WhenNoConflict_ReturnsSuffixed1()
    {
        var result = RenameAutoStrategy.GenerateNewName("file.txt", _ => false);
        result.Should().Be("file (1).txt");
    }

    [Fact]
    public void GenerateNewName_WhenFirstConflicts_Returns2()
    {
        var result = RenameAutoStrategy.GenerateNewName("file.txt", n => n == "file (1).txt");
        result.Should().Be("file (2).txt");
    }

    [Fact]
    public void GenerateNewName_NoExtension_Works()
    {
        var result = RenameAutoStrategy.GenerateNewName("folder", _ => false);
        result.Should().Be("folder (1)");
    }

    [Fact]
    public void GenerateNewName_Dotfile_KeepsItsNameWhole()
    {
        // ".gitignore" has an "extension" but no stem: it used to become " (1).gitignore".
        RenameAutoStrategy.GenerateNewName(".gitignore", _ => false).Should().Be(".gitignore (1)");
        RenameAutoStrategy.GenerateNewName(".env.local", _ => false).Should().Be(".env (1).local");
    }

    [Fact]
    public void GenerateNewName_FolderWithADot_IsNotSplitIntoNameAndExtension()
    {
        RenameAutoStrategy.GenerateNewName("v1.2", _ => false, isDirectory: true).Should().Be("v1.2 (1)");
        RenameAutoStrategy.GenerateNewName("v1.2", _ => false).Should().Be("v1 (1).2", "as a file the last dot starts an extension");
    }

    [Fact]
    public async Task GenerateNewNameAsync_FolderWithADot_SkipsTakenNames()
    {
        var taken = new HashSet<string> { "my.folder (1)" };

        var name = await RenameAutoStrategy.GenerateNewNameAsync(
            "my.folder", n => Task.FromResult(taken.Contains(n)), isDirectory: true);

        name.Should().Be("my.folder (2)");
    }

    [Theory]
    [InlineData("a.txt", "a", ".txt")]
    [InlineData("archive.tar.gz", "archive.tar", ".gz")]
    [InlineData("noext", "noext", "")]
    [InlineData(".hidden", ".hidden", "")]
    public void SplitName_SeparatesTheExtensionThatFollowsTheSuffix(string name, string stem, string extension)
    {
        RenameAutoStrategy.SplitName(name).Should().Be((stem, extension));
    }

    [Fact]
    public void ResolveAsync_ReturnsRenameAuto()
    {
        var strategy = new RenameAutoStrategy();
        var task = strategy.ResolveAsync(null!, CancellationToken.None);
        task.Result.Should().Be(ConflictResolution.RenameAuto);
    }
}
