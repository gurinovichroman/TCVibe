using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The setup hook: what makes a fresh worktree runnable instead of merely present. The command half
/// launches a terminal and is not testable headlessly; the file half is, and it is the half that
/// silently does nothing when the patterns or the scan are wrong.
/// </summary>
public class WorktreeSetupServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tcv_setup_{Guid.NewGuid():N}");
    private readonly WorktreeSetupService _service = new(
        new TerminalService(NullLogger<TerminalService>.Instance),
        NullLogger<WorktreeSetupService>.Instance);

    private string Source => Path.Combine(_root, "source");
    private string Target => Path.Combine(_root, "worktree");

    public WorktreeSetupServiceTests()
    {
        Directory.CreateDirectory(Source);
        Directory.CreateDirectory(Target);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void WriteSource(string relativePath, string content = "x")
    {
        var full = Path.Combine(Source, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static GitSettings Settings(params string[] patterns)
    {
        var settings = new GitSettings();
        settings.WorktreeCarryPatterns.AddRange(patterns);
        return settings;
    }

    [Fact]
    public void SuggestWorktreeRoot_NoConfiguration_PutsWorktreesBesideTheRepository()
    {
        // Beside, not inside: a worktree nested in its own repository shows up as untracked in
        // every status and gets swept into the next "git add .".
        var suggested = WorktreeSetupService.SuggestWorktreeRoot(@"D:\projects\thing", null);

        suggested.Should().Be(Path.Combine(@"D:\projects", "thing-worktrees"));
    }

    [Fact]
    public void SuggestWorktreeRoot_Configured_WinsOverTheDefault()
    {
        WorktreeSetupService.SuggestWorktreeRoot(@"D:\projects\thing", @"E:\wt")
            .Should().Be(@"E:\wt");
    }

    [Fact]
    public void SuggestWorktreeRoot_NoRepository_ReturnsEmpty()
    {
        WorktreeSetupService.SuggestWorktreeRoot("", "  ").Should().BeEmpty();
    }

    [Fact]
    public void EffectivePatterns_Unconfigured_FallsBackToTheDefaults()
    {
        WorktreeSetupService.EffectivePatterns(new GitSettings())
            .Should().BeEquivalentTo(WorktreeSetupPlan.DefaultCarryPatterns);
    }

    [Fact]
    public void EffectivePatterns_Configured_UsesTheUsersList()
    {
        WorktreeSetupService.EffectivePatterns(Settings("only-this"))
            .Should().BeEquivalentTo(new[] { "only-this" });
    }

    [Fact]
    public async Task RunAsync_CopiesMatchingFilesAndRecreatesTheirDirectories()
    {
        WriteSource(".env", "TOKEN=1");
        WriteSource("src/api/.env", "NESTED=1");
        WriteSource("src/Program.cs");

        var result = await _service.RunAsync(
            Source, Target, Settings(".env"), carryFiles: true, runCommand: false);

        result.CopiedFiles.Should().Be(2);
        File.ReadAllText(Path.Combine(Target, ".env")).Should().Be("TOKEN=1");
        File.ReadAllText(Path.Combine(Target, "src", "api", ".env")).Should().Be("NESTED=1");
        File.Exists(Path.Combine(Target, "src", "Program.cs")).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_SkipsBuildOutputAndDependencyDirectories()
    {
        // node_modules can hold more entries than the rest of the repository put together, and it
        // is exactly what the setup command rebuilds.
        WriteSource("node_modules/pkg/.env");
        WriteSource("bin/Debug/.env");
        WriteSource(".git/.env");

        var result = await _service.RunAsync(
            Source, Target, Settings(".env"), carryFiles: true, runCommand: false);

        result.CopiedFiles.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ExistingTargetFile_IsNotOverwritten()
    {
        // Anything already in a fresh worktree came from the checkout and is tracked content.
        WriteSource("config.json", "from source");
        File.WriteAllText(Path.Combine(Target, "config.json"), "from checkout");

        var result = await _service.RunAsync(
            Source, Target, Settings("config.json"), carryFiles: true, runCommand: false);

        result.CopiedFiles.Should().Be(0);
        File.ReadAllText(Path.Combine(Target, "config.json")).Should().Be("from checkout");
    }

    [Fact]
    public async Task RunAsync_CarryDisabled_CopiesNothing()
    {
        WriteSource(".env");

        var result = await _service.RunAsync(
            Source, Target, Settings(".env"), carryFiles: false, runCommand: false);

        result.CopiedFiles.Should().Be(0);
        result.CommandStarted.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_NoSetupCommandConfigured_StartsNothing()
    {
        var settings = Settings(".env");
        settings.WorktreeSetupCommand = "   ";

        var result = await _service.RunAsync(
            Source, Target, settings, carryFiles: false, runCommand: true);

        result.CommandStarted.Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_MissingDirectories_AreNotAnError()
    {
        var result = await _service.RunAsync(
            Path.Combine(_root, "nope"), Target, Settings(".env"), carryFiles: true, runCommand: false);

        result.CopiedFiles.Should().Be(0);
    }
}
