using System.IO;
using FluentAssertions;
using TCV.App.Settings;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// The rail's order (6.79): pinned workspaces stand where they were dragged, under their group's
/// heading, the rest follow by use; a drag moves a workspace only inside its group; the store writes
/// the order and nothing else.
/// </summary>
public sealed class WorkspaceOrderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tcv-wo-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var file in new[] { _path, _path + ".bak" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    private static Workspace Ws(string name, bool pinned = true, string group = "", int order = 0, int day = 1) =>
        new() { Name = name, Pinned = pinned, Group = group, Order = order, LastUsedUtc = new DateTime(2026, 1, day) };

    private static IEnumerable<string> Names(IEnumerable<Workspace> workspaces) => workspaces.Select(w => w.Name);

    // ── Arrange ──────────────────────────────────────────────────────────────

    [Fact]
    public void Arrange_PinnedStandWhereTheyWerePut_NotByUse()
    {
        var arranged = WorkspaceOrder.Arrange([Ws("b", order: 2, day: 9), Ws("loose", pinned: false, day: 5), Ws("a", order: 1)]);

        Names(arranged).Should().Equal("a", "b", "loose");
    }

    [Fact]
    public void Arrange_UngroupedLead_ThenGroupsAlphabetically_EachInItsOwnOrder()
    {
        var arranged = WorkspaceOrder.Arrange(
        [
            Ws("w2", group: "Work", order: 3),
            Ws("o1", group: "OSS", order: 2),
            Ws("u1", order: 9),
            Ws("w1", group: " work ", order: 1),
        ]);

        Names(arranged).Should().Equal("u1", "o1", "w1", "w2");
    }

    [Fact]
    public void Arrange_NotYetPlaced_GoAfterThePlacedOnes_MostRecentFirst()
    {
        // A file from before 6.79 has no places at all: most recent first, as the rail showed it then.
        var arranged = WorkspaceOrder.Arrange([Ws("older", day: 1), Ws("placed", order: 1), Ws("recent", day: 9)]);

        Names(arranged).Should().Equal("placed", "recent", "older");
    }

    // ── ShortcutNumber ───────────────────────────────────────────────────────

    [Fact]
    public void ShortcutNumber_CountsDownTheArrangedList_FromOne()
    {
        var arranged = WorkspaceOrder.Arrange([Ws("b", order: 2), Ws("loose", pinned: false), Ws("a", order: 1)]);

        WorkspaceOrder.ShortcutNumber(arranged, "a").Should().Be(1);
        WorkspaceOrder.ShortcutNumber(arranged, "B").Should().Be(2);
        WorkspaceOrder.ShortcutNumber(arranged, "loose").Should().Be(3);
        WorkspaceOrder.ShortcutNumber(arranged, "missing").Should().BeNull();
    }

    [Fact]
    public void ShortcutNumber_PastTheNinth_IsNull()
    {
        var arranged = Enumerable.Range(1, 10).Select(i => Ws($"w{i}", order: i)).ToList();

        WorkspaceOrder.ShortcutNumber(arranged, "w9").Should().Be(9);
        WorkspaceOrder.ShortcutNumber(arranged, "w10").Should().BeNull();
    }

    [Fact]
    public void Arrange_NotYetPlaced_KeepTheirPlacesOnScreen()
    {
        var shown = new[] { Ws("older"), Ws("recent") };

        var arranged = WorkspaceOrder.Arrange([Ws("recent", day: 9), Ws("older", day: 1)], shown);

        Names(arranged).Should().Equal("older", "recent");
    }

    // ── Numbering ────────────────────────────────────────────────────────────

    [Fact]
    public void Number_GivesThePinnedRisingPlaces_AndOnlyThem()
    {
        var arranged = WorkspaceOrder.Arrange([Ws("a", day: 1), Ws("b", day: 2), Ws("c", pinned: false)]);
        WorkspaceOrder.NeedsNumbering(arranged).Should().BeTrue();

        WorkspaceOrder.Number(arranged).Should().Equal("b", "a");

        arranged.Where(w => w.Pinned).Select(w => w.Order).Should().Equal(1, 2);
        arranged.Single(w => w.Name == "c").Order.Should().Be(0);
        WorkspaceOrder.NeedsNumbering(arranged).Should().BeFalse();
    }

    [Fact]
    public void NeedsNumbering_IgnoresAGap_LeftByAnUnpinnedOne()
    {
        var arranged = WorkspaceOrder.Arrange([Ws("a", order: 1), Ws("c", order: 3), Ws("b", pinned: false, order: 2)]);

        WorkspaceOrder.NeedsNumbering(arranged).Should().BeFalse();
    }

