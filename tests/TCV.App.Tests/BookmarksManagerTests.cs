using System.IO;
using System.Text.Json;
using FluentAssertions;
using Moq;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

public class BookmarkValidatorTests
{
    [Theory]
    [InlineData("Work", @"C:\src\app", BookmarkIssue.None)]
    [InlineData("Share", @"\\server\share\docs", BookmarkIssue.None)]
    [InlineData("  Padded  ", @"  D:\x  ", BookmarkIssue.None)]
    [InlineData("", @"C:\x", BookmarkIssue.EmptyName)]
    [InlineData("   ", @"C:\x", BookmarkIssue.EmptyName)]
    [InlineData(null, @"C:\x", BookmarkIssue.EmptyName)]
    [InlineData("Work", "", BookmarkIssue.EmptyPath)]
    [InlineData("Work", "   ", BookmarkIssue.EmptyPath)]
    [InlineData("Work", null, BookmarkIssue.EmptyPath)]
    [InlineData("Work", @"src\app", BookmarkIssue.NotAbsolute)]
    [InlineData("Work", @"..\app", BookmarkIssue.NotAbsolute)]
    [InlineData("Work", @"\app", BookmarkIssue.NotAbsolute)]
    [InlineData("Work", @"C:app", BookmarkIssue.NotAbsolute)]
    public void Validate_ReportsWhatIsWrong(string? name, string? path, BookmarkIssue expected)
    {
        BookmarkValidator.Validate(name, path).Should().Be(expected);
    }
}

