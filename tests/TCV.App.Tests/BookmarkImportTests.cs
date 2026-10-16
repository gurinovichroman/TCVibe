using System.IO;
using CommunityToolkit.Mvvm.Input;
using FluentAssertions;
using Moq;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

public class BookmarkMergeTests
{
    private static Bookmark B(string name, string path, string group = "") =>
        new() { Name = name, Path = path, Group = group };

    [Fact]
    public void NewOnes_SkipsWhatTheListAlreadyHas_InFileOrder()
    {
        var existing = new[] { B("Work", @"C:\work") };
        var imported = new[] { B("Docs", @"D:\docs"), B("Work", @"C:\work"), B("Src", @"C:\src") };

        BookmarkMerge.NewOnes(existing, imported).Select(b => b.Name).Should().Equal("Docs", "Src");
    }

    [Theory]
    [InlineData("work", @"c:\WORK")]
    [InlineData("  Work ", @"C:\work\")]
    [InlineData("Work", @"C:/work/")]
    public void NewOnes_SameEntryDifferentlySpelled_IsADuplicate(string name, string path)
    {
        var existing = new[] { B("Work", @"C:\work") };

        BookmarkMerge.NewOnes(existing, [B(name, path)]).Should().BeEmpty();
    }

    [Fact]
    public void NewOnes_SameFolderUnderAnotherName_IsKept()
    {
        // Dropping it would lose a name the user chose.
        var existing = new[] { B("Work", @"C:\work") };

        BookmarkMerge.NewOnes(existing, [B("Job", @"C:\work")]).Should().ContainSingle();
    }

    [Fact]
    public void NewOnes_OnlyTheGroupDiffers_IsADuplicate()
    {
        var existing = new[] { B("Work", @"C:\work", "A") };

        BookmarkMerge.NewOnes(existing, [B("Work", @"C:\work", "B")]).Should().BeEmpty();
    }

    [Fact]
    public void NewOnes_DuplicatesInsideTheFile_AreAddedOnce()
    {
        var imported = new[] { B("Docs", @"D:\docs"), B("docs", @"D:\Docs\") };

        BookmarkMerge.NewOnes([], imported).Should().ContainSingle().Which.Name.Should().Be("Docs");
    }

    [Fact]
    public void IsSame_ComparesNameAndFolderOnly()
    {
        BookmarkMerge.IsSame(B("A", @"C:\a", "x"), B("a", @"c:\A\", "y")).Should().BeTrue();
        BookmarkMerge.IsSame(B("A", @"C:\a"), B("A", @"C:\b")).Should().BeFalse();
    }
}

/// <summary>
/// Import in the bookmarks manager (6.55): a list with bookmarks is not replaced without a question,
/// and merging is offered as the answer that loses nothing.
/// </summary>
public sealed class BookmarkImportTests : IDisposable
{
    private readonly Mock<IDialogService> _dialogs = new();
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"tcv-bm-{Guid.NewGuid():N}.json");

    public BookmarkImportTests()
    {
        _dialogs.Setup(d => d.PickOpenFile(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(_file);
        _dialogs.Setup(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { /* best effort */ }
    }

    private BookmarksManagerDialogViewModel Create(params (string Name, string Path)[] bookmarks)
    {
        var settings = new AppSettings();
        foreach (var (name, path) in bookmarks)
        {
            settings.Bookmarks.Add(new Bookmark { Name = name, Path = path });
        }

        return new BookmarksManagerDialogViewModel(settings, _dialogs.Object, @"C:\work", persist: () => { });
    }

    private void Answer(ConfirmChoice choice) =>
        _dialogs.Setup(d => d.ChooseAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()))
            .ReturnsAsync(choice);

    private async Task Import(BookmarksManagerDialogViewModel vm, string json)
    {
        await File.WriteAllTextAsync(_file, json);
        await ((IAsyncRelayCommand)vm.ImportBookmarksCommand).ExecuteAsync(null);
    }

    private const string TwoBookmarks = """
        [ { "Name": "Old", "Path": "C:\\old" }, { "Name": "New", "Path": "C:\\new" } ]
        """;

    [Fact]
    public async Task Import_IntoAListWithBookmarks_AsksFirst()
    {
        Answer(ConfirmChoice.Cancel);
        var vm = Create(("Old", @"C:\old"));

        await Import(vm, TwoBookmarks);

        _dialogs.Verify(d => d.ChooseAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), true), Times.Once);
    }

    [Fact]
    public async Task Import_Cancelled_KeepsTheList()
    {
        Answer(ConfirmChoice.Cancel);
        var vm = Create(("Mine", @"C:\mine"));

        await Import(vm, TwoBookmarks);

        vm.Items.Select(i => i.DisplayName).Should().Equal("Mine");
    }

    [Fact]
    public async Task Import_Merge_AddsOnlyTheNewOnes_AndSelectsTheFirstOfThem()
    {
        Answer(ConfirmChoice.Confirm);
        var vm = Create(("Old", @"C:\old"), ("Mine", @"C:\mine"));

        await Import(vm, TwoBookmarks);

        vm.Items.Select(i => i.DisplayName).Should().Equal("Old", "Mine", "New");
        vm.SelectedItem!.DisplayName.Should().Be("New");
    }

    [Fact]
    public async Task Import_Replace_LeavesOnlyTheImportedOnes()
    {
        Answer(ConfirmChoice.Alternate);
        var vm = Create(("Mine", @"C:\mine"));

        await Import(vm, TwoBookmarks);

        vm.Items.Select(i => i.DisplayName).Should().Equal("Old", "New");
    }

    [Fact]
    public async Task Import_IntoAnEmptyList_DoesNotAsk()
    {
        var vm = Create();

        await Import(vm, TwoBookmarks);

        vm.Items.Should().HaveCount(2);
        _dialogs.Verify(d => d.ChooseAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>()), Times.Never);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[ null ]")]
    public async Task Import_FileWithoutBookmarks_SaysSo_AndKeepsTheList(string json)
    {
        var vm = Create(("Mine", @"C:\mine"));

        await Import(vm, json);

        vm.Items.Select(i => i.DisplayName).Should().Equal("Mine");
        _dialogs.Verify(d => d.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Import_ThenCancelTheManager_LeavesTheSettingsUntouched()
    {
        Answer(ConfirmChoice.Alternate);
        var settings = new AppSettings();
        settings.Bookmarks.Add(new Bookmark { Name = "Mine", Path = @"C:\mine" });
        var vm = new BookmarksManagerDialogViewModel(settings, _dialogs.Object, @"C:\work", persist: () => { });

        await Import(vm, TwoBookmarks);
        vm.CancelCommand.Execute(null);

        settings.Bookmarks.Should().ContainSingle().Which.Name.Should().Be("Mine");
    }
}

/// <summary>
/// A <c>--workspace</c> guest window (TRAP-22) does not write settings.json; the bookmarks manager
/// says so rather than letting a bookmark vanish when the window closes (6.55).
/// </summary>
public class BookmarksGuestWindowTests
{
    [Fact]
    public void IsGuestWindow_WithoutPersistence_IsTrue()
    {
        var vm = new BookmarksManagerDialogViewModel(new AppSettings(), Mock.Of<IDialogService>(), @"C:\x", persist: null);

        vm.IsGuestWindow.Should().BeTrue();
    }

    [Fact]
    public void IsGuestWindow_WithPersistence_IsFalse()
    {
        var vm = new BookmarksManagerDialogViewModel(new AppSettings(), Mock.Of<IDialogService>(), @"C:\x", persist: () => { });

        vm.IsGuestWindow.Should().BeFalse();
    }
}
