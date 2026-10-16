using FluentAssertions;
using TCV.App.Commands;
using TCV.App.Help;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The help window is generated from the command registry, so these pin the generation rules: what
/// is folded, what a row carries, how the article markup reads and how the search narrows.
/// </summary>
public class HelpCatalogTests
{
    private static readonly IReadOnlyList<CommandPaletteItem> Items =
    [
        new("Copy", "Copy", "Files", "F5"),
        new("Workspace1", "Switch to workspace 1", "Workspaces", "Alt+1"),
        new("Workspace2", "Switch to workspace 2", "Workspaces", "Alt+2"),
        new("Workspace9", "Switch to workspace 9", "Workspaces", "Alt+9"),
        new("GitDiff", "Git: show diff", "Git", "Ctrl+G"),
    ];

    private static string? Text(string key) => key switch
    {
        "Help_Cmd_Copy" => "Copies the marked files to the other panel.",
        "Help_Cmd_GitDiff" => "Shows the diff against HEAD.",
        "Help_Title_WorkspaceN" => "Switch to workspace 1–9",
        "Help_Topic_Start" => "Getting started",
        "Help_Topic_Start_Body" => "## Two panels\nThe window has two panels.\n• Tab switches them.",
        _ => null,
    };

    private static IReadOnlyList<HelpCommand> Commands() =>
        HelpCatalog.BuildCommands(Items, Text, id => id == "Copy" ? "menu: Files › Copy" : string.Empty);

    [Theory]
    [InlineData("Workspace2", true)]
    [InlineData("Workspace9", true)]
    [InlineData("Workspace1", false)]
    [InlineData("WorkspaceNotes", false)]
    [InlineData("Copy", false)]
    public void IsFolded_HidesOnlyTheSecondToNinthWorkspace(string id, bool folded)
    {
        HelpCatalog.IsFolded(id).Should().Be(folded);
    }

    [Fact]
    public void BuildCommands_ShowsTheNumberedWorkspacesAsOneRow()
    {
        var commands = Commands();

        commands.Select(c => c.Id).Should().Equal("Copy", "Workspace1", "GitDiff");
        var row = commands.Single(c => c.Id == "Workspace1");
        row.Title.Should().Be("Switch to workspace 1–9");
        row.Gesture.Should().Be("Alt+1 … Alt+9");
    }

    [Fact]
    public void BuildCommands_CarriesDescriptionLocationAndTheNeutralCategory()
    {
        var copy = Commands().Single(c => c.Id == "Copy");

        copy.Description.Should().Be("Copies the marked files to the other panel.");
        copy.Location.Should().Be("menu: Files › Copy");
        copy.CategoryKey.Should().Be("Files", "the catalogue's English category keys the section in every language");
        copy.Footer.Should().Be("Files · menu: Files › Copy");
    }

    [Fact]
    public void Footer_WithoutALocation_IsJustTheCategory()
    {
        Commands().Single(c => c.Id == "GitDiff").Footer.Should().Be("Git");
    }

    [Fact]
    public void Parse_ReadsHeadingsBulletsAndParagraphs_AndSkipsBlankLines()
    {
        var blocks = HelpText.Parse("## Title\r\n\r\nPlain text.\n• A bullet\n- Another");

        blocks.Should().Equal(
            new HelpBlock(HelpBlockKind.Heading, "Title"),
            new HelpBlock(HelpBlockKind.Paragraph, "Plain text."),
            new HelpBlock(HelpBlockKind.Bullet, "A bullet"),
            new HelpBlock(HelpBlockKind.Bullet, "Another"));
    }

    [Theory]
    [InlineData("copy", true)]
    [InlineData("COPY marked", true)]          // every word, any case
    [InlineData("f5", true)]                   // the shortcut is searchable
    [InlineData("menu files", true)]           // so is the menu path
    [InlineData("copy diff", false)]           // a word that is not there fails the row
    [InlineData("opies", false)]               // the middle of a word is not a match
    [InlineData("cop", true)]                  // a stem is
    public void Search_NeedsEveryWordSomewhereInTheRow(string query, bool matches)
    {
        var copy = Commands().Single(c => c.Id == "Copy");

        HelpSearch.Matches(copy, HelpSearch.Words(query)).Should().Be(matches);
    }

