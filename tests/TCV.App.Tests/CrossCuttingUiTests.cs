using FluentAssertions;
using TCV.App.Commands;
using TCV.App.Controls;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

public class MenuHeaderTextTests
{
    [Theory]
    [InlineData("my_project", "my__project")]
    [InlineData("_a_b_", "__a__b__")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Literal_UserText_DoublesEveryUnderscore(string? input, string expected) =>
        MenuHeaderText.Literal(input).Should().Be(expected);

    [Theory]
    [InlineData("Workspace _terminal", "npm run dev", "Workspace _terminal — npm run dev")]
    [InlineData("Workspace _terminal", "  npm run my_task ", "Workspace _terminal — npm run my__task")]
    [InlineData("Workspace _terminal", "", "Workspace _terminal")]
    [InlineData("Workspace _terminal", "   ", "Workspace _terminal")]
    [InlineData("Workspace _terminal", null, "Workspace _terminal")]
    public void WithDetail_KeepsTheAccessKeyAndShowsTheDetailAsTyped(string header, string? detail, string expected) =>
        MenuHeaderText.WithDetail(header, detail).Should().Be(expected);

    [Fact]
    public void WithDetail_LongDetail_IsShortenedToTheLimit()
    {
        var header = MenuHeaderText.WithDetail("T", new string('x', 100));

        var detail = header["T — ".Length..];
        detail.Should().HaveLength(MenuHeaderText.MaxDetailLength).And.EndWith("…");
    }

    [Fact]
    public void WithDetail_LineBreaks_KeepTheItemOnOneLine() =>
        MenuHeaderText.WithDetail("T", "npm ci\r\nnpm run dev\n").Should().Be("T — npm ci  npm run dev");

    [Fact]
    public void WithDetail_CutAtAnEmoji_KeepsTheEmojiWhole()
    {
        // 🚀 is two chars; placed where the cut falls, half of it would be left.
        var header = MenuHeaderText.WithDetail("T", new string('x', MenuHeaderText.MaxDetailLength - 2) + "🚀 and more");

        var detail = header["T — ".Length..];
        detail.Should().Be(new string('x', MenuHeaderText.MaxDetailLength - 2) + "…");
        detail.Any(char.IsSurrogate).Should().BeFalse();
    }
}

public class CommandCatalogGestureTests
{
    private static IReadOnlyList<CommandPaletteItem> Items(params (string CommandId, string Gesture)[] overrides)
    {
        var settings = new AppSettings();
        foreach (var (commandId, gesture) in overrides)
            settings.Hotkeys[commandId] = gesture;
        return CommandCatalog.BuildItems(new HotkeyManager(settings), "en");
    }

    [Theory]
    [InlineData("ToggleSplit", "Ctrl+/")]
    [InlineData("GoToRoot", "Ctrl+\\")]
    [InlineData("NavigateUp", "Backspace")]
    [InlineData("Properties", "Alt+Enter")]
    [InlineData("SelectAll", "Ctrl+Num +")]
    [InlineData("Workspace1", "Alt+1")]
    [InlineData("DeletePermanent", "Shift+Del")]
    [InlineData("AiRename", "Ctrl+K Ctrl+N")]
    public void BuildItems_DefaultBindings_ShowKeyCapsNotWpfKeyNames(string commandId, string expected) =>
        Items().Single(i => i.Id == commandId).Gesture.Should().Be(expected);

    [Fact]
    public void BuildItems_ReboundCommand_ShowsTheNewGesture() =>
        Items(("View", "Ctrl+F3")).Single(i => i.Id == "View").Gesture.Should().Be("Ctrl+F3");

    [Fact]
    public void BuildItems_Titles_CarryNoHardcodedShortcut() =>
        Items().Should().NotContain(i => i.Title.Contains("(F3"));
}

public class ConfirmDialogViewModelTests
{
    private static ConfirmDialogViewModel Create() => new("Title", "Remove it?", "Remove", "Cancel", isWarning: true);

    [Fact]
    public void Confirm_RaisesCloseWithTrue()
    {
        var vm = Create();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.ConfirmCommand.Execute(null);

        closed.Should().Be(1);
        vm.DialogResult.Should().BeTrue();
    }

    [Fact]
    public void Cancel_RaisesCloseWithFalse()
    {
        var vm = Create();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.CancelCommand.Execute(null);

        closed.Should().Be(1);
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void Constructor_KeepsTheVerbAndWarning()
    {
        var vm = Create();

        vm.ConfirmText.Should().Be("Remove");
        vm.CancelText.Should().Be("Cancel");
        vm.IsWarning.Should().BeTrue();
        vm.DialogResult.Should().BeFalse("nothing is confirmed until the button is pressed");
    }
}
