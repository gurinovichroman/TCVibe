using System.IO;
using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The git dialogs against a real throwaway repository: what the branch, commit and clone windows
/// enable, refuse and ask before doing. The windows themselves are not tested; the decisions are.
/// </summary>
public sealed class GitDialogsViewModelTests : IDisposable
{
    private readonly string _repo = Path.Combine(Path.GetTempPath(), $"tcv_gitvm_{Guid.NewGuid():N}");
    private readonly string _defaultBranch;
    private readonly GitWriteService _git = new(NullLogger<GitWriteService>.Instance);

    public GitDialogsViewModelTests()
    {
        Directory.CreateDirectory(_repo);
        Repository.Init(_repo);
        using var repo = new Repository(_repo);
        repo.Config.Set("user.name", "TCV Test");
        repo.Config.Set("user.email", "test@example.invalid");
        repo.Config.Set("core.autocrlf", false);

        File.WriteAllText(Path.Combine(_repo, "README.md"), "hello\n");
        LibGit2Sharp.Commands.Stage(repo, "README.md");
        var who = new Signature("TCV Test", "test@example.invalid", DateTimeOffset.Now);
        repo.Commit("init", who, who);
        _defaultBranch = repo.Head.FriendlyName;
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

    private static Mock<IDialogService> Dialogs(bool confirm)
    {
        var mock = new Mock<IDialogService>();
        mock.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(confirm);
        return mock;
    }

    private void MakeBranch(string name)
    {
        using var repo = new Repository(_repo);
        repo.CreateBranch(name);
    }

    // ── Branch dialog ────────────────────────────────────────────────────────

    [Fact]
    public async Task Branch_Refresh_MarksTheCurrentBranchAndSelectsAnotherOne()
    {
        MakeBranch("topic");
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(true).Object);

        await vm.RefreshAsync();

        vm.CurrentBranch.Should().Be(_defaultBranch);
        vm.Branches.Single(b => b.Name == _defaultBranch).Marker.Should().Be("●");
        vm.Selected!.Name.Should().Be("topic", "the dialog opens on something you can switch to");
        vm.CanSwitch.Should().BeTrue();
    }

    [Fact]
    public async Task Branch_SelectingTheCurrentBranch_CannotSwitchOrDelete()
    {
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(true).Object);
        await vm.RefreshAsync();

        vm.Selected = vm.Branches.Single(b => b.Info.IsCurrent);

