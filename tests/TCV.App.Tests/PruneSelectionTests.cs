using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>
/// Covers <see cref="FileListViewModel.PruneSelection"/>, which a directory load runs so a
/// selection cannot outlive the listing it was made in.
/// </summary>
public class PruneSelectionTests
{
    private static async Task<(FileListViewModel List, Dictionary<string, FileEntryViewModel> Vms)>
        CreateWithFiles(params string[] names)
    {
        var fs = new InMemoryFileSystem("file");
        foreach (var n in names) fs.AddFile($"/C:/work/{n}", [1]);

        var list = new FileListViewModel(fs, NullLogger<FileListViewModel>.Instance);
        var vms = new Dictionary<string, FileEntryViewModel>();
        foreach (var n in names)
        {
            var entry = await fs.GetEntryAsync(new VfsPath($"/C:/work/{n}"));
            var vm = new FileEntryViewModel(entry!);
            vms[n] = vm;
            list.Files.Add(vm);
        }
        return (list, vms);
    }

    [Fact]
    public async Task PruneSelection_DropsEntriesTheNewListingDoesNotContain()
    {
        var (list, vms) = await CreateWithFiles("a.txt", "b.txt");
        list.SelectAll();

        // The next directory holds only one of them.
        list.PruneSelection([vms["a.txt"]]);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("a.txt");
        vms["b.txt"].IsSelected.Should().BeFalse();
    }

    [Fact]
    public async Task PruneSelection_KeepsEntriesThatSurvivedTheLoad()
    {
        var (list, vms) = await CreateWithFiles("a.txt", "b.txt");
        list.SelectAll();

        list.PruneSelection(vms.Values);

        list.SelectedFiles.Select(f => f.Name).Should().BeEquivalentTo("a.txt", "b.txt");
    }

    [Fact]
    public async Task PruneSelection_ReplacedViewModelWithTheSameName_IsStillDropped()
    {
        var (list, vms) = await CreateWithFiles("a.txt");
        list.SelectAll();

        // A changed file gets a fresh view model on reload; the selection must follow the listing,
        // not the name.
        var replacement = new FileEntryViewModel(vms["a.txt"].Entry);
        list.PruneSelection([replacement]);

        list.SelectedFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task PruneSelection_EmptyListing_ClearsTheSelection()
    {
        var (list, _) = await CreateWithFiles("a.txt", "b.txt");
        list.SelectAll();

        list.PruneSelection([]);

        list.SelectedFiles.Should().BeEmpty();
    }
}
