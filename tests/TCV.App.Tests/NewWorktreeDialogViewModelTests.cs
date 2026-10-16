using System.IO;
using FluentAssertions;
using TCV.App.ViewModels.Dialogs;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The derivation rules behind "new worktree for a task". They are the difference between using
/// worktrees and meaning to: if the branch and directory have to be typed by hand, the flow costs
/// more than doing it in a terminal.
/// </summary>
public class NewWorktreeDialogViewModelTests
{
    private const string RepositoryRoot = @"D:\projects\thing";
    private const string WorktreeRoot = @"D:\projects\thing-worktrees";

    private static NewWorktreeDialogViewModel Create(params string[] branches) =>
        new(RepositoryRoot, WorktreeRoot, branches, setupCommand: "npm install",
            createWorkspaceByDefault: true);

    [Fact]
    public void TaskTitle_DerivesBothTheBranchAndThePath()
    {
        var vm = Create();

        vm.TaskTitle = "Fix login redirect";

        vm.Branch.Should().Be("fix-login-redirect");
        vm.TargetPath.Should().Be(Path.Combine(WorktreeRoot, "fix-login-redirect"));
    }

    [Fact]
    public void EditingTheBranch_StopsItFollowingTheTitleButStillDrivesThePath()
    {
        var vm = Create();
        vm.TaskTitle = "first";

        vm.Branch = "hand/written";

        vm.TargetPath.Should().Be(Path.Combine(WorktreeRoot, "hand-written"));

        vm.TaskTitle = "second";
        vm.Branch.Should().Be("hand/written");
    }

    [Fact]
    public void EditingThePath_StopsItFollowingAnything()
    {
        var vm = Create();
        vm.TaskTitle = "first";

        vm.TargetPath = @"E:\elsewhere";
        vm.TaskTitle = "second";
        vm.Branch = "third";

        vm.TargetPath.Should().Be(@"E:\elsewhere");
    }

    [Fact]
    public void NamespacedBranch_FlattensIntoASiblingFolder()
    {
        var vm = Create();

        vm.Branch = "feature/login";

        vm.TargetPath.Should().Be(Path.Combine(WorktreeRoot, "feature-login"));
    }

    [Fact]
    public void CanConfirm_RequiresAValidBranchAndAPath()
    {
        var vm = Create();
        vm.CanConfirm.Should().BeFalse();

        vm.TaskTitle = "something";
        vm.CanConfirm.Should().BeTrue();
    }

    [Fact]
    public void CanConfirm_InvalidBranch_IsRefusedWithAReason()
    {
        var vm = Create();
        vm.TaskTitle = "ok";

        vm.Branch = "bad name";

        vm.CanConfirm.Should().BeFalse();
        vm.ValidationMessage.Should().NotBeEmpty();
    }

    [Fact]
    public void ValidationMessage_EmptyForm_SaysNothing()
    {
        // A dialog that opens shouting at you is noise, not validation.
        Create().ValidationMessage.Should().BeEmpty();
    }

    [Fact]
    public void ValidationMessage_TitleWithNothingToSlug_ExplainsTheEmptyFields()
    {
        // A title with no Latin or Cyrillic letters slugs to an empty string (Russian titles are now
        // transliterated), and without a message the dialog just sits there with OK greyed out.
        var vm = Create();

        vm.TaskTitle = "日本語のタイトル";

        vm.Branch.Should().BeEmpty();
        vm.TargetPath.Should().BeEmpty();
        vm.CanConfirm.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_WtTitleNoSlug");
    }

    [Fact]
    public void BranchExists_DecidesWhetherTheBranchIsCreatedOrReused()
    {
        var vm = Create("existing", "other");

        vm.Branch = "existing";
        vm.BranchExists.Should().BeTrue();

        vm.Branch = "fresh";
        vm.BranchExists.Should().BeFalse();
    }

    [Fact]
    public void Confirm_ReportsCreateBranchFalseForAnExistingBranch()
    {
        var vm = Create("existing");
        vm.Branch = "existing";

        vm.ConfirmCommand.Execute(null);

        vm.Result.Should().NotBeNull();
        vm.Result!.CreateBranch.Should().BeFalse();
        vm.Result.Branch.Should().Be("existing");
        vm.DialogResult.Should().BeTrue();
    }

    [Fact]
    public void Confirm_CarriesTheCheckboxesThrough()
    {
        var vm = Create();
        vm.TaskTitle = "task";
        vm.CarryFiles = false;
        vm.RunSetupCommand = true;
        vm.CreateWorkspace = false;
        vm.BaseCommittish = "  main  ";

        vm.ConfirmCommand.Execute(null);

        vm.Result!.CarryFiles.Should().BeFalse();
        vm.Result.RunSetupCommand.Should().BeTrue();
        vm.Result.CreateWorkspace.Should().BeFalse();
        vm.Result.BaseCommittish.Should().Be("main");
    }

    [Fact]
    public void Confirm_WithoutASetupCommand_NeverAsksForOne()
    {
        var vm = new NewWorktreeDialogViewModel(
            RepositoryRoot, WorktreeRoot, Array.Empty<string>(),
            setupCommand: "", createWorkspaceByDefault: true);
        vm.TaskTitle = "task";

        vm.HasSetupCommand.Should().BeFalse();
        vm.RunSetupCommand.Should().BeFalse();

        vm.ConfirmCommand.Execute(null);
        vm.Result!.RunSetupCommand.Should().BeFalse();
    }

    [Fact]
    public void Confirm_InvalidState_DoesNothing()
    {
        var vm = Create();

        vm.ConfirmCommand.Execute(null);

        vm.Result.Should().BeNull();
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void Cancel_ProducesNoResult()
    {
        var vm = Create();
        vm.TaskTitle = "task";

        vm.CancelCommand.Execute(null);

        vm.Result.Should().BeNull();
        vm.DialogResult.Should().BeFalse();
    }
}