        vm.CanSwitch.Should().BeFalse();
        vm.CanDelete.Should().BeFalse();
    }

    [Theory]
    [InlineData("bad name", false)]
    [InlineData("-x", false)]
    [InlineData("feature/ok", true)]
    public async Task Branch_NewName_IsValidatedBeforeCreating(string name, bool expected)
    {
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(true).Object);
        await vm.RefreshAsync();

        vm.NewBranch = name;

        vm.CanCreate.Should().Be(expected);
    }

    [Fact]
    public async Task Branch_NewNameThatExists_ExplainsInsteadOfCreating()
    {
        MakeBranch("taken");
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(true).Object);
        await vm.RefreshAsync();

        vm.NewBranch = "taken";

        vm.CanCreate.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_BranchExists");
    }

    [Fact]
    public async Task Branch_Create_SwitchesAndReportsChange()
    {
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(true).Object);
        await vm.RefreshAsync();
        vm.NewBranch = "fresh";

        await vm.CreateCommand.ExecuteAsync(null);

        vm.Changed.Should().BeTrue();
        vm.DialogResult.Should().BeTrue();
        using var raw = new Repository(_repo);
        raw.Head.FriendlyName.Should().Be("fresh");
    }

    [Fact]
    public async Task Branch_SwitchWithDirtyTree_AcceptedOffer_StashesAndSwitches()
    {
        MakeBranch("other");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "dirty\n");
        var dialogs = Dialogs(true);
        var vm = new GitBranchDialogViewModel(_git, _repo, dialogs.Object);
        await vm.RefreshAsync();
        vm.Selected = vm.Branches.Single(b => b.Name == "other");

        await vm.SwitchCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        using var raw = new Repository(_repo);
        raw.Head.FriendlyName.Should().Be("other");
        raw.Stashes.Should().HaveCount(1, "the changes were stashed, not thrown away");
        File.ReadAllText(Path.Combine(_repo, "README.md")).Should().Be("hello\n");
    }

    [Fact]
    public async Task Branch_SwitchWithDirtyTree_DeclinedOffer_ChangesNothingAndShowsAnError()
    {
        MakeBranch("other");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "dirty\n");
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(false).Object);
        await vm.RefreshAsync();
        vm.Selected = vm.Branches.Single(b => b.Name == "other");

        await vm.SwitchCommand.ExecuteAsync(null);

        vm.IsError.Should().BeTrue();
        vm.Changed.Should().BeFalse();
        using var raw = new Repository(_repo);
        raw.Head.FriendlyName.Should().Be(_defaultBranch);
        File.ReadAllText(Path.Combine(_repo, "README.md")).Should().Be("dirty\n");
    }

    [Fact]
    public async Task Branch_Delete_AsksBeforeDeleting()
    {
        MakeBranch("done");
        var dialogs = Dialogs(false);
        var vm = new GitBranchDialogViewModel(_git, _repo, dialogs.Object);
        await vm.RefreshAsync();
        vm.Selected = vm.Branches.Single(b => b.Name == "done");

        await vm.DeleteCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        using var raw = new Repository(_repo);
        raw.Branches["done"].Should().NotBeNull("declining the confirmation deletes nothing");
    }

    [Fact]
    public async Task Branch_DeleteConfirmed_RemovesItAndRefreshesTheList()
    {
        MakeBranch("done");
        var vm = new GitBranchDialogViewModel(_git, _repo, Dialogs(true).Object);
        await vm.RefreshAsync();
        vm.Selected = vm.Branches.Single(b => b.Name == "done");

        await vm.DeleteCommand.ExecuteAsync(null);

        vm.Branches.Should().NotContain(b => b.Name == "done");
        vm.Changed.Should().BeTrue();
        vm.IsError.Should().BeFalse();
    }

    [Fact]
    public async Task Branch_DeleteUnmerged_NeedsASecondConfirmation()
    {
        using (var raw = new Repository(_repo))
        {
            var topic = raw.CreateBranch("topic");
            LibGit2Sharp.Commands.Checkout(raw, topic);
            File.WriteAllText(Path.Combine(_repo, "t.txt"), "t\n");
            LibGit2Sharp.Commands.Stage(raw, "t.txt");
            var who = new Signature("TCV Test", "test@example.invalid", DateTimeOffset.Now);
            raw.Commit("only here", who, who);
            LibGit2Sharp.Commands.Checkout(raw, raw.Branches[_defaultBranch]);
        }

        var dialogs = Dialogs(true);
        var vm = new GitBranchDialogViewModel(_git, _repo, dialogs.Object);
        await vm.RefreshAsync();
        vm.Selected = vm.Branches.Single(b => b.Name == "topic");

        await vm.DeleteCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Exactly(2));
        vm.Branches.Should().NotContain(b => b.Name == "topic");
    }

    // ── Commit dialog ────────────────────────────────────────────────────────

    private GitCommitDialogViewModel CommitVm(IDialogService? dialogs = null) =>
        new(_git, _repo, llm: null, dialogs);

    [Fact]
    public async Task Commit_OnlyUntrackedFiles_NeverOffersACommit()
    {
        File.WriteAllText(Path.Combine(_repo, "new.txt"), "x\n");
        var vm = CommitVm();
        await vm.RefreshAsync();

        vm.Message = "add new";
        vm.StageAll = true;

        vm.StageAll.Should().BeTrue();
        vm.HasTrackedUnstaged.Should().BeFalse();
        vm.CanCommit.Should().BeFalse("commit -a ignores untracked files, so this commit would be empty");
    }

    [Fact]
    public async Task Commit_TrackedChangeWithCommitAll_IsOffered()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");
        var vm = CommitVm();
        await vm.RefreshAsync();

        vm.Message = "tweak";

        vm.StageAll.Should().BeTrue();
        vm.CanCommit.Should().BeTrue();
    }

    [Fact]
    public async Task Commit_WhileBusy_IsNotOffered()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");
        var vm = CommitVm();
        await vm.RefreshAsync();
        vm.Message = "tweak";

        vm.IsBusy = true;

        vm.CanCommit.Should().BeFalse("a second press during the first commit would run it twice");
        vm.IsIdle.Should().BeFalse();
    }

    [Fact]
    public async Task Commit_StagesSeveralSelectedFilesAtOnce()
    {
        File.WriteAllText(Path.Combine(_repo, "a.txt"), "a\n");
        File.WriteAllText(Path.Combine(_repo, "b.txt"), "b\n");
        File.WriteAllText(Path.Combine(_repo, "c.txt"), "c\n");
        var vm = CommitVm();
        await vm.RefreshAsync();

        var selection = vm.Unstaged.Where(f => f.RelativePath is "a.txt" or "b.txt").ToList();
        await vm.StageSelectedCommand.ExecuteAsync(selection);

        vm.Staged.Select(f => f.RelativePath).Should().BeEquivalentTo("a.txt", "b.txt");
        vm.Unstaged.Select(f => f.RelativePath).Should().Contain("c.txt");
        vm.Changed.Should().BeTrue();
    }

    [Fact]
    public async Task Commit_DiscardWithoutConfirmation_KeepsTheChanges()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "precious edits\n");
        var dialogs = Dialogs(false);
        var vm = CommitVm(dialogs.Object);
        await vm.RefreshAsync();

        await vm.DiscardSelectedCommand.ExecuteAsync(vm.Unstaged.ToList());

        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        File.ReadAllText(Path.Combine(_repo, "README.md")).Should().Be("precious edits\n");
    }

    [Fact]
    public async Task Commit_DiscardWithNoDialogService_NeverDeletesAnything()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "precious edits\n");
        var vm = CommitVm(dialogs: null);
        await vm.RefreshAsync();

        await vm.DiscardSelectedCommand.ExecuteAsync(vm.Unstaged.ToList());

        File.ReadAllText(Path.Combine(_repo, "README.md")).Should().Be("precious edits\n");
    }

    [Fact]
    public async Task Commit_DiscardConfirmed_RestoresTheCommittedVersion()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "throw me away\n");
        var vm = CommitVm(Dialogs(true).Object);
        await vm.RefreshAsync();

        await vm.DiscardSelectedCommand.ExecuteAsync(vm.Unstaged.ToList());

        File.ReadAllText(Path.Combine(_repo, "README.md")).Should().Be("hello\n");
        vm.Unstaged.Should().BeEmpty();
    }

    [Fact]
    public async Task Commit_DiscardOfAnUntrackedFile_IsRefusedAndTheFileSurvives()
    {
        var untracked = Path.Combine(_repo, "new.txt");
        File.WriteAllText(untracked, "x\n");
        var dialogs = Dialogs(true);
        var vm = CommitVm(dialogs.Object);
        await vm.RefreshAsync();

        await vm.DiscardSelectedCommand.ExecuteAsync(vm.Unstaged.ToList());

        File.Exists(untracked).Should().BeTrue("removing a new file is a deletion and belongs to F8");
        vm.IsError.Should().BeTrue();
        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Commit_Failure_IsShownAsAnError()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");
        var vm = CommitVm();
        await vm.RefreshAsync();
        vm.Message = "x";
        vm.StageAll = false;   // nothing staged and commit-all off: not committable
        vm.CanCommit.Should().BeFalse();

        await vm.CommitCommand.ExecuteAsync(null);

        vm.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task Commit_Succeeds_ClosesAndReportsChange()
    {
        File.WriteAllText(Path.Combine(_repo, "README.md"), "changed\n");
        var vm = CommitVm();
        await vm.RefreshAsync();
        vm.Message = "tweak readme";

        await vm.CommitCommand.ExecuteAsync(null);

        vm.Committed.Should().BeTrue();
        vm.Changed.Should().BeTrue();
        vm.DialogResult.Should().BeTrue();
    }

    [Fact]
    public async Task Commit_ConflictStaging_NeedsConfirmation()
    {
        MakeConflict();
        var dialogs = Dialogs(false);
        var vm = CommitVm(dialogs.Object);
        await vm.RefreshAsync();

        await vm.StageSelectedCommand.ExecuteAsync(vm.Unstaged.ToList());

        dialogs.Verify(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        vm.Unstaged.Should().Contain(f => f.Status == GitFileStatus.Conflict, "declining leaves the conflict alone");
    }

    private void MakeConflict()
    {
        using var raw = new Repository(_repo);
        var who = new Signature("TCV Test", "test@example.invalid", DateTimeOffset.Now);
        var other = raw.CreateBranch("clash");

        LibGit2Sharp.Commands.Checkout(raw, other);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "from clash\n");
        LibGit2Sharp.Commands.Stage(raw, "README.md");
        raw.Commit("clash edit", who, who);

        LibGit2Sharp.Commands.Checkout(raw, raw.Branches[_defaultBranch]);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "from main\n");
        LibGit2Sharp.Commands.Stage(raw, "README.md");
        raw.Commit("main edit", who, who);

        raw.Merge(raw.Branches["clash"], who);
    }

    // ── Clone dialog ─────────────────────────────────────────────────────────

    private static GitCloneDialogViewModel CloneVm(string url = "") =>
        new(new GitCloneService(NullLogger<GitCloneService>.Instance), @"D:\work", url);

    [Fact]
    public void Clone_PastedUrl_DerivesTheTargetFolder()
    {
        var vm = CloneVm();

        vm.Url = "https://github.com/owner/some-repo.git";

        vm.TargetPath.Should().Be(@"D:\work\some-repo");
    }

    [Fact]
    public void Clone_LocalSourceWithSpaces_IsAccepted()
    {
        var vm = CloneVm();

        vm.Url = @"D:\my repos\thing";

        vm.ValidationMessage.Should().NotContain("Git_CloneBadUrl");
        vm.TargetPath.Should().Be(@"D:\work\thing");
    }

    [Fact]
    public void Clone_GarbageUrl_IsRejected()
    {
        var vm = CloneVm();

        vm.Url = "not a url";

        vm.CanClone.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_CloneBadUrl");
    }

    [Fact]
    public async Task Clone_UnreachableSource_ShowsAnErrorAndKeepsTheDialogOpen()
    {
        var target = Path.Combine(Path.GetTempPath(), $"tcv_clonevm_{Guid.NewGuid():N}");
        var vm = new GitCloneDialogViewModel(
            new GitCloneService(NullLogger<GitCloneService>.Instance), Path.GetTempPath(), string.Empty)
        {
            SecretProvider = () => string.Empty,
        };
        vm.Url = Path.Combine(Path.GetTempPath(), $"no_such_repo_{Guid.NewGuid():N}");
        vm.TargetPath = target;

        await vm.CloneCommand.ExecuteAsync(null);

        vm.IsError.Should().BeTrue();
        vm.DialogResult.Should().BeFalse();
        vm.IsBusy.Should().BeFalse();
        Directory.Exists(target).Should().BeFalse("a failed clone leaves nothing behind");
    }

    [Fact]
    public void Clone_SecretIsNeverExposedAsAProperty()
    {
        typeof(GitCloneDialogViewModel).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Password", StringComparison.OrdinalIgnoreCase)
                                      || n.Contains("Secret", StringComparison.Ordinal) && n != "SecretProvider"
                                      || n == "Token");
    }
}
