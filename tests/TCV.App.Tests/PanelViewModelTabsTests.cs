using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.App.Workspaces;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// Tabs, Back/Forward and workspace snapshots of one panel. These live on <see cref="PanelViewModel"/>
/// and were the part of the workspace pillar with no coverage at all — the mount-related bugs here
/// (a tab switch that left an archive mounted, history that replayed archive paths on the disk) are
/// the ones a unit test catches cheaply.
/// </summary>
public class PanelViewModelTabsTests
{
    private const string A = "/C:/a";
    private const string B = "/C:/b";
    private const string C = "/C:/c";

    private static PanelViewModel CreatePanel(out AppSettings settings, string name = "Left")
    {
        settings = new AppSettings();
        var local = new InMemoryFileSystem("file");
        var drives = new DriveManager();
        return new PanelViewModel(
            name,
            new FileListViewModel(local, NullLogger<FileListViewModel>.Instance),
            new DriveBarViewModel(drives),
            new StatusBarViewModel(drives),
            settings,
            new FileWatcher());
    }

    /// <summary>A panel with tabs A (active), B and C, each on its own folder.</summary>
    private static PanelViewModel PanelWithThreeTabs(out AppSettings settings)
    {
        var panel = CreatePanel(out settings);
        panel.LoadInitialPath(A);
        panel.NewTabCommand.Execute(null);
        panel.CurrentPath = B;
        panel.NewTabCommand.Execute(null);
        panel.CurrentPath = C;
        panel.SwitchTabCommand.Execute(panel.Tabs[0]);
        return panel;
    }

    private static InMemoryFileSystem MountArchive(PanelViewModel panel)
    {
        var archive = new InMemoryFileSystem("zip");
        panel.FileList.Mount(archive, kind: MountKind.Archive);
        return archive;
    }

    private static readonly ConnectionInfo Prod = new()
    {
        Name = "prod", Protocol = NetworkProtocol.Sftp, Host = "prod.example", Port = 22, Username = "deploy",
    };

    private static InMemoryFileSystem ConnectTo(PanelViewModel panel, ConnectionInfo server, string path = "/")
    {
        var remote = new InMemoryFileSystem("sftp");
        remote.AddDirectory("/var/log");
        panel.FileList.Mount(remote, path, server, MountKind.Remote);
        return remote;
    }

    // ── Tabs and mounts (ADR-0032: a tab keeps its mount) ─────────────────────

    [Fact]
    public void SwitchTab_WhileTheActiveTabIsInsideAnArchive_ParksTheMountAndShowsTheOtherFolder()
    {
        var panel = PanelWithThreeTabs(out _);
        var archive = MountArchive(panel);

        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.FileList.IsMounted.Should().BeFalse("the archive stays with the tab that was left");
        panel.CurrentPath.Should().Be(B);
        panel.ActiveTab.Should().BeSameAs(panel.Tabs[1]);
        archive.IsDisposed.Should().BeFalse("a parked mount is not closed");
        panel.Tabs[0].IsInArchive.Should().BeTrue("the parked tab still says where it is");
    }

