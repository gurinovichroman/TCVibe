using System.IO;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels.Dialogs;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The commit dialog's one piece of state that is not a mirror of git: the "commit everything"
/// checkbox. It decides whether the commit takes the index or the whole working tree, so a refresh
/// quietly changing it commits more than the user staged.
/// </summary>
public class GitCommitDialogViewModelTests : IDisposable
{
    private readonly string _repo =
        Path.Combine(Path.GetTempPath(), $"tcv_commitvm_{Guid.NewGuid():N}");

    public GitCommitDialogViewModelTests()
    {
        Directory.CreateDirectory(_repo);
        Repository.Init(_repo);

        using var repo = new Repository(_repo);
        repo.Config.Set("user.name", "TCV Test");
        repo.Config.Set("user.email", "test@example.invalid");

        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        // Fully qualified: "Commands" alone binds to TCV.App.Commands here.
        LibGit2Sharp.Commands.Stage(repo, "README.md");
        var who = new Signature("TCV Test", "test@example.invalid", DateTimeOffset.Now);
        repo.Commit("init", who, who);

        // A tracked modification: unstaged, and the state that arms the "-a" shortcut.
        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello again\n");
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_repo, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }

            Directory.Delete(_repo, recursive: true);
        }
        catch
        {
            // A leaked temp directory is not worth failing an otherwise green test over.
        }
    }

    private GitCommitDialogViewModel Create() =>
        new(new GitWriteService(NullLogger<GitWriteService>.Instance), _repo);

    [Fact]
    public async Task Refresh_NothingStaged_ArmsCommitEverything()
    {
        var vm = Create();

        await vm.RefreshAsync();

        vm.Unstaged.Should().NotBeEmpty();
        vm.StageAll.Should().BeTrue();
    }

    [Fact]
    public async Task Refresh_AfterTheUserUncheckedIt_LeavesCommitEverythingAlone()
    {
        // Refresh runs after every stage/unstage, so re-applying the default here would re-arm
        // `git commit -a` behind the back of a user who had just turned it off.
        var vm = Create();
        await vm.RefreshAsync();

        vm.StageAll = false;
        await vm.RefreshAsync();

        vm.StageAll.Should().BeFalse();
    }
}