    [Fact]
    public void Search_LooksInsideArticles()
    {
        var topic = HelpCatalog.BuildTopics(Text).Single(t => t.Id == "Start");

        HelpSearch.Matches(topic, HelpSearch.Words("tab switches")).Should().BeTrue();
        HelpSearch.Matches(topic, HelpSearch.Words("worktree")).Should().BeFalse();
    }

    [Fact]
    public void BuildTopics_ListsEveryGuideArticleInOrder()
    {
        HelpCatalog.BuildTopics(Text).Select(t => t.Id).Should().Equal(HelpCatalog.TopicIds);
    }
}

public class HelpDialogViewModelTests
{
    private static HelpDialogViewModel Create(List<string>? ran = null)
    {
        var topics = new[]
        {
            new HelpTopic("Start", "Getting started", HelpText.Parse("## Panels\nTwo of them.")),
            new HelpTopic("Git", "Git", HelpText.Parse("Branches and worktrees.")),
        };
        var commands = new[]
        {
            new HelpCommand("Copy", "Copy", "Files", "Files", "F5", "Copies files.", string.Empty),
            new HelpCommand("Delete", "Delete", "Files", "Files", "F8", "Deletes files.", string.Empty),
            new HelpCommand("GitDiff", "Git: show diff", "Git", "Git", "Ctrl+G", "Shows a diff.", string.Empty),
        };
        return new HelpDialogViewModel(topics, commands, id => ran?.Add(id));
    }

    [Fact]
    public void Opens_OnTheFirstArticle()
    {
        var vm = Create();

        vm.Article!.Id.Should().Be("Start");
        vm.Heading.Should().Be("Getting started");
        vm.VisibleCommands.Should().BeEmpty();
    }

    [Fact]
    public void Sections_AreTheArticlesThenTheCategoriesWithTheirCounts()
    {
        var vm = Create();

        vm.Sections.Select(s => s.Title).Should().Equal("Getting started", "Git", "Files", "Git");
        vm.Sections.Where(s => !s.IsTopic).Select(s => s.CountText).Should().Equal("2", "1");
    }

    [Fact]
    public void PickingACategory_ShowsItsCommands()
    {
        var vm = Create();

        vm.SelectedSection = vm.Sections.First(s => s.Key == "cat:Files");

        vm.Article.Should().BeNull();
        vm.VisibleCommands.Select(c => c.Id).Should().Equal("Copy", "Delete");
    }

    [Fact]
    public void Searching_ListsMatchesFromEverywhere_AndClearingGoesBack()
    {
        var vm = Create();

        vm.Query = "git";

        vm.IsSearching.Should().BeTrue();
        vm.MatchingTopics.Select(t => t.Id).Should().Equal("Git");
        vm.VisibleCommands.Select(c => c.Id).Should().Equal("GitDiff");

        vm.Query = string.Empty;

        vm.IsSearching.Should().BeFalse();
        vm.Article!.Id.Should().Be("Start");
    }

    [Fact]
    public void Searching_ForSomethingAbsent_SaysSo()
    {
        var vm = Create();

        vm.Query = "teleport";

        vm.NothingFound.Should().BeTrue();
    }

    [Fact]
    public void OpeningAFoundArticle_LeavesTheSearch()
    {
        var vm = Create();
        vm.Query = "worktrees";

        vm.OpenTopicCommand.Execute(vm.MatchingTopics.Single());

        vm.Query.Should().BeEmpty();
        vm.Article!.Id.Should().Be("Git");
    }

    [Fact]
    public void Run_HandsTheCommandIdToTheWindow()
    {
        var ran = new List<string>();
        var vm = Create(ran);
        vm.SelectedSection = vm.Sections.First(s => s.Key == "cat:Files");

        vm.RunCommand.Execute(vm.VisibleCommands[1]);

        ran.Should().Equal("Delete");
    }
}
