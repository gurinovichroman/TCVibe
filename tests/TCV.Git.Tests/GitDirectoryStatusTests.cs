using FluentAssertions;
using TCV.Git;

namespace TCV.Git.Tests;

/// <summary>Folding a repository-relative change list into the cells of one folder (pure).</summary>
public class GitDirectoryStatusTests
{
    private static GitChangedFile F(string path, GitFileStatus status, bool staged = false) =>
        new(path, status, staged);

    [Fact]
    public void BuildCells_FileInRoot_GetsTwoLetterShortStatus()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("a.cs", GitFileStatus.Modified) }, string.Empty);

        cells["a.cs"].Should().Be(new GitCell(GitCellKind.Modified, " M"));
    }

    [Fact]
    public void BuildCells_StagedThenModified_ShowsBothColumns()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("a.cs", GitFileStatus.Modified, staged: true), F("a.cs", GitFileStatus.Modified) },
            string.Empty);

        cells["a.cs"].Label.Should().Be("MM");
        cells["a.cs"].Kind.Should().Be(GitCellKind.Modified);
    }

    [Fact]
    public void BuildCells_OnlyStaged_IsStagedKind()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("a.cs", GitFileStatus.Added, staged: true) }, string.Empty);

        cells["a.cs"].Should().Be(new GitCell(GitCellKind.Staged, "A "));
    }

    [Fact]
    public void BuildCells_Conflict_IsUU()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("a.cs", GitFileStatus.Conflict) }, string.Empty);

        cells["a.cs"].Should().Be(new GitCell(GitCellKind.Conflict, "UU"));
    }

    [Fact]
    public void BuildCells_NestedChange_MarksTheFolderNotTheLeafName()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("sub/x.cs", GitFileStatus.Modified) }, string.Empty);

        cells.Should().ContainKey("sub");
        cells.Should().NotContainKey("x.cs");
        cells["sub"].Kind.Should().Be(GitCellKind.Modified);
    }

    [Fact]
    public void BuildCells_FolderWithConflictInside_TakesTheWorstKind()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("d/a.cs", GitFileStatus.Modified), F("d/b.cs", GitFileStatus.Conflict) }, string.Empty);

        cells["d"].Kind.Should().Be(GitCellKind.Conflict);
    }

    [Fact]
    public void BuildCells_WholeUntrackedDirectory_IsUntrackedNotModified()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("newdir/", GitFileStatus.Untracked) }, string.Empty);

        cells["newdir"].Should().Be(new GitCell(GitCellKind.Untracked, "??"));
    }

    [Fact]
    public void BuildCells_ListedSubdirectory_UsesNamesRelativeToIt()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[]
            {
                F("src/a.cs", GitFileStatus.Modified),
                F("src/inner/b.cs", GitFileStatus.Untracked),
                F("other/c.cs", GitFileStatus.Modified),
            },
            "src/");

        cells.Keys.Should().BeEquivalentTo("a.cs", "inner");
    }

    [Fact]
    public void BuildCells_PrefixThatOnlySharesTextWithAFolder_DoesNotMatch()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("src2/a.cs", GitFileStatus.Modified) }, "src");

        cells.Should().BeEmpty();
    }

    [Fact]
    public void BuildCells_NamesAreCaseInsensitive()
    {
        var cells = GitDirectoryStatus.BuildCells(
            new[] { F("Readme.md", GitFileStatus.Modified) }, string.Empty);

        cells.ContainsKey("README.MD").Should().BeTrue();
    }

    [Fact]
    public void Get_UnknownName_IsBlank()
    {
        GitDirectoryStatus.None.Get("nothing").Should().Be(default(GitCell));
    }
}
