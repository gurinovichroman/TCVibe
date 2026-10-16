using FluentAssertions;
using TCV.App.Navigation;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

public class FileFinderViewModelTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    private static FileIndexEntry File(string relativePath) =>
        new("/repo/" + relativePath, relativePath, NameOf(relativePath), false);

    private static FileIndexEntry Dir(string relativePath) =>
        new("/repo/" + relativePath, relativePath, NameOf(relativePath), true);

    private static string NameOf(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        return slash < 0 ? relativePath : relativePath[(slash + 1)..];
    }

    private static FileFinderViewModel Create(
        FileUsageIndex? usage = null, params FileIndexEntry[] entries)
    {
        var vm = new FileFinderViewModel("/repo", usage);
        vm.Append(entries);
        return vm;
    }

    [Fact]
    public void EmptyQuery_ListsEverything_ShallowestFirst()
    {
        var vm = Create(null, File("src/deep/nested/app.cs"), File("readme.md"));

        vm.Results.Should().HaveCount(2);
        vm.Results[0].RelativePath.Should().Be("readme.md");
    }

    [Fact]
    public void Query_KeepsOnlyFuzzyMatches()
    {
        var vm = Create(null, File("readme.md"), File("src/app.cs"), File("LICENSE"));

        vm.Query = "app";

        vm.Results.Select(r => r.RelativePath).Should().Equal("src/app.cs");
    }

    [Fact]
    public void HasNoMatches_TrueOnlyWhenTheIndexHasEntriesButNoneMatch()
    {
        var empty = Create(null);
        empty.HasNoMatches.Should().BeFalse("an empty index is still indexing, not a failed search");

        var vm = Create(null, File("readme.md"));
        vm.HasNoMatches.Should().BeFalse();

        vm.Query = "zzz";

        vm.HasNoMatches.Should().BeTrue();
    }

    [Fact]
    public void Query_MatchesAgainstThePathToo_SoDirectoryTypingWorks()
    {
        var vm = Create(null, File("src/app.cs"), File("docs/readme.md"));

        vm.Query = "docsread";

        vm.Results.Select(r => r.RelativePath).Should().Equal("docs/readme.md");
    }

    [Fact]
    public void NameMatch_OutranksAPathOnlyMatch()
    {
        var vm = Create(null, File("core/index.ts"), File("index/other.ts"));

        vm.Query = "index";

        vm.Results[0].RelativePath.Should().Be("core/index.ts",
            "the file itself is the target, not a folder that happens to be called index");
    }

    [Fact]
    public void SelectedItem_FollowsTheHighlightedRow()
    {
        var vm = Create(null, File("a.txt"), File("b.txt"));

        vm.SelectedIndex.Should().Be(0);
        vm.SelectedItem!.Name.Should().Be("a.txt");

        vm.MoveSelection(1);
        vm.SelectedItem!.Name.Should().Be("b.txt");
    }

    [Fact]
    public void MoveSelection_ClampsToTheResultRange()
    {
        var vm = Create(null, File("a.txt"), File("b.txt"));

        vm.MoveSelection(50);
        vm.SelectedIndex.Should().Be(1);

        vm.MoveSelection(-50);
        vm.SelectedIndex.Should().Be(0);
    }

    [Fact]
    public void NoMatch_LeavesNothingSelected()
    {
        var vm = Create(null, File("a.txt"));

        vm.Query = "zzzz";

        vm.Results.Should().BeEmpty();
        vm.SelectedIndex.Should().Be(-1);
        vm.SelectedItem.Should().BeNull();
    }

    [Fact]
    public void Habit_LiftsAPreviouslyOpenedFileAboveAnEquallyGoodMatch()
    {
        var entries = new[] { File("a/config.json"), File("b/config.json") };
        var plain = Create(null, entries);
        plain.Query = "config";
        var natural = plain.Results[0].RelativePath;

        var usage = new FileUsageIndex(
            [new FileUsageEntry { Path = "/repo/b/config.json", Hits = 5, LastUsedUtc = Now }], Now);
        var ranked = Create(usage, entries);
        ranked.Query = "config";

        natural.Should().Be("a/config.json", "without history the shorter path wins the tie");
        ranked.Results[0].RelativePath.Should().Be("b/config.json", "habit breaks the tie");
    }

    [Fact]
    public void DrillDown_OnADirectory_NarrowsToIt()
    {
        var vm = Create(null, Dir("src"), File("src/app.cs"), File("readme.md"));
        vm.Query = "src";

        vm.SelectedItem!.IsDirectory.Should().BeTrue();
        vm.DrillDown().Should().BeTrue();

        vm.Scope.Should().Be("src");
        vm.Query.Should().BeEmpty("the query is spent — the next keystrokes search inside");
        vm.Results.Select(r => r.RelativePath).Should().Equal("src/app.cs");
    }

    [Fact]
    public void DrillDown_OnAFile_NarrowsToItsFolder()
    {
        var vm = Create(null, File("src/deep/app.cs"), File("src/deep/other.cs"), File("readme.md"));
        vm.Query = "app";

        vm.DrillDown().Should().BeTrue();

        vm.Scope.Should().Be("src/deep");
        vm.Results.Select(r => r.RelativePath).Should().BeEquivalentTo("src/deep/app.cs", "src/deep/other.cs");
    }

    [Fact]
    public void DrillDown_AtTheRootOfTheIndex_DoesNothing()
    {
        var vm = Create(null, File("readme.md"));

        vm.DrillDown().Should().BeFalse("there is no directory to narrow to");
        vm.Scope.Should().BeEmpty();
    }

    [Fact]
    public void DrillDown_WithNoResults_DoesNothing()
    {
        var vm = Create(null, File("readme.md"));
        vm.Query = "zzzz";

        vm.DrillDown().Should().BeFalse();
    }

    [Fact]
    public void Scope_MatchesOnThePathBelowIt()
    {
        var vm = Create(null, File("src/deep/app.cs"));
        vm.Scope = "src";

        vm.Query = "deepapp";

        vm.Results.Should().ContainSingle("inside a scope the leading directories are not typed again");
    }

    [Fact]
    public void PopScope_StepsOneLevelUp_ThenOutOfTheScopeEntirely()
    {
        var vm = Create(null, File("src/deep/app.cs"), File("readme.md"));
        vm.Scope = "src/deep";

        vm.PopScope().Should().BeTrue();
        vm.Scope.Should().Be("src");

        vm.PopScope().Should().BeTrue();
        vm.Scope.Should().BeEmpty();
        vm.HasScope.Should().BeFalse();
        vm.Results.Should().HaveCount(2);
    }

    [Fact]
    public void PopScope_WithoutAScope_DoesNothing()
    {
        Create(null, File("a.txt")).PopScope().Should().BeFalse();
    }

    [Fact]
    public void Append_AddsToTheRankedSet_AndCountsWhatWasIndexed()
    {
        var vm = Create(null, File("a.txt"));
        vm.Query = "b";
        vm.Results.Should().BeEmpty();

        vm.Append([File("b.txt")]);

        vm.IndexedCount.Should().Be(2);
        vm.Results.Select(r => r.Name).Should().ContainSingle()
            .Which.Should().Be("b.txt", "the new batch is ranked immediately");
    }

    [Fact]
    public void Append_KeepsTheHighlightedRow_WhileTheIndexIsStillFilling()
    {
        var vm = Create(null, File("a.txt"), File("b.txt"));
        vm.MoveSelection(1);
        vm.SelectedItem!.Name.Should().Be("b.txt");

        vm.Append([File("aa.txt"), File("aaa.txt")]);

        vm.SelectedItem!.Name.Should().Be("b.txt", "arrow keys must survive an incoming batch");
    }

    [Fact]
    public void Query_ResetsTheHighlightToTheBestMatch()
    {
        var vm = Create(null, File("a.txt"), File("b.txt"));
        vm.MoveSelection(1);

        vm.Query = "txt";

        vm.SelectedIndex.Should().Be(0, "typing re-ranks, so the top row is the answer again");
    }

    [Fact]
    public void Results_AreCappedButRankingSeesEverything()
    {
        var many = Enumerable.Range(0, FileFinderViewModel.MaxResults + 50)
            .Select(i => File($"dir{i}/app{i}.cs"))
            .Append(File("app.cs"))
            .ToArray();

        var vm = Create(null, many);
        vm.Query = "app";

        vm.Results.Should().HaveCount(FileFinderViewModel.MaxResults);
        vm.Results[0].RelativePath.Should().Be("app.cs", "the best match is not lost to the cap");
    }

    [Fact]
    public void Status_ReportsIndexingUntilTheWalkFinishes()
    {
        var vm = new FileFinderViewModel("/repo");
        vm.IsIndexing.Should().BeTrue();
        vm.Status.Should().Contain("indexing");

        vm.Append([File("a.txt")]);
        vm.FinishIndexing();

        vm.IsIndexing.Should().BeFalse();
        vm.Status.Should().Contain("1").And.NotContain("indexing");
    }

    [Fact]
    public void Status_AdmitsTruncation()
    {
        var vm = new FileFinderViewModel("/repo");

        vm.FinishIndexing(truncated: true);

        vm.IsTruncated.Should().BeTrue();
        vm.Status.Should().Contain("limit");
    }

    [Fact]
    public void ScopeLabel_IsEmptyWithoutAScope()
    {
        var vm = Create(null, File("src/app.cs"));
        vm.ScopeLabel.Should().BeEmpty();

        vm.Scope = "src";

        vm.HasScope.Should().BeTrue();
        vm.ScopeLabel.Should().Contain("src");
    }
}