    [Fact]
    public void SwitchTab_BackToATabOnAServer_RestoresTheConnectionWhereItWas()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod, "/var/log");

        panel.SwitchTabCommand.Execute(panel.Tabs[1]);
        panel.SwitchTabCommand.Execute(panel.Tabs[0]);

        panel.FileList.IsRemote.Should().BeTrue();
        panel.FileList.ActiveVfs.Should().BeSameAs(remote, "the same connection comes back, not a new one");
        panel.FileList.RemoteConnection.Should().BeSameAs(Prod);
        panel.CurrentPath.Should().Be("/var/log");
        remote.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void SwitchTab_BetweenTwoTabsOnServers_ShowsEachItsOwnServer()
    {
        var panel = PanelWithThreeTabs(out _);
        var first = ConnectTo(panel, Prod);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);
        var second = ConnectTo(panel, new ConnectionInfo { Name = "staging", Protocol = NetworkProtocol.Sftp, Host = "s" });

        panel.SwitchTabCommand.Execute(panel.Tabs[0]);

        panel.FileList.ActiveVfs.Should().BeSameAs(first);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);
        panel.FileList.ActiveVfs.Should().BeSameAs(second);
        panel.FileList.LeaveConnection();
        panel.FileList.IsMounted.Should().BeFalse("leaving the server returns to the disk under it");
        panel.CurrentPath.Should().Be(B);
    }

    [Fact]
    public void SwitchTab_BackFromAServerToAnArchiveAtTheSameRoot_ReloadsTheRestoredListing()
    {
        var panel = PanelWithThreeTabs(out _);
        var archive = MountArchive(panel);
        archive.AddFile("/inside.txt", [1]);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);
        ConnectTo(panel, Prod);

        panel.SwitchTabCommand.Execute(panel.Tabs[0]);

        panel.FileList.ActiveVfs.Should().BeSameAs(archive, "the archive's \"/\" is not the server's \"/\"");
        panel.FileList.IsInsideArchive.Should().BeTrue();
    }

    [Fact]
    public void SwitchTab_ClickingTheActiveTabInsideAnArchive_KeepsTheMount()
    {
        var panel = PanelWithThreeTabs(out _);
        MountArchive(panel);

        panel.SwitchTabCommand.Execute(panel.Tabs[0]);

        panel.FileList.IsMounted.Should().BeTrue();
    }

    [Fact]
    public void DuplicateTab_OfTheActiveTabInsideAnArchive_LeavesTheArchiveWithTheOriginal()
    {
        var panel = PanelWithThreeTabs(out _);
        var archive = MountArchive(panel);

        panel.DuplicateTabCommand.Execute(panel.Tabs[0]);

        panel.Tabs.Should().HaveCount(4);
        panel.ActiveTab.Should().BeSameAs(panel.Tabs[1]);
        panel.Tabs[1].Path.Should().Be(A, "the copy starts on the disk folder under the archive");
        panel.FileList.IsMounted.Should().BeFalse();
        panel.Tabs[0].IsInArchive.Should().BeTrue("the original keeps its archive");
        archive.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void DuplicateTab_OfATabOnAServer_OpensASecondConnectionToTheSamePlace()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod, "/var/log");
        (ConnectionInfo Server, string Path)? opened = null;
        panel.OpenConnectionInActiveTab = (server, path) =>
        {
            opened = (server, path);
            return Task.CompletedTask;
        };

        panel.DuplicateTabCommand.Execute(panel.Tabs[0]);

        opened.Should().Be((Prod, "/var/log"));
        panel.ActiveTab.Should().BeSameAs(panel.Tabs[1], "the copy is the tab the second connection opens in");
        remote.IsDisposed.Should().BeFalse("the original tab keeps its own connection");
    }

    [Fact]
    public void DuplicateTab_OfAnotherTabWhileMounted_ShowsItsFolderOnTheDisk()
    {
        var panel = PanelWithThreeTabs(out _);
        var archive = MountArchive(panel);

        panel.DuplicateTabCommand.Execute(panel.Tabs[1]);

        panel.FileList.IsMounted.Should().BeFalse();
        panel.CurrentPath.Should().Be(B);
        panel.Tabs.Should().HaveCount(4);
        archive.IsDisposed.Should().BeFalse("the archive waits in its own tab");
    }

    [Fact]
    public void NewTab_OnAServer_OpensOnTheDiskAndLeavesTheConnectionInItsTab()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod, "/var/log");

        panel.NewTabCommand.Execute(null);

        panel.Tabs.Should().HaveCount(4);
        panel.FileList.IsMounted.Should().BeFalse();
        panel.CurrentPath.Should().Be(A, "the new tab opens on the disk folder the connection was opened from");
        panel.Tabs[0].IsOnServer.Should().BeTrue();
        remote.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void CloseTab_OnTheActiveMountedTab_ShowsTheNeighbourAndClosesTheMount()
    {
        var panel = PanelWithThreeTabs(out _);
        var archive = MountArchive(panel);

        panel.CloseTabCommand.Execute(panel.Tabs[0]);

        panel.FileList.IsMounted.Should().BeFalse();
        panel.CurrentPath.Should().Be(B);
        archive.IsDisposed.Should().BeTrue("a closed tab takes its mount with it");
    }

    [Fact]
    public void CloseTab_OnAParkedTabOnAServer_ClosesItsConnection()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.CloseTabCommand.Execute(panel.Tabs[0]);

        remote.IsDisposed.Should().BeTrue();
        panel.CurrentPath.Should().Be(B, "closing a tab in the background does not move the panel");
    }

    [Fact]
    public void CloseTab_OnAParkedTabWhileAnOperationReadsFromIt_ClosesTheConnectionWhenItLetsGo()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod);
        var lease = panel.FileList.LeaseActiveVfs();
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.CloseTabCommand.Execute(panel.Tabs[0]);
        remote.IsDisposed.Should().BeFalse("a copy is still streaming from it (INV-09)");
        lease.Dispose();

        remote.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void OpenConnections_ListsTheShownAndTheParkedOnes()
    {
        var panel = PanelWithThreeTabs(out _);
        ConnectTo(panel, Prod);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);
        var staging = new ConnectionInfo { Name = "staging", Protocol = NetworkProtocol.Ftp, Host = "s" };
        ConnectTo(panel, staging);

        panel.OpenConnections().Select(c => (c.Tab, c.Connection)).Should().Equal(
            (panel.Tabs[0], Prod), (panel.Tabs[1], staging));
    }

    // ── Locked tabs ───────────────────────────────────────────────────────────

    [Fact]
    public void CloseTab_OnALockedTab_DoesNothing()
    {
        var panel = PanelWithThreeTabs(out _);
        panel.Tabs[1].IsLocked = true;

        panel.CloseTabCommand.Execute(panel.Tabs[1]);

        panel.Tabs.Should().HaveCount(3);
    }

    [Fact]
    public void CloseTab_TheOnlyTab_IsKept()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);

        panel.CloseTabCommand.Execute(null);

        panel.Tabs.Should().HaveCount(1);
    }

    [Fact]
    public void CloseOtherTabs_KeepsLockedTabs()
    {
        var panel = PanelWithThreeTabs(out _);
        panel.Tabs[2].IsLocked = true;

        panel.CloseOtherTabsCommand.Execute(panel.Tabs[0]);

        panel.Tabs.Select(t => t.Path).Should().Equal(A, C);
        panel.ActiveTab.Should().BeSameAs(panel.Tabs[0]);
    }

    [Fact]
    public void CloseOtherTabs_ClosesTheConnectionsOfTheTabsThatGo()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.CloseOtherTabsCommand.Execute(panel.Tabs[1]);

        remote.IsDisposed.Should().BeTrue();
        panel.Tabs.Should().ContainSingle();
    }

    [Fact]
    public void CloseOtherTabs_OnTheActiveTabInsideAnArchive_KeepsTheMount()
    {
        var panel = PanelWithThreeTabs(out _);
        MountArchive(panel);

        panel.CloseOtherTabsCommand.Execute(panel.Tabs[0]);

        panel.Tabs.Should().HaveCount(1);
        panel.FileList.IsMounted.Should().BeTrue("closing the other tabs is not a navigation");
    }

    // ── History ───────────────────────────────────────────────────────────────

    [Fact]
    public void Back_AfterNavigating_ReturnsToThePreviousFolder()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        panel.CurrentPath = B;

        panel.CanGoBack.Should().BeTrue();
        panel.NavigateBackCommand.Execute(null);

        panel.CurrentPath.Should().Be(A);
        panel.CanGoForward.Should().BeTrue();
    }

    [Fact]
    public void History_SwitchingToAnotherTab_DoesNotReplayTheFirstTabsFolders()
    {
        var panel = PanelWithThreeTabs(out _);   // tab A active
        panel.CurrentPath = "/C:/a/sub";
        panel.CanGoBack.Should().BeTrue();

        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.CanGoBack.Should().BeFalse("Back in tab B must not walk into tab A's folders");
        panel.CurrentPath.Should().Be(B);
    }

    [Fact]
    public void History_ClickingTheActiveTab_KeepsItsHistory()
    {
        var panel = PanelWithThreeTabs(out _);
        panel.CurrentPath = "/C:/a/sub";

        panel.SwitchTabCommand.Execute(panel.Tabs[0]);

        panel.CanGoBack.Should().BeTrue();
    }

    [Fact]
    public void History_EnteringAnArchive_StartsAFreshHistory()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        panel.CurrentPath = B;
        panel.CanGoBack.Should().BeTrue();

        MountArchive(panel);

        // Back from inside the archive must not try to open a disk folder against the archive.
        panel.CanGoBack.Should().BeFalse();
        panel.CanGoForward.Should().BeFalse();
    }

    [Fact]
    public void History_LeavingAnArchive_DoesNotOfferTheArchivePathsAsBack()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        MountArchive(panel);
        panel.CurrentPath = "/docs";

        panel.FileList.Unmount();

        panel.FileList.IsMounted.Should().BeFalse();
        panel.CanGoBack.Should().BeFalse("\"/docs\" would be replayed as a path on the disk");
    }

    [Fact]
    public void History_InsideAnArchive_StillWorksBetweenArchiveFolders()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        MountArchive(panel);
        panel.CurrentPath = "/docs";
        panel.CurrentPath = "/docs/api";

        panel.NavigateBackCommand.Execute(null);

        panel.CurrentPath.Should().Be("/docs");
        panel.FileList.IsMounted.Should().BeTrue();
    }

    // ── Session and workspace snapshots ───────────────────────────────────────

    [Fact]
    public void LoadInitialPath_RestoresTheTabThatWasActive()
    {
        var panel = CreatePanel(out var settings);
        settings.LeftPanel.Tabs = new List<TabState>
        {
            new() { Path = A }, new() { Path = B }, new() { Path = C },
        };
        settings.LeftPanel.ActiveTabIndex = 2;

        panel.LoadInitialPath(A);

        panel.ActiveTab.Should().BeSameAs(panel.Tabs[2]);
        panel.CurrentPath.Should().Be(C);
    }

    [Fact]
    public void LoadInitialPath_WithAnIndexOutOfRange_FallsBackToALegalTab()
    {
        var panel = CreatePanel(out var settings);
        settings.LeftPanel.Tabs = new List<TabState> { new() { Path = A }, new() { Path = B } };
        settings.LeftPanel.ActiveTabIndex = 7;

        panel.LoadInitialPath(A);

        panel.ActiveTab.Should().BeSameAs(panel.Tabs[1]);
    }

    [Fact]
    public void SaveState_RecordsWhichTabWasActive()
    {
        var panel = PanelWithThreeTabs(out var settings);
        panel.SwitchTabCommand.Execute(panel.Tabs[2]);

        panel.SaveState();

        settings.LeftPanel.ActiveTabIndex.Should().Be(2);
        settings.LeftPanel.Tabs.Select(t => t.Path).Should().Equal(A, B, C);
    }

    [Fact]
    public void RestoreSnapshot_RebuildsTheTabsAndActivatesTheSavedOne()
    {
        var source = PanelWithThreeTabs(out _);
        source.Tabs[1].CustomTitle = "beta";
        source.SwitchTabCommand.Execute(source.Tabs[1]);
        var snapshot = source.CaptureSnapshot();

        var target = CreatePanel(out _);
        target.LoadInitialPath("/C:/other");
        target.RestoreSnapshot(snapshot);

        target.Tabs.Select(t => t.Path).Should().Equal(A, B, C);
        target.ActiveTab!.CustomTitle.Should().Be("beta");
        target.CurrentPath.Should().Be(B);
    }

    [Fact]
    public void RestoreSnapshot_StartsWithACleanHistory()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        panel.CurrentPath = B;
        panel.CanGoBack.Should().BeTrue();

        panel.RestoreSnapshot(new WorkspacePanelState { Tabs = { new TabState { Path = C } } });

        panel.CanGoBack.Should().BeFalse("Back must not walk into the workspace that was left");
        panel.CurrentPath.Should().Be(C);
    }

    [Fact]
    public void RestoreSnapshot_WhileMounted_LeavesTheMount()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        MountArchive(panel);

        panel.RestoreSnapshot(new WorkspacePanelState { Tabs = { new TabState { Path = C } } });

        panel.FileList.IsMounted.Should().BeFalse();
        panel.CurrentPath.Should().Be(C);
    }

    [Fact]
    public void RestoreSnapshot_ClosesTheParkedMountsOfEveryTab()
    {
        var panel = PanelWithThreeTabs(out _);
        var remote = ConnectTo(panel, Prod);
        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.RestoreSnapshot(new WorkspacePanelState { Tabs = { new TabState { Path = C } } });

        remote.IsDisposed.Should().BeTrue("a workspace switch leaves every connection (TRAP-15)");
    }

    // ── How a tab on a server presents itself ────────────────────────────────

    [Fact]
    public void Location_OnAServer_TitlesTheTabByTheConnectionAndPutsTheFolderBeside()
    {
        var panel = PanelWithThreeTabs(out _);

        ConnectTo(panel, Prod, "/var/log");

        var tab = panel.Tabs[0];
        tab.DisplayTitle.Should().Be("prod", "the user's pain: the tab kept the disk folder's name");
        tab.Annotation.Should().Be("log");
        tab.ToolTip.Should().Be("sftp://deploy@prod.example/var/log");
        tab.IsOnServer.Should().BeTrue();
    }

    [Fact]
    public void Location_AtTheServersRoot_HasNoFolderBesideTheTitle()
    {
        var panel = PanelWithThreeTabs(out _);

        ConnectTo(panel, Prod);

        panel.Tabs[0].Annotation.Should().BeEmpty();
    }

    [Fact]
    public void Location_OfAParkedTab_StaysWhileAnotherTabIsShown()
    {
        var panel = PanelWithThreeTabs(out _);
        ConnectTo(panel, Prod, "/var/log");

        panel.SwitchTabCommand.Execute(panel.Tabs[1]);

        panel.Tabs[0].DisplayTitle.Should().Be("prod");
        panel.Tabs[1].Location.Should().BeNull();
        panel.Tabs[1].DisplayTitle.Should().Be("b");
    }

    [Fact]
    public void Location_AfterLeavingTheServer_IsTheDiskFolderAgain()
    {
        var panel = PanelWithThreeTabs(out _);
        ConnectTo(panel, Prod);

        panel.FileList.LeaveConnection();

        panel.Tabs[0].Location.Should().BeNull();
        panel.Tabs[0].DisplayTitle.Should().Be("a");
    }

    [Fact]
    public void Location_OfAnArchiveOnAServer_IsStillTheServersTab()
    {
        var panel = PanelWithThreeTabs(out _);
        ConnectTo(panel, Prod, "/var/log");

        panel.FileList.Mount(new InMemoryFileSystem("zip"), kind: MountKind.Archive);

        var tab = panel.Tabs[0];
        tab.IsOnServer.Should().BeTrue();
        tab.DisplayTitle.Should().Be("prod");
        tab.ToolTip.Should().Be("sftp://deploy@prod.example/var/log", "the server's address, where the archive lies");
    }

    [Fact]
    public void RestoreSnapshot_WithATabWhosePathWasNull_DoesNotThrow()
    {
        var panel = CreatePanel(out _);
        panel.LoadInitialPath(A);
        var state = new WorkspacePanelState { Tabs = { new TabState { Path = null! } } };

        var act = () => panel.RestoreSnapshot(state);

        act.Should().NotThrow();
    }
}
