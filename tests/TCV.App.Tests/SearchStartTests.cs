using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using TCV.App.Services;
using TCV.App.ViewModels;
using TCV.Core.PluginSdk;

namespace TCV.App.Tests;

/// <summary>Where Ctrl+F searches, and what a search through a panel's mount offers (6.55).</summary>
public sealed class SearchStartTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-searchstart-" + Guid.NewGuid().ToString("N"));

    public SearchStartTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // ── SearchStart.For ───────────────────────────────────────────────────────

    [Fact]
    public void For_PanelOnDisk_SearchesItsFolderOnDisk()
    {
        SearchStart.For(isMounted: false, showsSearchResults: false, @"D:\proj", @"D:\proj")
            .Should().Be(new SearchStart(SearchScope.Disk, @"D:\proj"));
    }

    [Fact]
    public void For_PanelOnAServer_SearchesTheServerFolderNotTheDiskBeneath()
    {
        SearchStart.For(isMounted: true, showsSearchResults: false, "/home/me/site", @"D:\proj")
            .Should().Be(new SearchStart(SearchScope.Mount, "/home/me/site"));
    }

    [Fact]
    public void For_PanelInsideAnArchive_SearchesTheArchiveFolder()
    {
        SearchStart.For(isMounted: true, showsSearchResults: false, "/src", @"D:\downloads")
            .Should().Be(new SearchStart(SearchScope.Mount, "/src"));
    }

    [Fact]
    public void For_SearchResultsList_StartsInTheDiskFolderBeneathIt()
    {
        SearchStart.For(isMounted: true, showsSearchResults: true, "/", @"D:\proj")
            .Should().Be(new SearchStart(SearchScope.Disk, @"D:\proj"));
    }

    [Fact]
    public void For_MountWithNoPathYet_StartsAtItsRoot()
    {
        SearchStart.For(isMounted: true, showsSearchResults: false, string.Empty, @"D:\proj")
            .Should().Be(new SearchStart(SearchScope.Mount, "/"));
    }

    // ── A search through a mount ──────────────────────────────────────────────

    private SearchDialogViewModel MountSearch(InMemoryFileSystem server, IArchivePlugin? archives = null) =>
        new(server, "/srv", archives, new SearchPresetStore(Path.Combine(_dir, "presets.json"))) { SearchesMount = true };

    private static InMemoryFileSystem Server()
    {
        var fs = new InMemoryFileSystem("sftp");
        fs.AddFile("/srv/www/index.html", Encoding.UTF8.GetBytes("<html>"));
        fs.AddFile("/srv/notes.txt", Encoding.UTF8.GetBytes("x"));
        return fs;
    }

    [Fact]
    public async Task MountSearch_FindsTheServersFilesThroughItsFileSystem()
    {
        var vm = MountSearch(Server());
        vm.NamePattern = "index.html";

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeFalse();
        vm.Results.Should().ContainSingle().Which.FullPath.Should().Be("/srv/www/index.html");
    }

    [Fact]
    public async Task MountSearch_HitFolder_KeepsTheServersForwardSlashes()
    {
        var vm = MountSearch(Server());
        vm.NamePattern = "index.html";

        await vm.StartCommand.ExecuteAsync(null);

        vm.Results.Single().Directory.Should().Be("/srv/www", "not \\srv\\www (TRAP-18)");
    }

    [Fact]
    public void MountSearch_StatusSaysWhereItSearches()
    {
        MountSearch(Server()).StatusText.Should().Contain("Search_ReadyInMount");
    }

    [Fact]
    public async Task MountSearch_OperationsOnHits_AreRequestedWithTheMountsEntries()
    {
        // 6.60: the window lists the hits over the mount they came from, so they are asked for, not declined.
        var vm = MountSearch(Server());
        vm.NamePattern = "*";
        await vm.StartCommand.ExecuteAsync(null);
        vm.SelectedResults.Add(vm.Results.First());
        var requests = new List<SearchResultActionRequest>();
        vm.ActionRequested += (_, request) => requests.Add(request);

        vm.CopyResultsCommand.Execute(null);
        vm.FeedResultsToPanelCommand.Execute(null);

        requests.Select(r => r.Action).Should().Equal(SearchResultAction.Copy, SearchResultAction.FeedToPanel);
        requests[0].Results.Single().FullPath.Should().StartWith("/srv/");
    }

    [Fact]
    public void MountSearch_InsideArchivesBox_DoesNotApply()
    {
        var archives = new Mock<IArchivePlugin>().Object;

        MountSearch(Server(), archives).CanSearchInsideArchives.Should().BeFalse();
        new SearchDialogViewModel(Server(), "/srv", archives, new SearchPresetStore(Path.Combine(_dir, "p2.json")))
            .CanSearchInsideArchives.Should().BeTrue("on disk the box works as before");
    }

    [Fact]
    public async Task DiskSearch_OperationsOnHits_StillReachTheApp()
    {
        var vm = new SearchDialogViewModel(Server(), "/srv", null, new SearchPresetStore(Path.Combine(_dir, "p3.json")));
        vm.NamePattern = "notes.txt";
        await vm.StartCommand.ExecuteAsync(null);
        vm.SelectedResults.Add(vm.Results.Single());
        var requested = false;
        vm.ActionRequested += (_, _) => requested = true;

        vm.CopyResultsCommand.Execute(null);

        requested.Should().BeTrue();
    }
}
