using FluentAssertions;
using TCV.Ai.Rename;

namespace TCV.Ai.Tests;

/// <summary>
/// The guard that keeps AI renaming away from code. Renaming <c>IMG_4821.jpg</c> is a win; renaming
/// <c>UserService.cs</c> breaks a build, an import graph and a git history at once, so the correct
/// behaviour in a source tree is to refuse.
/// </summary>
public class JunkCornerGuardTests
{
    [Fact]
    public void Evaluate_DownloadsFolderOfScans_IsAllowed()
    {
        var verdict = JunkCornerGuard.Evaluate(
            "Downloads", ["IMG_4821.jpg", "scan001.pdf", "document(3).pdf"], insideRepository: false);

        verdict.Allowed.Should().BeTrue();
        verdict.Reason.Should().BeEmpty();
    }

    [Fact]
    public void Evaluate_InsideAGitWorkingCopy_IsRefused()
    {
        // A version-controlled directory already has a rename tool, and it is `git mv`.
        var verdict = JunkCornerGuard.Evaluate(
            "assets", ["IMG_4821.jpg"], insideRepository: true);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("git");
    }

    [Theory]
    [InlineData("src")]
    [InlineData("tests")]
    [InlineData("node_modules")]
    [InlineData("bin")]
    [InlineData("docs")]
    public void Evaluate_ProjectDirectory_IsRefusedEvenOutsideARepository(string directory)
    {
        var verdict = JunkCornerGuard.Evaluate(directory, ["a.jpg"], insideRepository: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain(directory);
    }

    [Fact]
    public void Evaluate_MostlyCodeFiles_IsRefused()
    {
        var verdict = JunkCornerGuard.Evaluate(
            "stuff", ["Parser.cs", "Lexer.cs", "notes.pdf"], insideRepository: false);

        verdict.Allowed.Should().BeFalse();
        verdict.Reason.Should().Contain("source or configuration");
    }

    [Fact]
    public void Evaluate_OneStrayScriptAmongScans_IsStillAllowed()
    {
        // A folder of scans with one stray script is still a junk corner.
        var files = Enumerable.Range(0, 20).Select(i => $"scan{i:000}.pdf").Append("resize.ps1").ToList();

        JunkCornerGuard.Evaluate("Scans", files, insideRepository: false).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_NothingSelected_IsRefused()
    {
        JunkCornerGuard.Evaluate("Downloads", [], insideRepository: false).Allowed.Should().BeFalse();
    }

    [Fact]
    public void Evaluate_DirectoryNameIsMatchedCaseInsensitively()
    {
        JunkCornerGuard.Evaluate("SRC", ["a.jpg"], insideRepository: false).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("Program.cs", true)]
    [InlineData("app.csproj", true)]
    [InlineData("tsconfig.json", true)]
    [InlineData("Dockerfile.dockerfile", true)]
    [InlineData("IMG_4821.jpg", false)]
    [InlineData("scan.pdf", false)]
    [InlineData("recording.mp4", false)]
    public void IsCodeFile_RecognisesTheUsualSuspects(string name, bool expected) =>
        JunkCornerGuard.IsCodeFile(name).Should().Be(expected);

    [Fact]
    public void RenamableNames_AllowedFolderWithAStrayScript_LeavesTheScriptOut()
    {
        // The folder passes the guard, but the script in it is still code: it must not be renamed.
        var files = Enumerable.Range(0, 7).Select(i => $"scan{i}.pdf").Concat(["install.ps1", "config.json", "setup.bat"]).ToList();
        JunkCornerGuard.Evaluate("Downloads", files, insideRepository: false).Allowed.Should().BeTrue();

        JunkCornerGuard.RenamableNames(files).Should().HaveCount(7).And.OnlyContain(n => n.EndsWith(".pdf"));
    }
}
