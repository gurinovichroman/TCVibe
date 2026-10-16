using System.Linq;
using FluentAssertions;
using TCV.App.Commands;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

public class CommandPaletteViewModelTests
{
    private static IReadOnlyList<CommandPaletteItem> Items() => new[]
    {
        new CommandPaletteItem("Copy", "Copy", "Files", "F5"),
        new CommandPaletteItem("Move", "Move", "Files", "F6"),
        new CommandPaletteItem("ClipboardPaste", "Paste from clipboard", "Clipboard", "Ctrl+V"),
        new CommandPaletteItem("Search", "Search…", "Tools", "Ctrl+F"),
    };

    [Fact]
    public void EmptyQuery_ShowsAllItems_FirstSelected()
    {
        var vm = new CommandPaletteViewModel(Items());
        vm.Results.Should().HaveCount(4);
        vm.SelectedIndex.Should().Be(0);
        vm.SelectedItem.Should().NotBeNull();
    }

    [Fact]
    public void Query_FiltersAndRanks_TopMatchIsSelected()
    {
        var vm = new CommandPaletteViewModel(Items()) { Query = "copy" };
        vm.Results.Should().NotBeEmpty();
        vm.SelectedItem!.Id.Should().Be("Copy");
    }

    [Fact]
    public void Query_MatchesAcrossCategoryAndTitle()
    {
        var vm = new CommandPaletteViewModel(Items()) { Query = "clip paste" };
        vm.Results.Select(r => r.Id).Should().Contain("ClipboardPaste");
    }

    [Fact]
    public void NoMatch_YieldsEmptyResults_AndNullSelection()
    {
        var vm = new CommandPaletteViewModel(Items()) { Query = "zzzzz" };
        vm.Results.Should().BeEmpty();
        vm.SelectedIndex.Should().Be(-1);
        vm.SelectedItem.Should().BeNull();
    }

    [Fact]
    public void MoveSelection_ClampsToRange()
    {
        var vm = new CommandPaletteViewModel(Items());
        vm.MoveSelection(-1);
        vm.SelectedIndex.Should().Be(0);
        vm.MoveSelection(100);
        vm.SelectedIndex.Should().Be(vm.Results.Count - 1);
    }

    [Fact]
    public void Refresh_OfAGrowingSource_KeepsTheHighlightedRow()
    {
        // A background index rebuild refreshes the list while the user is choosing; the highlight
        // used to jump back to the first row on every refresh.
        var rows = new List<CommandPaletteItem> { new("a", "a", "", ""), new("b", "b", "", "") };
        var vm = new CommandPaletteViewModel(_ => rows.ToList());
        vm.MoveSelection(1);

        rows.Insert(0, new CommandPaletteItem("new", "new", "", ""));
        vm.Refresh();

        vm.SelectedItem!.Id.Should().Be("b");
    }

    [Fact]
    public void EmptyQuery_RecentConnectionsLead_TheMostRecentFirst()
    {
        // The palette's recent connections (6.82) carry a rank; the commands keep their order after them.
        var items = Items().Concat(new[]
        {
            new CommandPaletteItem("Connection:old", "Connect: old", "sftp://old", "", Rank: 1),
            new CommandPaletteItem("Connection:new", "Connect: new", "sftp://new", "", Rank: 2),
        }).ToList();

        var vm = new CommandPaletteViewModel(items);

        vm.Results.Select(r => r.Id).Take(3).Should().Equal("Connection:new", "Connection:old", "Copy");
    }

    [Fact]
    public void Query_ScoreStillDecides_RankOnlyBreaksTies()
    {
        var items = Items().Concat(new[] { new CommandPaletteItem("Connection:x", "Connect: copycat", "sftp://x", "", Rank: 5) }).ToList();

        var vm = new CommandPaletteViewModel(items) { Query = "copy" };

        vm.SelectedItem!.Id.Should().Be("Copy", "a prefix match beats a ranked substring match");
        vm.Results.Select(r => r.Id).Should().Contain("Connection:x");
    }
}
