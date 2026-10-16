using FluentAssertions;
using TCV.Archives.Engines;

namespace TCV.Archives.Tests;

/// <summary>
/// The pure part of renaming inside an archive (6.71): which stored entries change, to what, and which
/// batches are refused before anything is written.
/// </summary>
public class ArchiveRenamePlanTests
{
    private static Dictionary<string, string> Renamed(string[] stored, params (string EntryPath, string NewName)[] renames) =>
        ArchiveRenamePlan.Build(stored, renames).Changes.ToDictionary(c => stored[c.Index], c => c.StoredName);

    [Fact]
    public void Build_OneFile_ChangesOnlyThatEntry()
    {
        var changes = Renamed(["a.txt", "dir/b.txt"], ("a.txt", "c.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["a.txt"] = "c.txt" });
    }

    [Fact]
    public void Build_FileInAFolder_StaysInItsFolder()
    {
        var changes = Renamed(["dir/", "dir/b.txt"], ("/dir/b.txt", "c.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["dir/b.txt"] = "dir/c.txt" });
    }

    [Fact]
    public void Build_Folder_TakesItsEntryAndEverythingInsideAlong()
    {
        var changes = Renamed(["dir/", "dir/a.txt", "dir/sub/", "dir/sub/b.txt", "directory.txt"], ("dir", "docs"));

        changes.Should().Equal(new Dictionary<string, string>
        {
            ["dir/"] = "docs/",
            ["dir/a.txt"] = "docs/a.txt",
            ["dir/sub/"] = "docs/sub/",
            ["dir/sub/b.txt"] = "docs/sub/b.txt",
        });
    }

    [Fact]
    public void Build_FolderTheArchiveOnlyImplies_IsRenamedThroughItsFiles()
    {
        // No "dir/" entry: the folder exists only as the prefix of its files (as BCL-made ZIPs store it).
        var changes = Renamed(["dir/a.txt", "dir/sub/b.txt"], ("dir/sub", "deep"));

        changes.Should().Equal(new Dictionary<string, string> { ["dir/sub/b.txt"] = "dir/deep/b.txt" });
    }

    [Fact]
    public void Build_StoredWithBackslashes_IsFoundAndWrittenWithSlashes()
    {
        var changes = Renamed([@"dir\a.txt"], ("dir/a.txt", "b.txt"));

        changes.Should().Equal(new Dictionary<string, string> { [@"dir\a.txt"] = "dir/b.txt" });
    }

    [Fact]
    public void Build_Swap_NeedsNoTemporaryName()
    {
        var changes = Renamed(["a.txt", "b.txt"], ("a.txt", "b.txt"), ("b.txt", "a.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["a.txt"] = "b.txt", ["b.txt"] = "a.txt" });
    }

    [Fact]
    public void Build_Chain_RenamesEveryLinkFromItsOwnName()
    {
        var changes = Renamed(["1.txt", "2.txt"], ("1.txt", "2.txt"), ("2.txt", "3.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["1.txt"] = "2.txt", ["2.txt"] = "3.txt" });
    }

    [Fact]
    public void Build_FileInsideARenamedFolder_EndsUpInTheFoldersNewName()
    {
        var changes = Renamed(["dir/a.txt", "dir/c.txt"], ("dir/a.txt", "b.txt"), ("dir", "d2"));

        changes.Should().Equal(new Dictionary<string, string> { ["dir/a.txt"] = "d2/b.txt", ["dir/c.txt"] = "d2/c.txt" });
    }

    [Fact]
    public void Build_ChangeOfCaseOnly_IsARename()
    {
        var changes = Renamed(["readme.txt"], ("readme.txt", "README.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["readme.txt"] = "README.txt" });
    }

    [Fact]
    public void Build_OtherCaseWhenTheExactNameIsMissing_FindsTheEntry()
    {
        var changes = Renamed(["Docs/Readme.txt"], ("docs/readme.TXT", "a.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["Docs/Readme.txt"] = "Docs/a.txt" });
    }

    [Fact]
    public void Build_NamesDifferingOnlyByCase_RenamesTheExactOne()
    {
        var changes = Renamed(["Readme.txt", "readme.txt"], ("readme.txt", "lower.txt"));

        changes.Should().Equal(new Dictionary<string, string> { ["readme.txt"] = "lower.txt" });
    }

    [Theory]
    [InlineData("b.txt")]          // a file left alone
    [InlineData("B.TXT")]          // the same name to Windows
    [InlineData("dir")]            // a folder the archive only implies
    public void Build_NameTakenAfterTheBatch_IsRefused(string taken)
    {
        var act = () => ArchiveRenamePlan.Build(["a.txt", "b.txt", "dir/c.txt"], [("a.txt", taken)]);

        act.Should().Throw<IOException>().WithMessage($"*{taken}*");
    }

    [Fact]
    public void Build_TwoEntriesRenamedToOneName_IsRefused()
    {
        var act = () => ArchiveRenamePlan.Build(["a.txt", "b.txt"], [("a.txt", "c.txt"), ("b.txt", "c.txt")]);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Build_FolderRenamedOntoAnExistingFolder_IsRefused()
    {
        var act = () => ArchiveRenamePlan.Build(["src/a.cs", "lib/b.cs"], [("src", "lib")]);

        act.Should().Throw<IOException>();
    }

    [Fact]
    public void Build_EntryNotInTheArchive_IsRefused()
    {
        var act = () => ArchiveRenamePlan.Build(["a.txt"], [("gone.txt", "b.txt")]);

        act.Should().Throw<FileNotFoundException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("sub/b.txt")]
    [InlineData(@"..\b.txt")]
    public void Build_NewNameThatIsNoName_IsRefused(string newName)
    {
        var act = () => ArchiveRenamePlan.Build(["a.txt"], [("a.txt", newName)]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_TheRoot_IsRefused()
    {
        var act = () => ArchiveRenamePlan.Build(["a.txt"], [("/", "b")]);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Build_OneEntryRenamedTwice_IsRefused()
    {
        var act = () => ArchiveRenamePlan.Build(["a.txt"], [("a.txt", "b.txt"), ("/a.txt", "c.txt")]);

        act.Should().Throw<ArgumentException>();
    }
}
