using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace TCV.Git.Tests;

/// <summary>The «+304 −65» of the agent strip: lines changed against HEAD, over the agent's files only.</summary>
public class GitLineStatsTests
{
    private readonly GitWriteService _git = new(NullLogger<GitWriteService>.Instance);

    private static void Commit(TempRepository repo, string relativePath)
    {
        using var git = new Repository(repo.Path);
        Commands.Stage(git, relativePath);
        git.Commit("base", TempRepository.Signature, TempRepository.Signature);
    }

    [Fact]
    public async Task GetLineStatsAsync_ModifiedFile_CountsAddedAndRemovedLines()
    {
        using var repo = new TempRepository("stats-mod");
        repo.WriteFile("a.txt", "one\ntwo\n");
        Commit(repo, "a.txt");
        repo.WriteFile("a.txt", "one\nthree\nfour\n");

        var stats = await _git.GetLineStatsAsync(repo.Path, ["a.txt"]);

        stats.Should().Be(new GitLineStats(2, 1));
    }

    [Fact]
    public async Task GetLineStatsAsync_UntrackedFile_CountsAsAdded()
    {
        using var repo = new TempRepository("stats-new");
        repo.WriteFile("a.txt", "one\n");
        Commit(repo, "a.txt");
        repo.WriteFile("b.txt", "x\ny\nz\n");

        var stats = await _git.GetLineStatsAsync(repo.Path, ["b.txt"]);

        stats.Should().Be(new GitLineStats(3, 0));
    }

    [Fact]
    public async Task GetLineStatsAsync_OnlyTheNamedPaths_AreCounted()
    {
        using var repo = new TempRepository("stats-scope");
        repo.WriteFile("a.txt", "one\n");
        repo.WriteFile("c.txt", "one\n");
        Commit(repo, "a.txt");
        Commit(repo, "c.txt");
        repo.WriteFile("a.txt", "one\ntwo\n");
        repo.WriteFile("c.txt", "changed\n");

        var stats = await _git.GetLineStatsAsync(repo.Path, ["a.txt"]);

        stats.Should().Be(new GitLineStats(1, 0));
    }

    [Fact]
    public async Task GetLineStatsAsync_NoPathsOrNoRepository_IsZero()
    {
        using var repo = new TempRepository("stats-none");

        (await _git.GetLineStatsAsync(repo.Path, [])).Should().Be(GitLineStats.None);
        (await _git.GetLineStatsAsync(Path.GetTempPath(), ["a.txt"])).Should().Be(GitLineStats.None);
    }
}