    [Fact]
    public void NeedsNumbering_WhenTwoShareAPlace()
    {
        // Two windows each pinned a workspace at the end of the same rail.
        var arranged = WorkspaceOrder.Arrange([Ws("a", order: 1), Ws("b", order: 2), Ws("c", order: 2)]);

        WorkspaceOrder.NeedsNumbering(arranged).Should().BeTrue();
    }

    // ── Move ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("c", 0, "c,a,b")]
    [InlineData("c", 1, "a,c,b")]
    [InlineData("a", 2, "b,a,c")]
    [InlineData("a", 3, "b,c,a")]
    public void Move_PutsTheWorkspaceInTheSlotOfItsBlock(string name, int slot, string expected)
    {
        var arranged = WorkspaceOrder.Arrange([Ws("a", order: 1), Ws("b", order: 2), Ws("c", order: 3)]);

        WorkspaceOrder.Move(arranged, name, slot).Should().Equal(expected.Split(','));
    }

    [Theory]
    [InlineData("b", 1)]      // just above itself
    [InlineData("b", 2)]      // just below itself
    [InlineData("b", -1)]
    [InlineData("b", 4)]
    [InlineData("loose", 0)]  // not on the rail
    [InlineData("gone", 0)]
    public void Move_IsNull_WhenNothingMoves(string name, int slot)
    {
        var arranged = WorkspaceOrder.Arrange(
            [Ws("a", order: 1), Ws("b", order: 2), Ws("c", order: 3), Ws("loose", pinned: false)]);

        WorkspaceOrder.Move(arranged, name, slot).Should().BeNull();
    }

    [Fact]
    public void Move_StaysInsideTheGroup_AndLeavesTheOtherBlocksAlone()
    {
        var arranged = WorkspaceOrder.Arrange(
        [
            Ws("u1", order: 1), Ws("u2", order: 2),
            Ws("o1", group: "OSS", order: 3), Ws("o2", group: "OSS", order: 4),
            Ws("w1", group: "Work", order: 5),
            Ws("loose", pinned: false),
        ]);

        WorkspaceOrder.Move(arranged, "o2", 0).Should().Equal("u1", "u2", "o2", "o1", "w1");
        WorkspaceOrder.Move(arranged, "o1", 2).Should().Equal("u1", "u2", "o2", "o1", "w1");
        WorkspaceOrder.Move(arranged, "u1", 3).Should().BeNull("slot 3 is past the end of a block of two");
    }

    // ── SlotAt ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10, 0)]
    [InlineData(30, 1)]
    [InlineData(60, 1)]
    [InlineData(80, 2)]
    [InlineData(135, 3)]
    [InlineData(-10, 0)]    // in the gap above the first button
    [InlineData(150, 3)]    // in the gap below the last
    public void SlotAt_PicksTheGap_TheMiddleOfAButtonDecides(double y, int expected)
    {
        (double, double)[] buttons = [(0, 38), (51, 89), (102, 140)];

        WorkspaceOrder.SlotAt(y, buttons, reach: 13).Should().Be(expected);
    }

    [Theory]
    [InlineData(-20)]
    [InlineData(160)]
    public void SlotAt_IsNull_OutsideTheGroup(double y)
    {
        (double, double)[] buttons = [(0, 38), (51, 89)];

        WorkspaceOrder.SlotAt(y, buttons, reach: 13).Should().BeNull();
        WorkspaceOrder.SlotAt(0, [], reach: 13).Should().BeNull();
    }

    // ── The store ────────────────────────────────────────────────────────────

    [Fact]
    public void SetOrder_WritesThePlacesAlone_AndKeepsWhatAnotherWindowSaved()
    {
        var store = new WorkspaceStore(_path);
        store.Save(new Workspace { Name = "a", Pinned = true, Color = "#111111" });
        store.Save(new Workspace { Name = "b", Pinned = true });
        store.Save(new Workspace { Name = "c" });

        // A guest window recolours "a" meanwhile (TRAP-22).
        var guest = new WorkspaceStore(_path);
        var a = guest.Get("a")!;
        a.Color = "#222222";
        guest.Save(a);

        store.SetOrder(["b", "A"]).Should().BeTrue();

        var loaded = store.LoadAll();
        loaded.Single(w => w.Name == "b").Order.Should().Be(1);
        loaded.Single(w => w.Name == "a").Order.Should().Be(2);
        loaded.Single(w => w.Name == "a").Color.Should().Be("#222222");
        loaded.Single(w => w.Name == "c").Order.Should().Be(0);
        Names(store.LoadOrdered()).Should().Equal("b", "a", "c");
    }

