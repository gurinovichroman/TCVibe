using System.Windows.Input;
using FluentAssertions;
using TCV.App.Commands;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// «New workspace» and the rail that shows it: a workspace made from nothing has a free name and a
/// colour of its own, and the rail never hides the workspace the panels are on.
/// </summary>
public class NewWorkspaceTests
{
    private static Workspace Ws(string name, bool pinned = true, string color = "") =>
        new() { Name = name, Pinned = pinned, Color = color };

    [Fact]
    public void UnpinnedActive_IsTheActiveWorkspace_WhenItIsNotPinned()
    {
        var items = new[] { Ws("api"), Ws("scratch", pinned: false) };

        WorkspaceGrouping.UnpinnedActive(items, "SCRATCH")!.Name.Should().Be("scratch");
    }

    [Fact]
    public void UnpinnedActive_IsNull_WhenTheActiveOneIsPinned_OrThereIsNone()
    {
        var items = new[] { Ws("api"), Ws("scratch", pinned: false) };

        WorkspaceGrouping.UnpinnedActive(items, "api").Should().BeNull();
        WorkspaceGrouping.UnpinnedActive(items, null).Should().BeNull();
        WorkspaceGrouping.UnpinnedActive(items, "gone").Should().BeNull();
        WorkspaceGrouping.UnpinnedActive(null, "api").Should().BeNull();
    }

    [Fact]
    public void SuggestedColour_IsTheFirstPresetNobodyWears()
    {
        var used = new[] { WorkspaceColor.Presets[0], WorkspaceColor.Presets[1].ToLowerInvariant().TrimStart('#'), "", null };

        WorkspaceColor.SuggestFor(used).Should().Be(WorkspaceColor.Presets[2]);
    }

    [Fact]
    public void SuggestedColour_OnceAllAreWorn_IsTheLeastWorn()
    {
        var used = WorkspaceColor.Presets.Concat(WorkspaceColor.Presets).ToList();
        used.Remove(WorkspaceColor.Presets[3]);
        used.Add("#123456");   // not a preset: does not count against any

        WorkspaceColor.SuggestFor(used).Should().Be(WorkspaceColor.Presets[3]);
    }

    [Fact]
    public void NumberedName_TakesTheFirstFreeNumber_IgnoringCase()
    {
        Workspace.NextNumberedName("Project {0}", ["Project 1", "project 2", "Project 4"]).Should().Be("Project 3");
        Workspace.NextNumberedName("Project {0}", []).Should().Be("Project 1");
    }

    [Fact]
    public void NumberedName_WithoutAPlaceholder_StillEnds_ByNumberingAfterTheText()
    {
        Workspace.NextNumberedName("[Ws_DefaultName]", ["[Ws_DefaultName]"]).Should().Be("[Ws_DefaultName] 1");
        Workspace.NextNumberedName("Project", []).Should().Be("Project 1");
    }

    [Fact]
    public void BlankPanel_IsOneTabOnTheFolder_KeepingTheViewItWasGiven()
    {
        var view = new WorkspacePanelState
        {
            Tabs = [new TabState { Path = @"D:\old" }, new TabState { Path = @"D:\older" }],
            ActiveTabIndex = 1,
            ViewMode = ViewMode.Brief,
            SortBy = SortColumn.Date,
            SortDescending = true,
            ShowHiddenFiles = true,
            FoldersFirst = false,
            ShowExtensions = false,
        };

        var blank = WorkspacePanelState.Blank(@"C:\Users\me", view);

        blank.Tabs.Select(t => t.Path).Should().Equal(@"C:\Users\me");
        blank.ActiveTabIndex.Should().Be(0);
        blank.Should().BeEquivalentTo(view, o => o.Excluding(s => s.Tabs).Excluding(s => s.ActiveTabIndex));
    }

    [Fact]
    public void BlankPanel_WithoutAView_HasTheDefaults()
    {
        WorkspacePanelState.Blank(@"C:\Users\me", null)
            .Should().BeEquivalentTo(new WorkspacePanelState(), o => o.Excluding(s => s.Tabs));
    }

    [Fact]
    public void PropertiesWindow_ForANewWorkspace_SaysCreate_AndHandsBackThePin()
    {
        var draft = new Workspace { Name = "Project 3", Color = "#46A758", Pinned = true };

        var vm = new WorkspacePropertiesDialogViewModel(draft, ["api", "web"], [], isNew: true);
        vm.ConfirmCommand.Execute(null);

        vm.WindowTitle.Should().Be("[WsProps_NewTitle]");
        vm.ConfirmText.Should().Be("[WsProps_Create]");
        vm.Result!.Pinned.Should().BeTrue();
        vm.Result.Color.Should().Be("#46A758");
    }

    [Fact]
    public void PropertiesWindow_ForANewWorkspace_RefusesATakenName()
    {
        var vm = new WorkspacePropertiesDialogViewModel(new Workspace { Name = "Project 1" }, ["api"], [], isNew: true);

        vm.Name = "API";

        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void PropertiesWindow_ForAnExistingWorkspace_StillSaysOk()
    {
        new WorkspacePropertiesDialogViewModel(new Workspace { Name = "api" }, ["api"], [])
            .ConfirmText.Should().Be("[OK]");
    }

    [Fact]
    public void NewWorkspace_IsAltShiftW_AndInThePalette()
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(Key.W, ModifierKeys.Alt | ModifierKeys.Shift).Should().Be("NewWorkspace");
        CommandCatalog.All.Select(c => c.Id).Should().Contain("NewWorkspace");
    }
}
