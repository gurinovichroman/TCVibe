using FluentAssertions;
using LibGit2Sharp;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.Git;

namespace TCV.Git.Tests;

/// <summary>
/// The panel-facing reads (git column, Ctrl+G diff) against a real repository. The old provider
/// held a handle and keyed its answer by leaf file name; these pin down the replacement: stateless,
/// keyed per folder, and honest about nested and untracked content.
/// </summary>
public class GitStatusProviderTests
{
    private readonly GitStatusProvider _provider = new(NullLogger<GitStatusProvider>.Instance);

    [Fact]
    public async Task GetDirectoryStatusAsync_PlainDirectory_IsNotARepository()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var status = await _provider.GetDirectoryStatusAsync(dir);

            status.IsRepository.Should().BeFalse();
            status.Cells.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_NonExistentPath_DoesNotThrow()
    {
        var status = await _provider.GetDirectoryStatusAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));

        status.IsRepository.Should().BeFalse();
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_RepositoryRoot_LabelsModifiedAndUntrackedFiles()
    {
        using var repo = new TempRepository("sp-root");
        repo.WriteFile("README.md", "changed\n");
        repo.WriteFile("new.txt");

        var status = await _provider.GetDirectoryStatusAsync(repo.Path);

        status.IsRepository.Should().BeTrue();
        status.Branch.Should().Be(repo.InitialBranch);
        status.Get("README.md").Should().Be(new GitCell(GitCellKind.Modified, " M"));
        status.Get("new.txt").Should().Be(new GitCell(GitCellKind.Untracked, "??"));
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_NestedChange_DoesNotPaintASiblingWithTheSameName()
    {
        // The old provider keyed by leaf name, so a change in sub/README.md marked ./README.md.
        using var repo = new TempRepository("sp-nested");
        repo.WriteFile("sub/README.md", "tracked\n");
        using (var raw = new Repository(repo.Path))
        {
            Commands.Stage(raw, "sub/README.md");
            raw.Commit("add sub", TempRepository.Signature, TempRepository.Signature);
        }
        repo.WriteFile("sub/README.md", "changed in sub\n");

        var root = await _provider.GetDirectoryStatusAsync(repo.Path);

        root.Get("README.md").Kind.Should().Be(GitCellKind.None, "the root README is untouched");
        root.Get("sub").Kind.Should().Be(GitCellKind.Modified, "a folder with changes inside is marked");
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_Subdirectory_ListsOnlyItsOwnChildren()
    {
        using var repo = new TempRepository("sp-sub");
        repo.WriteFile("sub/a.txt");
        repo.WriteFile("other/b.txt");

        var status = await _provider.GetDirectoryStatusAsync(Path.Combine(repo.Path, "sub"));

        status.Get("a.txt").Kind.Should().Be(GitCellKind.Untracked);
        status.Cells.Should().NotContainKey("b.txt");
        status.Cells.Should().NotContainKey("other");
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_StagedFile_IsLabelledAsStaged()
    {
        using var repo = new TempRepository("sp-staged");
        var file = repo.WriteFile("staged.txt");
        using (var raw = new Repository(repo.Path)) Commands.Stage(raw, "staged.txt");

        var status = await _provider.GetDirectoryStatusAsync(repo.Path);

        status.Get("staged.txt").Should().Be(new GitCell(GitCellKind.Staged, "A "));
        File.Exists(file).Should().BeTrue();
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_BracketsAndUnicodeInNames_AreTakenLiterally()
    {
        using var repo = new TempRepository("sp-names");
        repo.WriteFile("notes[1].md");
        repo.WriteFile("файл.txt");

        var status = await _provider.GetDirectoryStatusAsync(repo.Path);

        status.Get("notes[1].md").Kind.Should().Be(GitCellKind.Untracked);
        status.Get("файл.txt").Kind.Should().Be(GitCellKind.Untracked);
    }

    [Fact]
    public async Task GetDiffVsHeadAsync_ModifiedFile_ShowsTheChange()
    {
        using var repo = new TempRepository("sp-diff");
        var file = repo.WriteFile("README.md", "hello there\n");

        var diff = await _provider.GetDiffVsHeadAsync(file);

        diff.Text.Should().Contain("-hello").And.Contain("+hello there");
        diff.Branch.Should().Be(repo.InitialBranch);
    }

    [Fact]
    public async Task GetDiffVsHeadAsync_CleanFile_IsEmptyNotNull()
    {
        using var repo = new TempRepository("sp-diff-clean");

        var diff = await _provider.GetDiffVsHeadAsync(Path.Combine(repo.Path, "README.md"));

        diff.Text.Should().BeEmpty();
    }

    [Fact]
    public async Task GetDiffVsHeadAsync_UntrackedFile_ShowsItAsAdded()
    {
        using var repo = new TempRepository("sp-diff-new");
        var file = repo.WriteFile("brand-new.txt", "fresh\n");

        var diff = await _provider.GetDiffVsHeadAsync(file);

        diff.Text.Should().Contain("+fresh");
    }

    [Fact]
    public async Task GetDiffVsHeadAsync_OutsideARepository_ReturnsNullText()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"tcv_nogit_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var diff = await _provider.GetDiffVsHeadAsync(Path.Combine(dir, "x.txt"));

            diff.Text.Should().BeNull();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task GetDirectoryStatusAsync_AfterwardsTheRepositoryCanBeDeleted()
    {
        // No handle may outlive the call: an open one would make this delete fail on Windows.
        var repo = new TempRepository("sp-handle");
        repo.WriteFile("x.txt");
        await _provider.GetDirectoryStatusAsync(repo.Path);
        await _provider.GetDiffVsHeadAsync(Path.Combine(repo.Path, "README.md"));

        var act = () => TempRepository.DeleteTree(repo.Path);

        act.Should().NotThrow();
        Directory.Exists(repo.Path).Should().BeFalse();
    }
}
