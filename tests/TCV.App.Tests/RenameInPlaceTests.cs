using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>Renaming in place: the edit the panel runs for F2 and the shell menu's Rename.</summary>
public class RenameInPlaceTests
{
    private static async Task<(FileListViewModel List, List<FileEntryViewModel> Rows)> CreateWithFiles(params string[] names)
    {
        var fs = new InMemoryFileSystem("file");
        foreach (var n in names)
        {
            fs.AddFile($"/C:/work/{n}", [1]);
        }

        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);
        var rows = new List<FileEntryViewModel>();
        foreach (var n in names)
        {
            var entry = await fs.GetEntryAsync(new VfsPath($"/C:/work/{n}"));
            var vm = new FileEntryViewModel(entry!);
            rows.Add(vm);
            list.Files.Add(vm);
        }

        list.SelectedItem = rows[0];
        return (list, rows);
    }

    [Fact]
    public async Task EditNameInPlaceAsync_Enter_CompletesWithTheTypedName()
    {
        var (list, rows) = await CreateWithFiles("notes.txt");

        var edit = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        rows[0].IsEditingName.Should().BeTrue();
        list.NameEditTarget.Should().BeSameAs(rows[0]);

        rows[0].EditedName = "todo.txt";
        list.EndNameEdit(commit: true).Should().BeTrue();

        (await edit).Should().Be("todo.txt");
        rows[0].IsEditingName.Should().BeFalse();
        list.NameEditTarget.Should().BeNull();
    }

    [Fact]
    public async Task EndNameEdit_Esc_CompletesWithNothing()
    {
        var (list, rows) = await CreateWithFiles("notes.txt");
        var edit = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        rows[0].EditedName = "todo.txt";

        list.EndNameEdit(commit: false);

        (await edit).Should().BeNull();
        rows[0].IsEditingName.Should().BeFalse();
    }

    [Theory]
    [InlineData("a:b.txt")]
    [InlineData("name.")]
    [InlineData("CON.txt")]
    public async Task EndNameEdit_NameWindowsRefuses_KeepsTheBoxOpenAndSaysWhy(string refused)
    {
        var (list, rows) = await CreateWithFiles("notes.txt");
        var edit = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        rows[0].EditedName = refused;

        list.EndNameEdit(commit: true).Should().BeFalse();

        edit.IsCompleted.Should().BeFalse();
        rows[0].IsEditingName.Should().BeTrue();
        rows[0].NameEditError.Should().NotBeNullOrEmpty();

        // Typing again clears the message; the corrected name is taken.
        rows[0].EditedName = "fixed.txt";
        rows[0].NameEditError.Should().BeNull();
        list.EndNameEdit(commit: true).Should().BeTrue();
        (await edit).Should().Be("fixed.txt");
    }

    [Fact]
    public async Task EndNameEdit_EmptyBox_EndsLikeEsc()
    {
        var (list, rows) = await CreateWithFiles("notes.txt");
        var edit = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        rows[0].EditedName = "   ";

        list.EndNameEdit(commit: true).Should().BeTrue();

        (await edit).Should().BeNull();
    }

    [Fact]
    public async Task EditNameInPlaceAsync_ASecondEdit_DropsTheFirst()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt");
        var first = list.EditNameInPlaceAsync(rows[0], rows[0].Name);

        var second = list.EditNameInPlaceAsync(rows[1], "retyped.txt");

        (await first).Should().BeNull();
        rows[0].IsEditingName.Should().BeFalse();
        rows[1].IsEditingName.Should().BeTrue();
        rows[1].EditedName.Should().Be("retyped.txt");
        second.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task EditNameInPlaceAsync_ARowOffTheCursor_BecomesTheCursor()
    {
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt", "c.txt");
        list.ToggleSelect(rows[2]);

        _ = list.EditNameInPlaceAsync(rows[2], rows[2].Name);

        list.SelectedItem.Should().BeSameAs(rows[2]);
        list.SelectedFiles.Should().ContainSingle().Which.Should().BeSameAs(rows[2]);
    }

    [Fact]
    public async Task PostponeReloadForNameEdit_OnlyWhileANameIsBeingEdited()
    {
        var (list, rows) = await CreateWithFiles("notes.txt");
        list.PostponeReloadForNameEdit().Should().BeFalse();

        _ = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        list.PostponeReloadForNameEdit().Should().BeTrue();

        rows[0].EditedName = "todo.txt";
        list.EndNameEdit(commit: true);
        list.PostponeReloadForNameEdit().Should().BeFalse();
    }

    [Fact]
    public async Task EndNameEdit_UnchangedNameWithAReloadPutOff_ReloadsTheFolder()
    {
        // Enter on the name as it was: the command renames nothing and reloads nothing, so the change
        // the watcher saw meanwhile must be loaded here or it never shows.
        var (list, rows) = await CreateWithFiles("notes.txt");
        var loads = 0;
        list.BeforeDirectoryLoad += (_, _) => loads++;
        var edit = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        list.PostponeReloadForNameEdit().Should().BeTrue();

        list.EndNameEdit(commit: true).Should().BeTrue();

        (await edit).Should().Be("notes.txt");
        loads.Should().Be(1);
    }

    [Fact]
    public async Task EndNameEdit_NewNameWithAReloadPutOff_LeavesTheReloadToTheRename()
    {
        var (list, rows) = await CreateWithFiles("notes.txt");
        var loads = 0;
        list.BeforeDirectoryLoad += (_, _) => loads++;
        _ = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        list.PostponeReloadForNameEdit();

        rows[0].EditedName = "todo.txt";
        list.EndNameEdit(commit: true);

        loads.Should().Be(0);
    }

    [Fact]
    public async Task EditNameInPlaceAsync_ReplacingAnEditWithAReloadPutOff_KeepsItForTheNewEdit()
    {
        // Run when the first edit is replaced, the reload would land just after the second box opened
        // and close it; it waits for the second edit instead.
        var (list, rows) = await CreateWithFiles("a.txt", "b.txt");
        var loads = 0;
        list.BeforeDirectoryLoad += (_, _) => loads++;
        _ = list.EditNameInPlaceAsync(rows[0], rows[0].Name);
        list.PostponeReloadForNameEdit();

        var second = list.EditNameInPlaceAsync(rows[1], rows[1].Name);
        loads.Should().Be(0);
        rows[1].IsEditingName.Should().BeTrue();

        list.EndNameEdit(commit: false);
        (await second).Should().BeNull();
        loads.Should().Be(1);
    }

    [Fact]
    public async Task EditNameInPlaceAsync_ARowNoLongerListed_CompletesWithNothing()
    {
        // The shell menu is modal and a reload can replace the rows while it is open.
        var (list, rows) = await CreateWithFiles("a.txt");
        var gone = new FileEntryViewModel(rows[0].Entry);

        var edit = list.EditNameInPlaceAsync(gone, gone.Name);

        (await edit).Should().BeNull();
        gone.IsEditingName.Should().BeFalse();
        list.NameEditTarget.Should().BeNull();
        list.SelectedItem.Should().BeSameAs(rows[0]);
    }

    [Theory]
    [InlineData("report.final.md", false, 12)]
    [InlineData(".gitignore", false, 10)]
    [InlineData("Makefile", false, 8)]
    [InlineData("my.project", true, 10)]
    public void BaseNameLength_SelectsUpToTheExtension(string name, bool isDirectory, int expected)
    {
        var entry = new FileEntry(new VfsPath($"/C:/work/{name}"), name, isDirectory, 0, DateTime.Now,
            System.IO.FileAttributes.Normal);
        var row = new FileEntryViewModel(entry) { EditedName = name };

        row.BaseNameLength.Should().Be(expected);
    }
}
