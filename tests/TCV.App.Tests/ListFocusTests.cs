using FluentAssertions;
using TCV.App.Controls;

namespace TCV.App.Tests;

/// <summary>When the file list may put the keyboard on its cursor, and whom it must leave it with.</summary>
public class ListFocusTests
{
    [Fact]
    public void MayTakeKeys_ReloadOfTheInactivePanel_LeavesTheKeysWhereTheyAre()
    {
        // The other panel's watcher reloaded it, its cursor cell took the focus, and the next Shift+↓
        // marked a file in the panel the user was not working in.
        ListFocus.MayTakeKeys(KeyHolder.Other, FocusClaim.Follow, panelIsActive: false).Should().BeFalse();
    }

    [Fact]
    public void MayTakeKeys_ReloadOfTheActivePanel_PutsTheKeysOnItsCursor()
    {
        ListFocus.MayTakeKeys(KeyHolder.Other, FocusClaim.Follow, panelIsActive: true).Should().BeTrue();
    }

    [Theory]
    [InlineData(FocusClaim.Hand)]
    [InlineData(FocusClaim.Panel)]
    public void MayTakeKeys_HandedOrActivated_TakesThemEvenBeforeTheFlagSaysActive(FocusClaim claim)
    {
        ListFocus.MayTakeKeys(KeyHolder.Other, claim, panelIsActive: false).Should().BeTrue();
    }

    [Theory]
    [InlineData(FocusClaim.Hand)]
    [InlineData(FocusClaim.Panel)]
    [InlineData(FocusClaim.Follow)]
    public void MayTakeKeys_NameBeingRenamed_KeepsTheKeys(FocusClaim claim)
    {
        // Focusing the cell would close the box (TRAP-76).
        ListFocus.MayTakeKeys(KeyHolder.NameEdit, claim, panelIsActive: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(FocusClaim.Hand, true)]
    [InlineData(FocusClaim.Panel, false)]
    [InlineData(FocusClaim.Follow, false)]
    public void MayTakeKeys_OwnQuickFilterBeingTyped_GivesThemUpOnlyWhenHanded(FocusClaim claim, bool expected)
    {
        ListFocus.MayTakeKeys(KeyHolder.OwnFilter, claim, panelIsActive: true).Should().Be(expected);
    }

    [Theory]
    [InlineData(FocusClaim.Hand, true)]
    [InlineData(FocusClaim.Panel, true)]
    [InlineData(FocusClaim.Follow, false)]
    public void MayTakeKeys_OtherPanelsQuickFilter_GivesThemUpWhenThisPanelIsActivated(FocusClaim claim, bool expected)
    {
        ListFocus.MayTakeKeys(KeyHolder.OtherFilter, claim, panelIsActive: true).Should().Be(expected);
    }
}
