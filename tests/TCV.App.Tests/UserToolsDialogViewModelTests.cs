using FluentAssertions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.Tools;

namespace TCV.App.Tests;

/// <summary>The user-tools manager edits copies, validates the list, and writes back only on OK.</summary>
public class UserToolsDialogViewModelTests
{
    private static UserToolsPlugin PluginWith(params UserTool[] tools)
    {
        var plugin = new UserToolsPlugin();
        plugin.Tools.AddRange(tools);
        return plugin;
    }

    private static UserTool Tool(string name, string command = "tool.exe", string? hotkey = null) =>
        new() { Name = name, Command = command, Hotkey = hotkey };

    // ── Copies, not live objects ──────────────────────────────────────────────

    [Fact]
    public void Editing_DoesNotTouchThePluginUntilOk()
    {
        var original = Tool("Build");
        var plugin = PluginWith(original);
        var vm = new UserToolsDialogViewModel(plugin);

        vm.EditName = "Renamed";
        vm.EditCommand = "other.exe";

        original.Name.Should().Be("Build");
        original.Command.Should().Be("tool.exe");
        plugin.Tools.Should().ContainSingle().Which.Should().BeSameAs(original);
    }

    [Fact]
    public void Cancel_LeavesTheToolsExactlyAsTheyWere()
    {
        var plugin = PluginWith(Tool("Build"), Tool("Test"));
        var vm = new UserToolsDialogViewModel(plugin);
        bool? saved = null;
        vm.CloseRequested += (_, s) => saved = s;

        vm.EditName = "Changed";
        vm.RemoveCommand.Execute(null);
        vm.CancelCommand.Execute(null);

        saved.Should().BeFalse();
        plugin.Tools.Select(t => t.Name).Should().Equal("Build", "Test");
    }

    [Fact]
    public void SwitchingTheSelection_KeepsWhatWasTyped()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("A"), Tool("B")));
        var first = vm.ToolItems[0];

        vm.EditCommand = "edited-a.exe";           // typed, "Apply" never pressed
        vm.SelectedItem = vm.ToolItems[1];
        vm.SelectedItem = first;

        vm.EditCommand.Should().Be("edited-a.exe");
    }

    [Fact]
    public void Save_WritesTheCopiesBack_InTheDialogsOrder()
    {
        var plugin = PluginWith(Tool("A"), Tool("B"));
        var vm = new UserToolsDialogViewModel(plugin);
        bool? saved = null;
        vm.CloseRequested += (_, s) => saved = s;

        vm.EditArguments = "--flag";
        vm.SelectedItem = vm.ToolItems[1];
        vm.MoveUpCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        saved.Should().BeTrue();
        plugin.Tools.Select(t => t.Name).Should().Equal("B", "A");
        plugin.Tools.Single(t => t.Name == "A").Arguments.Should().Be("--flag");
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void Save_EmptyName_IsBlockedWithAMessage()
    {
        var plugin = PluginWith(Tool("A"));
        var vm = new UserToolsDialogViewModel(plugin);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.EditName = "  ";
        vm.SaveCommand.Execute(null);

        closed.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.ErrorText.Should().Contain("UserTools_ErrorNoName");
    }

    [Fact]
    public void Save_DuplicateNames_AreBlocked_CaseInsensitively()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("Build"), Tool("Test")));

        vm.SelectedItem = vm.ToolItems[1];
        vm.EditName = "build";

        vm.HasError.Should().BeTrue();
        vm.ErrorText.Should().Contain("UserTools_ErrorDuplicate");
    }

    [Fact]
    public void Save_MissingCommand_IsBlocked()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("Build")));

        vm.EditCommand = string.Empty;

        vm.ErrorText.Should().Contain("UserTools_ErrorNoCommand");
    }

    [Theory]
    [InlineData("Ctrl+NoSuchKey")]
    [InlineData("Ctrl+K Ctrl+T Ctrl+X")]
    public void Save_InvalidHotkey_IsBlocked(string hotkey)
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("Build")));

        vm.EditHotkey = hotkey;

        vm.ErrorText.Should().Contain("UserTools_ErrorHotkey");
    }

    [Fact]
    public void Save_TwoToolsOnTheSameHotkey_IsBlocked()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("A", hotkey: "Ctrl+F1"), Tool("B")));

        vm.SelectedItem = vm.ToolItems[1];
        vm.EditHotkey = "ctrl+f1";

        vm.ErrorText.Should().Contain("UserTools_ErrorHotkeyTwice");
    }

    [Fact]
    public void ValidList_HasNoError_AndAnEmptyHotkeyIsFine()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("A", hotkey: "Ctrl+F1"), Tool("B")));

        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public void HotkeyOwnedByABuiltInCommand_WarnsButDoesNotBlock()
    {
        var vm = new UserToolsDialogViewModel(
            PluginWith(Tool("A")),
            (first, _) => first.Key == System.Windows.Input.Key.F5 ? "Copy" : null);

        vm.EditHotkey = "F5";

        vm.HasWarning.Should().BeTrue();
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public void HotkeyOwnedByAnotherUserTool_IsNotAWarning()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("A")), (_, _) => "UserTool:B");

        vm.EditHotkey = "Ctrl+F1";

        vm.HasWarning.Should().BeFalse();
    }

    // ── Add / remove ──────────────────────────────────────────────────────────

    [Fact]
    public void Add_TwiceGivesDistinctNames_SoTheListNeverStartsInvalid()
    {
        var vm = new UserToolsDialogViewModel(PluginWith());

        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);

        vm.ToolItems.Select(t => t.Tool.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Remove_SelectsTheNeighbour()
    {
        var vm = new UserToolsDialogViewModel(PluginWith(Tool("A"), Tool("B"), Tool("C")));
        vm.SelectedItem = vm.ToolItems[1];

        vm.RemoveCommand.Execute(null);

        vm.SelectedItem!.Tool.Name.Should().Be("A");
        vm.ToolItems.Should().HaveCount(2);
    }

    [Theory]
    [InlineData("Ctrl+F1", true)]
    [InlineData("Ctrl+K Ctrl+T", true)]
    [InlineData("F5", true)]
    [InlineData("", false)]
    [InlineData("Ctrl+", false)]
    [InlineData("A B C", false)]
    public void TryParseGesture_AcceptsOneOrTwoStrokes(string text, bool valid)
    {
        UserToolsDialogViewModel.TryParseGesture(text, out _, out _).Should().Be(valid);
    }
}
