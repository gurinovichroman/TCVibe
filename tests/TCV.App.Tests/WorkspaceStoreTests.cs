using System.IO;
using System.Linq;
using FluentAssertions;
using TCV.App.Settings;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"tcv-ws-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private WorkspaceStore Store() => new(_path);

    private static Workspace Sample(string name) => new()
    {
        Name = name,
        Color = "#4F9CF9",
        Icon = "🚀",
        SplitVertical = true,
        ActiveLeft = false,
        TerminalCommand = "npm run dev",
        TerminalDirectory = @"C:\src\app",
        Left = new WorkspacePanelState
        {
            ActiveTabIndex = 1,
            ViewMode = ViewMode.Brief,
            SortBy = SortColumn.Size,
            SortDescending = true,
            Tabs =
            {
                new TabState { Path = @"C:\a", IsLocked = true },
                new TabState { Path = @"C:\b", CustomTitle = "beta" },
            },
        },
    };

    [Fact]
    public void MissingFile_LoadsEmpty()
    {
        Store().LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void Save_Then_Get_RoundTrips_FullState()
    {
        Store().Save(Sample("work"));

        var loaded = Store().Get("work");
        loaded.Should().NotBeNull();
        loaded!.Icon.Should().Be("🚀");
        loaded.SplitVertical.Should().BeTrue();
        loaded.ActiveLeft.Should().BeFalse();
        loaded.Left.ViewMode.Should().Be(ViewMode.Brief);
        loaded.Left.SortBy.Should().Be(SortColumn.Size);
        loaded.Left.ActiveTabIndex.Should().Be(1);
        loaded.Left.Tabs.Should().HaveCount(2);
        loaded.Left.Tabs[0].Path.Should().Be(@"C:\a");
        loaded.Left.Tabs[0].IsLocked.Should().BeTrue();
        loaded.Left.Tabs[1].CustomTitle.Should().Be("beta");
        loaded.TerminalCommand.Should().Be("npm run dev");
        loaded.TerminalDirectory.Should().Be(@"C:\src\app");
    }

    [Fact]
    public void OldFileWithoutTerminalFields_LoadsWithEmptyDefaults()
    {
        // A settings file written before 6.8 must keep opening — the fields simply come back blank.
        File.WriteAllText(_path, """[{"Name":"legacy","Icon":"🚀"}]""");

        var loaded = Store().Get("legacy");
        loaded.Should().NotBeNull();
        loaded!.TerminalCommand.Should().BeEmpty();
        loaded.TerminalDirectory.Should().BeEmpty();
    }

    [Fact]
    public void Save_SameName_Overwrites()
    {
        var store = Store();
        store.Save(Sample("work"));
        store.Save(new Workspace { Name = "WORK", Icon = "🐛" });   // case-insensitive key

        store.LoadAll().Should().HaveCount(1);
        store.Get("work")!.Icon.Should().Be("🐛");
    }

    [Fact]
    public void Delete_RemovesByName_CaseInsensitively()
    {
        var store = Store();
        store.Save(Sample("work"));
        store.Delete("WORK");
        store.LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void LoadOrdered_PinnedFirst_ThenMostRecentlyUsed()
    {
        var store = Store();
        store.Save(new Workspace { Name = "old",    LastUsedUtc = new DateTime(2026, 1, 1) });
        store.Save(new Workspace { Name = "recent", LastUsedUtc = new DateTime(2026, 7, 1) });
        store.Save(new Workspace { Name = "pinned", Pinned = true, LastUsedUtc = new DateTime(2020, 1, 1) });

        store.LoadOrdered().Select(w => w.Name).Should().Equal("pinned", "recent", "old");
    }

    [Fact]
    public void Touch_BumpsOneTimestamp_AndLeavesEveryOtherEntryAlone()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work", LastUsedUtc = new DateTime(2026, 1, 1) });

        // Written by another window after this one read the file: a whole-file rewrite would drop it.
        store.Save(new Workspace { Name = "from-the-other-window", Icon = "🐛" });

        store.Touch("WORK", new DateTime(2026, 7, 26)).Should().BeTrue();

        store.Get("work")!.LastUsedUtc.Should().Be(new DateTime(2026, 7, 26));
        store.Get("from-the-other-window")!.Icon.Should().Be("🐛");
    }

    [Fact]
    public void Touch_UnknownName_ChangesNothing()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work", LastUsedUtc = new DateTime(2026, 1, 1) });

        store.Touch("gone", DateTime.UtcNow).Should().BeFalse();
        store.Get("work")!.LastUsedUtc.Should().Be(new DateTime(2026, 1, 1));
    }

    [Fact]
    public void Save_WhenTheFileCannotBeRead_ReportsFailure_AndKeepsTheContent()
    {
        var store = Store();
        store.Save(new Workspace { Name = "work", Icon = "🚀" });

        // Another process holding the file must never be read as "the store is empty" — rewriting
        // from that would wipe every workspace.
        using (File.Open(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            store.Save(new Workspace { Name = "later" }).Should().BeFalse();
        }

        store.LoadAll().Select(w => w.Name).Should().Equal("work");
    }

    [Fact]
    public void CorruptFile_LoadsEmpty()
    {
        File.WriteAllText(_path, "{ this is not valid json ]");
        Store().LoadAll().Should().BeEmpty();
    }

    [Fact]
    public void EmptyName_Save_Throws()
    {
        var act = () => Store().Save(new Workspace { Name = "  " });
        act.Should().Throw<ArgumentException>();
    }
}