public sealed class BookmarksManagerDialogViewModelTests : IDisposable
{
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch (IOException) { /* best effort */ }
        }
    }

    private static AppSettings SettingsWith(params (string Name, string Path, string Group)[] bookmarks)
    {
        var settings = new AppSettings();
        foreach (var (name, path, group) in bookmarks)
        {
            settings.Bookmarks.Add(new Bookmark { Name = name, Path = path, Group = group });
        }

        return settings;
    }

    private BookmarksManagerDialogViewModel Create(
        AppSettings settings, string currentPath = @"C:\work", Action? persist = null) =>
        new(settings, _dialogs.Object, currentPath, persist);

    [Fact]
    public void EditingOneBookmark_AndSelectingAnother_KeepsTheEdit()
    {
        var vm = Create(SettingsWith(("A", @"C:\a", ""), ("B", @"C:\b", "")));
        vm.SelectedItem = vm.Items[0];

        vm.EditName = "Renamed";
        vm.SelectedItem = vm.Items[1];
        vm.SelectedItem = vm.Items[0];

        vm.EditName.Should().Be("Renamed", "typing must not need an Apply click to survive a selection change");
        vm.Items[0].DisplayName.Should().Be("Renamed");
    }

    [Fact]
    public void Cancel_LeavesTheSettingsUntouched()
    {
        var settings = SettingsWith(("A", @"C:\a", ""));
        var vm = Create(settings);
        vm.SelectedItem = vm.Items[0];

        vm.EditName = "Changed";
        vm.EditPath = @"C:\changed";
        vm.CancelCommand.Execute(null);

        settings.Bookmarks.Should().ContainSingle().Which.Name.Should().Be("A");
        settings.Bookmarks[0].Path.Should().Be(@"C:\a");
    }

    [Fact]
    public void Save_WritesTrimmedCopiesToTheSettings_AndPersists()
    {
        var settings = SettingsWith(("A", @"C:\a", ""));
        var persisted = 0;
        var vm = Create(settings, persist: () => persisted++);
        bool? closedWith = null;
        vm.CloseRequested += (_, ok) => closedWith = ok;
        vm.SelectedItem = vm.Items[0];

        vm.EditName = "  Alpha ";
        vm.EditGroup = " Work ";
        vm.SaveCommand.Execute(null);

        settings.Bookmarks.Should().ContainSingle();
        settings.Bookmarks[0].Name.Should().Be("Alpha");
        settings.Bookmarks[0].Group.Should().Be("Work");
        persisted.Should().Be(1);
        closedWith.Should().BeTrue();
    }

    [Fact]
    public void Save_InAGuestWindow_ChangesTheListButDoesNotWriteSettingsJson()
    {
        // TRAP-22: a --workspace window passes no persist callback.
        var settings = SettingsWith(("A", @"C:\a", ""));
        var vm = Create(settings, persist: null);
        vm.SelectedItem = vm.Items[0];
        vm.EditName = "Alpha";

        var act = () => vm.SaveCommand.Execute(null);

        act.Should().NotThrow();
        settings.Bookmarks[0].Name.Should().Be("Alpha");
    }

    [Fact]
    public void Save_WithAnInvalidBookmark_StaysOpen_AndSelectsTheOffender()
    {
        var settings = SettingsWith(("Good", @"C:\good", ""), ("Bad", @"relative\path", ""));
        var persisted = 0;
        var vm = Create(settings, persist: () => persisted++);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.SaveCommand.Execute(null);

        closed.Should().BeFalse();
        persisted.Should().Be(0);
        vm.SelectedItem.Should().BeSameAs(vm.Items[1]);
        vm.HasValidationMessage.Should().BeTrue();
        settings.Bookmarks[0].Name.Should().Be("Good");
    }

    [Fact]
    public void Add_StartsOnTheCurrentFolder_WithItsNameAsTheTitle()
    {
        var vm = Create(SettingsWith(), currentPath: @"D:\projects\api");

        vm.AddCommand.Execute(null);

        vm.SelectedItem.Should().NotBeNull();
        vm.EditName.Should().Be("api");
        vm.EditPath.Should().Be(@"D:\projects\api");
        vm.HasValidationMessage.Should().BeFalse();
    }

    [Fact]
    public void Add_WithoutACurrentFolder_ExplainsWhatIsMissing()
    {
        var vm = Create(SettingsWith(), currentPath: "");

        vm.AddCommand.Execute(null);

        vm.HasValidationMessage.Should().BeTrue("a bookmark with no path cannot be saved");
    }

    [Fact]
    public void Remove_SelectsTheNeighbour_AndMoveKeepsTheSelection()
    {
        var vm = Create(SettingsWith(("A", @"C:\a", ""), ("B", @"C:\b", ""), ("C", @"C:\c", "")));
        vm.SelectedItem = vm.Items[1];

        vm.MoveDownCommand.Execute(null);
        vm.Items.Select(i => i.DisplayName).Should().Equal("A", "C", "B");
        vm.SelectedItem!.DisplayName.Should().Be("B");
        vm.MoveDownCommand.CanExecute(null).Should().BeFalse("B is last now");

        vm.RemoveCommand.Execute(null);
        vm.Items.Select(i => i.DisplayName).Should().Equal("A", "C");
        vm.SelectedItem!.DisplayName.Should().Be("C");
    }

    [Fact]
    public async Task Import_TolerantOfNullEntriesAndFields()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tcv-bm-{Guid.NewGuid():N}.json");
        _tempFiles.Add(file);
        await File.WriteAllTextAsync(file, """
            [ null, { "Name": null, "Path": "C:\\x", "Group": null }, { "Name": "Ok", "Path": "C:\\y" } ]
            """);
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(file);
        // The list is not empty, so the import asks (6.55); «Replace» is what this test is about.
        _dialogs.Setup(d => d.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(ConfirmChoice.Alternate);
        var vm = Create(SettingsWith(("Old", @"C:\old", "")));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ImportBookmarksCommand).ExecuteAsync(null);

        vm.Items.Should().HaveCount(2);
        vm.Items[0].Bookmark.Name.Should().BeEmpty();
        vm.Items[1].Bookmark.Name.Should().Be("Ok");
    }

    [Fact]
    public async Task Import_OfAFileThatIsNotJson_ShowsAnError_AndKeepsTheList()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tcv-bm-{Guid.NewGuid():N}.json");
        _tempFiles.Add(file);
        await File.WriteAllTextAsync(file, "not json");
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(file);
        _dialogs.Setup(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
        var vm = Create(SettingsWith(("Keep", @"C:\keep", "")));

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ImportBookmarksCommand).ExecuteAsync(null);

        vm.Items.Should().ContainSingle().Which.DisplayName.Should().Be("Keep");
        _dialogs.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Export_WritesTheEditedList()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tcv-bm-{Guid.NewGuid():N}.json");
        _tempFiles.Add(file);
        _dialogs.Setup(d => d.PickSaveFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(file);
        var vm = Create(SettingsWith(("A", @"C:\a", "")));
        vm.SelectedItem = vm.Items[0];
        vm.EditName = "Edited";

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ExportBookmarksCommand).ExecuteAsync(null);

        var written = JsonSerializer.Deserialize<List<Bookmark>>(await File.ReadAllTextAsync(file));
        written.Should().ContainSingle().Which.Name.Should().Be("Edited");
    }

    [Fact]
    public void CancelledPicker_DoesNothing()
    {
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns((string?)null);
        var vm = Create(SettingsWith(("A", @"C:\a", "")));

        vm.ImportBookmarksCommand.Execute(null);

        vm.Items.Should().ContainSingle();
    }
}
