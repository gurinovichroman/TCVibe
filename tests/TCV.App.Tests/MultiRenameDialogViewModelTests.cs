using FluentAssertions;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>Multi-Rename dialog logic that sits above the engine: rule wiring, per-folder conflicts, the date.</summary>
public sealed class MultiRenameDialogViewModelTests
{
    private static async Task<List<IFileEntry>> EntriesAsync(InMemoryFileSystem fs, params string[] paths)
    {
        var entries = new List<IFileEntry>();
        foreach (var path in paths)
        {
            entries.Add((await fs.GetEntryAsync(new VfsPath(path)))!);
        }

        return entries;
    }

    [Fact]
    public async Task RegexFind_SingleBlank_ReplacesSpaces()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/docs/my report.txt", [1]);
        var vm = new MultiRenameDialogViewModel(await EntriesAsync(fs, "/docs/my report.txt"), fs)
        {
            RegexFind = " ",
            RegexReplace = "_",
        };

        vm.PreviewItems.Should().ContainSingle().Which.NewName.Should().Be("my_report.txt");
    }

    [Fact]
    public async Task Preview_SameNewNameInDifferentFolders_IsNoConflict()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/readme.md", [1]);
        fs.AddFile("/b/readme.md", [1]);
        var vm = new MultiRenameDialogViewModel(await EntriesAsync(fs, "/a/readme.md", "/b/readme.md"), fs)
        {
            TemplateText = "[N]_old.[E]",
        };

        vm.PreviewItems.Should().HaveCount(2).And.OnlyContain(p => !p.HasConflict,
            "a batch fed from search results spans folders, and a name is only taken within one");
    }

    [Fact]
    public async Task Preview_NameTakenInTheFilesOwnFolder_IsExistsInFolder()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/y.txt", [1]);
        fs.AddFile("/b/y_new.txt", [1]);   // outside the batch, in b only
        var vm = new MultiRenameDialogViewModel(await EntriesAsync(fs, "/a/x.txt", "/b/y.txt"), fs)
        {
            TemplateText = "[N]_new.[E]",
        };

        vm.PreviewItems.Single(p => p.OldName == "x.txt").HasConflict.Should().BeFalse();
        vm.PreviewItems.Single(p => p.OldName == "y.txt").HasConflict.Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_TimePlaceholder_RenamesToExactlyWhatThePreviewShowed()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/shot.png", [1]);
        var vm = new MultiRenameDialogViewModel(
            await EntriesAsync(fs, "/d/shot.png"), fs, clock: () => new DateTime(2020, 1, 2, 3, 4, 5))
        {
            TemplateText = "[N]_[T].[E]",
        };
        var previewed = vm.PreviewItems.Single().NewName;

        await vm.ConfirmCommand.ExecuteAsync(null);

        previewed.Should().Be("shot_03-04-05.png");
        fs.HasFile("/d/" + previewed).Should().BeTrue();
    }
}
