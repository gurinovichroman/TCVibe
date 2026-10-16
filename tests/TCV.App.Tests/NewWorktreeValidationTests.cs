using FluentAssertions;
using TCV.App.Columns;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using Xunit;

namespace TCV.App.Tests;

/// <summary>Validation of the "new worktree" form, the Git column's place in the catalog, and the wt.exe quoting.</summary>
public class NewWorktreeValidationTests
{
    private const string RepositoryRoot = @"D:\projects\thing";
    private const string WorktreeRoot = @"D:\projects\thing-worktrees";

    private static NewWorktreeDialogViewModel Create(
        string[]? branches = null, string[]? heldElsewhere = null) =>
        new(RepositoryRoot, WorktreeRoot, branches ?? [], setupCommand: "",
            createWorkspaceByDefault: true, heldElsewhere);

    [Theory]
    [InlineData("", NewWorktreeDialogViewModel.PathProblem.Empty)]
    [InlineData("   ", NewWorktreeDialogViewModel.PathProblem.Empty)]
    [InlineData("worktrees\\x", NewWorktreeDialogViewModel.PathProblem.NotAbsolute)]
    [InlineData("\\rooted-but-no-drive", NewWorktreeDialogViewModel.PathProblem.NotAbsolute)]
    [InlineData("D:\\wt\\bad\0name", NewWorktreeDialogViewModel.PathProblem.InvalidCharacters)]
    [InlineData("D:\\definitely\\not\\existing\\yet", NewWorktreeDialogViewModel.PathProblem.None)]
    public void CheckPath_ClassifiesWhatIsWrongWithAWorktreeDirectory(
        string path, NewWorktreeDialogViewModel.PathProblem expected)
    {
        NewWorktreeDialogViewModel.CheckPath(path).Should().Be(expected);
    }

    [Fact]
    public void CanConfirm_WhitespaceOnlyPath_IsOffButNeverThrows()
    {
        var vm = Create();
        vm.Branch = "feature/x";

        vm.TargetPath = "   ";

        vm.CanConfirm.Should().BeFalse();
        vm.ConfirmCommand.Execute(null);   // used to reach Path.GetFullPath("") and throw
        vm.Result.Should().BeNull();
    }

    [Fact]
    public void CanConfirm_RelativePath_IsRefusedWithAnExplanation()
    {
        var vm = Create();
        vm.Branch = "feature/x";

        vm.TargetPath = "somewhere\\else";

        vm.CanConfirm.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_WtPathRelative");
    }

    [Fact]
    public void CanConfirm_BranchHeldByAnotherWorktree_IsRefusedBeforeCreatePressed()
    {
        var vm = Create(branches: ["busy"], heldElsewhere: ["busy"]);

        vm.Branch = "busy";

        vm.CanConfirm.Should().BeFalse();
        vm.ValidationMessage.Should().Contain("Git_WtBranchHeld");
    }

    [Fact]
    public void InfoMessage_ExistingBranch_SaysItWillBeCheckedOut()
    {
        var vm = Create(branches: ["already-there"]);

        vm.Branch = "already-there";

        vm.InfoMessage.Should().Contain("Git_WtBranchExists");
        vm.CanConfirm.Should().BeTrue();
    }

    [Fact]
    public void InfoMessage_ExistingBranchWithBaseCommit_WarnsTheBaseIsIgnored()
    {
        var vm = Create(branches: ["already-there"]);
        vm.Branch = "already-there";

        vm.BaseCommittish = "main~3";

        vm.InfoMessage.Should().Contain("Git_WtBranchExistsBase");
    }

    [Fact]
    public void InfoMessage_DirectoryInsideTheRepository_IsFlaggedButNotBlocked()
    {
        var vm = Create();
        vm.Branch = "feature/x";

        vm.TargetPath = @"D:\projects\thing\wt-inside";

        vm.InfoMessage.Should().Contain("Git_WtInsideRepo");
        vm.CanConfirm.Should().BeTrue();
    }

    [Fact]
    public void TaskTitle_Russian_NowDerivesATransliteratedBranchAndDirectory()
    {
        var vm = Create();

        vm.TaskTitle = "Починить вход";

        vm.Branch.Should().Be("pochinit-vkhod");
        vm.TargetPath.Should().Be(@"D:\projects\thing-worktrees\pochinit-vkhod");
        vm.CanConfirm.Should().BeTrue();
    }

    // ── Git column in the catalog ─────────────────────────────────────────────

    [Fact]
    public void ColumnCatalog_GitColumn_IsOnByDefault()
    {
        ColumnCatalog.Find(ColumnCatalog.Git).Should().NotBeNull();
        ColumnCatalog.DefaultLayout().Select(c => c.Id).Should().Contain(ColumnCatalog.Git);

        var normalised = ColumnCatalog.Normalize(ColumnCatalog.DefaultLayout());
        normalised.Should().ContainSingle(c => c.Id == ColumnCatalog.Git && c.Visible);
    }

    [Fact]
    public void ColumnCatalog_GitColumn_SurvivesARoundTripWhenSwitchedOff()
    {
        var layout = ColumnCatalog.DefaultLayout();
        layout.Single(c => c.Id == ColumnCatalog.Git).Visible = false;

        var normalised = ColumnCatalog.Normalize(layout);

        normalised.Single(c => c.Id == ColumnCatalog.Git).Visible.Should().BeFalse();
    }

    // ── wt.exe quoting ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("npm ci", "npm ci")]
    [InlineData("npm ci; npm run build", "npm ci\\; npm run build")]
    [InlineData("a;b;c", "a\\;b\\;c")]
    [InlineData("already\\; escaped", "already\\; escaped")]
    [InlineData(";leading", "\\;leading")]
    public void EscapeForWindowsTerminal_SemicolonsBecomeLiteral(string command, string expected)
    {
        TerminalService.EscapeForWindowsTerminal(command).Should().Be(expected);
    }
}
