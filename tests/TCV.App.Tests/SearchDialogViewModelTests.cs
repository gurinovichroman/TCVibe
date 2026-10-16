using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using TCV.App.FileSystem;
using TCV.App.Services;
using TCV.App.ViewModels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Search;

namespace TCV.App.Tests;

/// <summary>The search window's logic: validation, caps, cancellation, presets, hit clean-up.</summary>
public sealed class SearchDialogViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-searchvm-" + Guid.NewGuid().ToString("N"));

    public SearchDialogViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SearchDialogViewModel Create(InMemoryFileSystem vfs, string start = "/proj") =>
        new(vfs, start, archivePlugin: null, presetStore: new SearchPresetStore(Path.Combine(_dir, "presets.json")));

    private static InMemoryFileSystem Project(int files = 3)
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/proj");
        for (var i = 0; i < files; i++)
        {
            fs.AddFile($"/proj/f{i}.txt", Encoding.UTF8.GetBytes("needle"));
        }

        return fs;
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_FolderThatDoesNotExist_ReportsItAndSearchesNothing()
    {
        var vm = Create(Project(), start: "/nowhere");

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Search_PathMissing");
        vm.Results.Should().BeEmpty();
        vm.IsSearching.Should().BeFalse();
    }

    [Fact]
    public void BuildCriteria_WhitespaceOnlyContent_IsSearchedFor()
    {
        var vm = Create(Project());
        vm.ContentText = "  ";

        vm.BuildCriteria().ContentText.Should().Be("  ", "a double space is a pattern, not an empty box");
    }

    [Theory]
    [InlineData("10 apples")]
    [InlineData("-5")]
    [InlineData("MB")]
    public async Task Start_UnparseableSize_IsReportedNotIgnored(string text)
    {
        var vm = Create(Project());
        vm.MinSizeText = text;

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Search_BadSize");
    }

    [Fact]
    public async Task Start_MinimumAboveMaximum_IsReported()
    {
        var vm = Create(Project());
        vm.MinSizeText = "2MB";
        vm.MaxSizeText = "1MB";

        await vm.StartCommand.ExecuteAsync(null);

        vm.StatusText.Should().Contain("Search_SizeRange");
    }

    [Fact]
    public async Task Start_MalformedRegex_IsAStatusMessageNotACrash()
    {
        var vm = Create(Project());
        vm.ContentText = "(";
        vm.ContentIsRegex = true;

        await vm.StartCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.StatusText.Should().Contain("Search_RegexError");
        vm.IsSearching.Should().BeFalse();
    }

    [Theory]
    [InlineData("", true, null)]
    [InlineData("  ", true, null)]
    [InlineData("1024", true, 1024L)]
    [InlineData("1,024", true, 1024L)]
    [InlineData("10MB", true, 10L * 1024 * 1024)]
    [InlineData("1.5 GB", true, 1610612736L)]
    [InlineData("512k", true, 512L * 1024)]
    [InlineData("3 kb", true, 3L * 1024)]
    [InlineData("abc", false, null)]
    [InlineData("-1", false, null)]
    [InlineData("1.2.3", false, null)]
    public void SizeParser_HandlesUnitsBlanksAndGarbage(string text, bool ok, long? expected)
    {
        SizeParser.TryParse(text, out var bytes).Should().Be(ok);
        bytes.Should().Be(expected);
    }

    // ── Running ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Start_FindsMatchesAndReportsTheCount()
    {
        var vm = Create(Project(3));
        vm.ContentText = "needle";

        await vm.StartCommand.ExecuteAsync(null);

        vm.Results.Should().HaveCount(3);
        vm.StatusText.Should().Contain("Search_Done");
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Start_MoreHitsThanTheCap_StopsAtTheCapAndSaysSo()
    {
        var fs = Project(SearchDialogViewModel.DefaultResultCap + 25);
        var vm = Create(fs);
        vm.ContentText = "needle";

        await vm.StartCommand.ExecuteAsync(null);

        vm.Results.Should().HaveCount(SearchDialogViewModel.DefaultResultCap);
        vm.StatusText.Should().Contain("Search_Truncated");
    }

    [Fact]
    public async Task Dispose_WhileSearching_CancelsTheWalk()
    {
        var release = new TaskCompletionSource();
        var listed = new TaskCompletionSource();
        var mock = new Mock<IVirtualFileSystem>();
        mock.Setup(v => v.GetEntryAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Dir("/proj"));
        mock.Setup(v => v.ListAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns(async (VfsPath _, CancellationToken ct) =>
            {
                listed.TrySetResult();
                await release.Task.WaitAsync(ct);
                return (IReadOnlyList<IFileEntry>)Array.Empty<IFileEntry>();
            });
        var vm = new SearchDialogViewModel(mock.Object, "/proj", null, new SearchPresetStore(Path.Combine(_dir, "p.json")));

        var running = vm.StartCommand.ExecuteAsync(null);
        await listed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        vm.IsSearching.Should().BeTrue();

        vm.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        vm.IsSearching.Should().BeFalse();
        vm.StatusText.Should().Contain("Search_Stopped");
        release.TrySetResult();
    }

    private static IFileEntry Dir(string path)
    {
        var entry = new Mock<IFileEntry>();
        entry.SetupGet(e => e.Path).Returns(new VfsPath(path));
        entry.SetupGet(e => e.Name).Returns(path.TrimStart('/'));
        entry.SetupGet(e => e.IsDirectory).Returns(true);
        entry.SetupGet(e => e.Attributes).Returns(FileAttributes.Directory);
        return entry.Object;
    }

    // ── Result actions ────────────────────────────────────────────────────────

    [Fact]
    public async Task PruneMissingAsync_DropsRowsWhoseFileIsGone()
    {
        var fs = Project(3);
        var vm = Create(fs);
        vm.ContentText = "needle";
        await vm.StartCommand.ExecuteAsync(null);

        await fs.DeleteAsync(new VfsPath("/proj/f1.txt"), false, CancellationToken.None);
        await vm.PruneMissingAsync();

        vm.Results.Select(r => r.Name).Should().BeEquivalentTo("f0.txt", "f2.txt");
    }

    [Fact]
    public async Task DeleteResults_RaisesOneRequestPerFile_NotPerHit()
    {
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/proj");
        fs.AddFile("/proj/a.txt", Encoding.UTF8.GetBytes("needle\nneedle\nneedle"));
        var vm = Create(fs);
        vm.ContentText = "needle";
        await vm.StartCommand.ExecuteAsync(null);
        vm.Results.Should().HaveCount(3);
        foreach (var row in vm.Results)
        {
            vm.SelectedResults.Add(row);
        }

        SearchResultActionRequest? request = null;
        vm.ActionRequested += (_, r) => request = r;

        vm.DeleteResultsCommand.Execute(null);

        request.Should().NotBeNull();
        request!.Results.Should().ContainSingle("copying or deleting the same file three times is never meant");
    }

    [Fact]
    public void DeleteResults_NothingSelected_SaysSo()
    {
        var vm = Create(Project());

        vm.DeleteResultsCommand.Execute(null);

        vm.StatusText.Should().Contain("Search_NothingSelected");
    }

    // ── Presets ───────────────────────────────────────────────────────────────

    [Fact]
    public void Preset_KeepsTheDateFilters()
    {
        var vm = Create(Project());
        vm.ModifiedAfter = new DateTime(2026, 1, 2);
        vm.ModifiedBefore = new DateTime(2026, 3, 4);

        vm.SavePreset("recent").Should().BeTrue();
        vm.ModifiedAfter = null;
        vm.ModifiedBefore = null;
        vm.SelectedPreset = vm.Presets.First(p => p.Name != "recent");   // a different preset first: re-selecting the same one is no change
        vm.ModifiedAfter = null;
        vm.ModifiedBefore = null;
        vm.SelectedPreset = vm.Presets.First(p => p.Name == "recent");

        vm.ModifiedAfter.Should().Be(new DateTime(2026, 1, 2));
        vm.ModifiedBefore.Should().Be(new DateTime(2026, 3, 4));
    }

    [Fact]
    public void Preset_SizeIsRestoredAsBytes()
    {
        var vm = Create(Project());
        vm.MinSizeText = "2KB";
        vm.SavePreset("big").Should().BeTrue();
        vm.MinSizeText = string.Empty;

        vm.SelectedPreset = vm.Presets.First(p => p.Name != "big");
        vm.MinSizeText = string.Empty;
        vm.SelectedPreset = vm.Presets.First(p => p.Name == "big");

        vm.MinSizeText.Should().Be("2048");
    }

    // ── Hits panel ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchResultsFileSystem_DropsHitsThatNoLongerExist()
    {
        var fs = Project(3);
        var hits = (await fs.ListAsync(new VfsPath("/proj"))).Where(e => !e.IsDirectory).ToList();
        var view = new SearchResultsFileSystem(fs, hits);

        (await view.ListAsync(new VfsPath("/"))).Should().HaveCount(3);
        await fs.DeleteAsync(new VfsPath("/proj/f0.txt"), false, CancellationToken.None);

        var after = await view.ListAsync(new VfsPath("/"));

        after.Select(e => e.Name).Should().BeEquivalentTo("f1.txt", "f2.txt");
    }
}