public class FileFinderQueryTermsTests
{
    private static FileIndexEntry File(string relativePath)
    {
        var slash = relativePath.LastIndexOf('/');
        var name = slash < 0 ? relativePath : relativePath[(slash + 1)..];
        return new FileIndexEntry("/repo/" + relativePath, relativePath, name, false);
    }

    private static FileFinderViewModel Create(params FileIndexEntry[] entries)
    {
        var vm = new FileFinderViewModel("/repo");
        vm.Append(entries);
        return vm;
    }

    [Fact]
    public void Query_SeveralTerms_MustAllMatch_InAnyOrder()
    {
        var vm = Create(File("src/app/Main.cs"), File("src/lib/Main.cs"), File("docs/app.md"));

        vm.Query = "main app";
        vm.Results.Select(r => r.RelativePath).Should().Equal("src/app/Main.cs");

        vm.Query = "app main";
        vm.Results.Select(r => r.RelativePath).Should().Equal("src/app/Main.cs");
    }

    [Fact]
    public void Query_TermNotFound_ExcludesTheEntry()
    {
        var vm = Create(File("src/app/Main.cs"));

        vm.Query = "main zzz";

        vm.Results.Should().BeEmpty();
    }

    [Fact]
    public void Query_WindowsBackslashes_MatchTheSlashPaths()
    {
        var vm = Create(File("src/app/Main.cs"), File("src/lib/Main.cs"));

        vm.Query = @"src\app";

        vm.Results.Select(r => r.RelativePath).Should().Equal("src/app/Main.cs");
    }

    [Fact]
    public void Query_ExtraWhitespace_IsIgnored()
    {
        var vm = Create(File("src/app/Main.cs"));

        vm.Query = "   app     main  ";

        vm.Results.Should().ContainSingle();
    }
}
