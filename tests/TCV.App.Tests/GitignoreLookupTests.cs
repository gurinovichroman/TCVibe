using System.IO;
using FluentAssertions;
using TCV.App.Navigation;

namespace TCV.App.Tests;

/// <summary>
/// Covers the panel-side gitignore filter (6.20) against a real directory tree — the rules stack
/// is assembled from files on disk, so an in-memory fake would test nothing that matters.
/// </summary>
public class GitignoreLookupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tcv-ignore-" + Guid.NewGuid().ToString("N"));

    public GitignoreLookupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void Gitignore(string directory, string content) =>
        File.WriteAllText(Path.Combine(directory, ".gitignore"), content);

    private void MakeRepository(string directory) =>
        Directory.CreateDirectory(Path.Combine(directory, ".git"));

    // ── Outside a repository ──────────────────────────────────────────────────

    [Fact]
    public void ForDirectory_NoRepository_IsInactiveAndHidesNothing()
    {
        var dir = Dir("plain");
        Gitignore(dir, "*.log");   // present but meaningless without a working copy

        var lookup = GitignoreLookup.ForDirectory(dir);

        lookup.IsActive.Should().BeFalse();
        lookup.IsIgnored("app.log", false).Should().BeFalse();
    }

    [Fact]
    public void ForDirectory_NullOrMissingPath_IsNone()
    {
        GitignoreLookup.ForDirectory(null).IsActive.Should().BeFalse();
        GitignoreLookup.ForDirectory("   ").IsActive.Should().BeFalse();
    }

    // ── Inside a repository ───────────────────────────────────────────────────

    [Fact]
    public void IsIgnored_RootRules_ApplyAtTheRoot()
    {
        MakeRepository(_root);
        Gitignore(_root, "*.log\nbin/\n");

        var lookup = GitignoreLookup.ForDirectory(_root);

        lookup.IsActive.Should().BeTrue();
        lookup.IsIgnored("app.log", false).Should().BeTrue();
        lookup.IsIgnored("bin", true).Should().BeTrue();
        lookup.IsIgnored("Program.cs", false).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_RootRules_StillApplyDeeperInTheTree()
    {
        MakeRepository(_root);
        Gitignore(_root, "*.log\n");
        var deep = Dir("src", "app");

        GitignoreLookup.ForDirectory(deep).IsIgnored("trace.log", false).Should().BeTrue();
    }

    [Fact]
    public void IsIgnored_AnchoredRootRule_DoesNotReachSubdirectories()
    {
        MakeRepository(_root);
        Gitignore(_root, "/notes.txt\n");
        var sub = Dir("docs");

        GitignoreLookup.ForDirectory(_root).IsIgnored("notes.txt", false).Should().BeTrue();
        GitignoreLookup.ForDirectory(sub).IsIgnored("notes.txt", false).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_NestedRules_OverrideTheOnesAbove()
    {
        MakeRepository(_root);
        Gitignore(_root, "*.tmp\n");
        var sub = Dir("keep");
        Gitignore(sub, "!*.tmp\n");

        GitignoreLookup.ForDirectory(_root).IsIgnored("a.tmp", false).Should().BeTrue();
        GitignoreLookup.ForDirectory(sub).IsIgnored("a.tmp", false).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_DirectoryOnlyRule_DoesNotHideAFileOfThatName()
    {
        MakeRepository(_root);
        Gitignore(_root, "build/\n");

        var lookup = GitignoreLookup.ForDirectory(_root);

        lookup.IsIgnored("build", true).Should().BeTrue();
        lookup.IsIgnored("build", false).Should().BeFalse();
    }

    [Fact]
    public void ForDirectory_WorktreeStyleGitFile_CountsAsARoot()
    {
        // In a linked worktree ".git" is a file pointing at the real git directory (TRAP-25).
        var worktree = Dir("wt");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/wt");
        Gitignore(worktree, "*.log\n");

        var lookup = GitignoreLookup.ForDirectory(worktree);

        lookup.IsActive.Should().BeTrue();
        lookup.IsIgnored("a.log", false).Should().BeTrue();
    }

    [Fact]
    public void ForDirectory_NoGitignoreAnywhere_IsActiveButHidesNothing()
    {
        MakeRepository(_root);
        var sub = Dir("src");

        var lookup = GitignoreLookup.ForDirectory(sub);

        lookup.IsActive.Should().BeTrue();
        lookup.IsIgnored("anything.cs", false).Should().BeFalse();
    }

    [Fact]
    public void IsIgnored_EmptyName_IsNeverIgnored()
    {
        MakeRepository(_root);
        Gitignore(_root, "*\n");

        GitignoreLookup.ForDirectory(_root).IsIgnored(string.Empty, false).Should().BeFalse();
    }
}