    [Fact]
    public void AFileFromBefore_LoadsUnplaced_MostRecentFirst()
    {
        File.WriteAllText(_path,
            """[{"Name":"a","Pinned":true,"LastUsedUtc":"2026-01-01T00:00:00Z"},{"Name":"b","Pinned":true,"LastUsedUtc":"2026-05-01T00:00:00Z"}]""");
        var store = new WorkspaceStore(_path);

        store.LoadAll().Should().OnlyContain(w => w.Order == 0);
        Names(store.LoadOrdered()).Should().Equal("b", "a");
    }

    [Fact]
    public void ResavingTheLayout_KeepsThePlace()
    {
        var snapshot = new Workspace { Name = "a" };

        snapshot.CopyIdentityFrom(new Workspace { Name = "a", Pinned = true, Order = 4 });

        snapshot.Order.Should().Be(4);
    }

    [Fact]
    public void GroupLabel_IsTheHeadingInCapitals()
    {
        new WorkspaceGroup("Work", []).Label.Should().Be("WORK");
        new WorkspaceGroup(string.Empty, []).Label.Should().BeEmpty();
    }
}

/// <summary>A folder dropped on the rail (6.79): what it carries, what the workspace is called, and
/// when an existing workspace already stands there.</summary>
public class WorkspaceFolderDropTests
{
    [Fact]
    public void SinglePath_TakesOneFullPath_AndNothingElse()
    {
        WorkspaceFolderDrop.SinglePath([@"C:\Projects\api"]).Should().Be(@"C:\Projects\api");
        WorkspaceFolderDrop.SinglePath([@"\\server\share\api"]).Should().Be(@"\\server\share\api");
        WorkspaceFolderDrop.SinglePath(null).Should().BeNull();
        WorkspaceFolderDrop.SinglePath([]).Should().BeNull();
        WorkspaceFolderDrop.SinglePath([@"C:\a", @"C:\b"]).Should().BeNull();
        WorkspaceFolderDrop.SinglePath([@"relative\api"]).Should().BeNull();
        WorkspaceFolderDrop.SinglePath([" "]).Should().BeNull();
    }

    [Theory]
    [InlineData(@"C:\Projects\api", "api")]
    [InlineData(@"C:\Projects\api\", "api")]
    [InlineData(@"D:\", "D:")]
    [InlineData(@"\\server\share", "share")]
    public void NameFor_IsTheFolderName(string folder, string expected)
    {
        WorkspaceFolderDrop.NameFor(folder, []).Should().Be(expected);
    }

    [Fact]
    public void NameFor_IsNumbered_WhenTheNameIsTaken()
    {
        WorkspaceFolderDrop.NameFor(@"C:\Projects\api", ["API", "api (2)"]).Should().Be("api (3)");
    }

    private static Workspace Ws(string name, string[] left, int leftActive, string[] right) => new()
    {
        Name = name,
        Left = new WorkspacePanelState { Tabs = left.Select(p => new TabState { Path = p }).ToList(), ActiveTabIndex = leftActive },
        Right = new WorkspacePanelState { Tabs = right.Select(p => new TabState { Path = p }).ToList() },
    };

    [Fact]
    public void RootedAt_IsTheWorkspaceWhosePanelStandsInTheFolder()
    {
        var x = Ws("x", [@"C:\a", @"C:\b"], 0, [@"C:\c"]);
        var y = Ws("y", [@"C:\d"], 0, [@"C:\b\"]);
        var workspaces = new[] { x, y };

        WorkspaceFolderDrop.RootedAt(workspaces, @"c:\B").Should().BeSameAs(y, "x has the folder only in a tab in the background");
        WorkspaceFolderDrop.RootedAt(workspaces, @"C:\c\").Should().BeSameAs(x);
        WorkspaceFolderDrop.RootedAt(workspaces, @"C:\z").Should().BeNull();
    }

    [Fact]
    public void RootedAt_TakesTheFirstInTheRailOrder()
    {
        var first = Ws("first", [@"C:\a"], 0, []);
        var second = Ws("second", [@"C:\a"], 0, []);

        WorkspaceFolderDrop.RootedAt([first, second], @"C:\a").Should().BeSameAs(first);
    }
}
