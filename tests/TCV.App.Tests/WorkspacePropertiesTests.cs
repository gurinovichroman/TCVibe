using System.Windows.Input;
using FluentAssertions;
using TCV.App.Commands;
using TCV.App.FileSystem;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>
/// The one window that replaced the separate icon / colour / group / rename prompts: it must refuse
/// what the store would refuse, and hand back clean values.
/// </summary>
public class WorkspacePropertiesDialogViewModelTests
{
    private static WorkspacePropertiesDialogViewModel Create(
        Workspace? ws = null, string[]? names = null, string[]? groups = null) =>
        new(
            ws ?? new Workspace { Name = "api", Icon = "🚀", Color = "#0090FF", Group = "Work", Pinned = true },
            names ?? ["api", "web", "docs"],
            groups ?? ["Work", "OSS", "work", "", " "]);

    [Fact]
    public void StartsFromTheWorkspace()
    {
        var vm = Create();

        vm.Name.Should().Be("api");
        vm.Icon.Should().Be("🚀");
        vm.ColorText.Should().Be("#0090FF");
        vm.Group.Should().Be("Work");
        vm.Pinned.Should().BeTrue();
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Groups_AreTheDistinctNonBlankOnes_Sorted()
    {
        Create().Groups.Should().Equal("OSS", "Work");
    }

    [Fact]
    public void ANameAnotherWorkspaceHas_CannotBeConfirmed()
    {
        var vm = Create();

        vm.Name = "WEB";

        vm.NameError.Should().NotBeEmpty();
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void ChangingOnlyTheCaseOfItsOwnName_IsAllowed()
    {
        var vm = Create();

        vm.Name = "API";

        vm.NameError.Should().BeEmpty();
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ABlankName_CannotBeConfirmed()
    {
        var vm = Create();

        vm.Name = "   ";

        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void AnUnreadableColour_CannotBeConfirmed_ButABlankOneMeansNone()
    {
        var vm = Create();

        vm.ColorText = "banana";
        vm.ColorError.Should().NotBeEmpty();
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();

        vm.ColorText = string.Empty;
        vm.ColorError.Should().BeEmpty();
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void PickingASwatch_SetsTheColourAndMarksThatSwatchOnly()
    {
        var vm = Create();
        var red = WorkspaceColor.Presets[0];

        vm.PickColorCommand.Execute(red);

        vm.NormalizedColor.Should().Be(red);
        vm.Swatches.Where(s => s.IsSelected).Select(s => s.Hex).Should().Equal(red);
    }

    [Fact]
    public void TypingAPresetInShortOrLowerCase_StillMarksItsSwatch()
    {
        var vm = Create();
        var preset = WorkspaceColor.Presets[5];

        vm.ColorText = preset.ToLowerInvariant().TrimStart('#');

        vm.Swatches.Single(s => s.IsSelected).Hex.Should().Be(preset);
    }

    [Fact]
    public void Confirm_HandsBackTrimmedAndNormalisedValues()
    {
        var vm = Create();
        vm.Name = "  api-v2 ";
        vm.Icon = " 🔧 ";
        vm.ColorText = "f00";
        vm.Group = " OSS ";
        vm.Pinned = false;

        vm.ConfirmCommand.Execute(null);

        vm.DialogResult.Should().BeTrue();
        vm.Result.Should().Be(new TCV.App.Services.WorkspacePropertiesResult("api-v2", "🔧", "#FF0000", "OSS", false));
    }

    [Fact]
    public void PreviewName_IsWhatTheMenuWillShow()
    {
        var vm = Create();
        vm.Icon = string.Empty;

        vm.PreviewName.Should().Be("api");

        vm.PickIconCommand.Execute("⭐");

        vm.PreviewName.Should().Be("⭐ api");
    }

    [Fact]
    public void Presets_AreValidDistinctColours()
    {
        WorkspaceColor.Presets.Should().OnlyHaveUniqueItems();
        foreach (var hex in WorkspaceColor.Presets)
        {
            WorkspaceColor.TryNormalize(hex, out var normalized).Should().BeTrue(hex);
            normalized.Should().Be(hex, "presets are stored in the canonical form");
        }
    }
}

public class WorkspacePropertiesWiringTests
{
    [Fact]
    public void Properties_TookOverTheRenameShortcut_AndThePaletteRow()
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(Key.R, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("WorkspaceProperties");
        CommandCatalog.All.Select(c => c.Id).Should().Contain("WorkspaceProperties")
            .And.NotContain(["WorkspaceRename", "WorkspaceIcon", "WorkspaceColor", "WorkspaceGroup"]);
    }

    [Fact]
    public void AShortcutSavedForTheOldRenameCommand_FollowsItIntoProperties()
    {
        var settings = new AppSettings();
        settings.Hotkeys["WorkspaceRename"] = "Ctrl+Alt+F12";

        var hotkeys = new HotkeyManager(settings);

        hotkeys.GetCommandId(Key.F12, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("WorkspaceProperties");
    }

    [Fact]
    public void Help_IsShiftF1_AndThePaletteIsListedToo()
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(Key.F1, ModifierKeys.Shift).Should().Be("Help");
        CommandCatalog.All.Select(c => c.Id).Should().Contain(["Help", "CommandPalette"]);
    }
}

public class SearchResultsRootTests
{
    private static SearchResultsFileSystem View()
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/D:/proj");
        fs.AddFile("/D:/proj/a.cs", [1]);
        return new SearchResultsFileSystem(fs, []);
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/New Folder", true)]
    [InlineData("/D:/proj", false)]
    [InlineData("/D:/proj/a.cs", false)]
    public void IsSyntheticPath_IsTheListItselfAndNamesDirectlyUnderIt(string path, bool synthetic)
    {
        SearchResultsFileSystem.IsSyntheticPath(new VfsPath(path)).Should().Be(synthetic);
    }

    [Fact]
    public async Task CreatingAFolderInTheList_IsRefused_NotSentToTheDriveRoot()
    {
        var view = View();

        var act = () => view.CreateDirectoryAsync(new VfsPath("/New Folder"), CancellationToken.None);

        await act.Should().ThrowAsync<SearchResultsReadOnlyException>();
    }

    [Fact]
    public async Task CopyingIntoTheList_IsRefused()
    {
        var view = View();

        var act = () => view.CopyAsync(new VfsPath("/D:/proj/a.cs"), new VfsPath("/a.cs"), null, CancellationToken.None);

        await act.Should().ThrowAsync<SearchResultsReadOnlyException>();
    }

    [Fact]
    public async Task WritingAtARealPath_StillReachesTheDisk()
    {
        var view = View();

        await view.CreateDirectoryAsync(new VfsPath("/D:/proj/sub"), CancellationToken.None);

        (await view.GetEntryAsync(new VfsPath("/D:/proj/sub"))).Should().NotBeNull();
    }
}

public class MenuHeaderPlainTextTests
{
    [Theory]
    [InlineData("_Files", "Files")]
    [InlineData("S_ettings", "Settings")]
    [InlineData("my__project", "my_project")]
    [InlineData("plain", "plain")]
    public void Plain_DropsTheAccessKeyMarker_AndKeepsDoubledUnderscores(string header, string expected)
    {
        TCV.App.Controls.MenuHeaderText.Plain(header).Should().Be(expected);
    }

    [Fact]
    public void Plain_IsTheReverseOfLiteral()
    {
        var name = "a_b__c";

        TCV.App.Controls.MenuHeaderText.Plain(TCV.App.Controls.MenuHeaderText.Literal(name)).Should().Be(name);
    }
}
