using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>Covers <see cref="FileListViewModel.SelectByMask"/> (the Ctrl+Num+/Num- feature).</summary>
public class SelectByMaskTests
{
    private static async Task<FileListViewModel> CreateWithFiles(params string[] names)
    {
        var fs = new InMemoryFileSystem("file");
        foreach (var n in names)
            fs.AddFile($"/C:/work/{n}", new byte[] { 1 });

        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);
        foreach (var n in names)
        {
            var entry = await fs.GetEntryAsync(new VfsPath($"/C:/work/{n}"));
            list.Files.Add(new FileEntryViewModel(entry!));
        }
        return list;
    }

    [Fact]
    public async Task SelectByMask_SelectsMatchingExtension()
    {
        var list = await CreateWithFiles("a.txt", "b.txt", "c.md", "d.png");

        list.SelectByMask("*.txt", select: true);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("a.txt", "b.txt");
    }

    [Fact]
    public async Task SelectByMask_SupportsMultipleSemicolonSeparatedMasks()
    {
        var list = await CreateWithFiles("a.txt", "b.md", "c.png");

        list.SelectByMask("*.txt; *.md", select: true);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("a.txt", "b.md");
    }

    [Fact]
    public async Task SelectByMask_QuestionMark_MatchesExactlyOneCharacter()
    {
        var list = await CreateWithFiles("a1.txt", "a10.txt", "b1.txt");

        list.SelectByMask("a?.txt", select: true);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("a1.txt");
    }

    [Fact]
    public async Task SelectByMask_Unselect_RemovesOnlyMatchesFromExistingSelection()
    {
        var list = await CreateWithFiles("a.txt", "b.txt", "c.log");
        list.SelectAll();

        list.SelectByMask("*.txt", select: false);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("c.log");
    }

    [Fact]
    public async Task SelectByMask_StarDotStar_SelectsEveryFileIncludingExtensionless()
    {
        var list = await CreateWithFiles("a.txt", "README", "Makefile", "b.png");

        list.SelectByMask("*.*", select: true);

        list.SelectedFiles.Select(f => f.Name)
            .Should().BeEquivalentTo("a.txt", "README", "Makefile", "b.png");
    }

    [Fact]
    public async Task SelectByMask_BlankMask_IsNoOp()
    {
        var list = await CreateWithFiles("a.txt", "b.txt");

        list.SelectByMask("   ", select: true);

        list.SelectedFiles.Should().BeEmpty();
    }
}
