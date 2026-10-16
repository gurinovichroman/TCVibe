using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels.Dialogs;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The dashboard's refusals. Everything else it does needs a repository; what is worth pinning down
/// without one is which rows it must not offer to delete.
/// </summary>
public class GitWorktreeDialogViewModelTests
{
    private static GitWorktreeDialogViewModel Create() =>
        new(new GitWorktreeService(NullLogger<GitWorktreeService>.Instance),
            @"D:\projects\thing",
            _ => true);

    private static GitWorktreeInfo Row(bool isMain = false, bool isCurrent = false) =>
        new("name", @"D:\projects\thing", "main", IsMain: isMain, IsCurrent: isCurrent,
            IsLocked: false, Missing: false, DirtyCount: 0, AheadBy: 0, BehindBy: 0,
            HasUpstream: false);

    [Fact]
    public void CanRemoveSelected_OrdinaryWorktree_IsAllowed()
    {
        var vm = Create();

        vm.Selected = Row();

        vm.CanRemoveSelected.Should().BeTrue();
    }

    [Fact]
    public void CanRemoveSelected_MainCheckout_IsRefused()
    {
        var vm = Create();

        vm.Selected = Row(isMain: true);

        vm.CanRemoveSelected.Should().BeFalse();
    }

    [Fact]
    public async Task RemoveSelected_TheWorktreeThePanelIsIn_IsRefused()
    {
        // git will not prune a worktree that is in use, and going through with it would leave the
        // panel standing in a directory being deleted underneath it.
        var vm = Create();
        vm.Selected = Row(isCurrent: true);

        vm.CanRemoveSelected.Should().BeFalse();

        await vm.RemoveSelectedCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().BeEmpty();
    }
}
