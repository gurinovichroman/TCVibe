using System.IO;
using System.Net.Sockets;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.App.FileSystem;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// A panel whose connection drops (6.82): it reconnects by itself in its tab, with a server that fails
/// on demand standing in for the network; it stops when the user or the panel moves on; and the
/// connections every tab keeps open are pinged, each held open while its ping is out (INV-09).
/// </summary>
public class ConnectionResilienceTests
{
    private static readonly ReconnectPolicy ThreeTries = new(
        [TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1)]);

    private static ConnectionInfo Prod() => new()
    {
        Name = "prod", Protocol = NetworkProtocol.Sftp, Host = "prod.example", Port = 22, Username = "deploy",
    };

    private static FileListViewModel List()
    {
        var local = new InMemoryFileSystem("file");
        local.AddDirectory("/C:/work");
        var list = new FileListViewModel(local, NullLogger<FileListViewModel>.Instance);
        list.CurrentPath = "/C:/work";
        return list;
    }

    /// <summary>A server with <c>/srv/app.log</c>; <paramref name="drop"/> makes its listing fail as a dropped line.</summary>
    private static InMemoryFileSystem Server(bool drop = false)
    {
        var server = new InMemoryFileSystem("sftp");
        server.AddFile("/srv/app.log", [1, 2, 3]);
        if (drop)
        {
            server.FailListingOf = "/srv";
            server.FailListingWith = () => new SocketException((int)SocketError.ConnectionReset);
        }

        return server;
    }

    /// <summary>Opens the next scripted file system, or throws the next scripted failure.</summary>
    private sealed class ScriptedOpener(params object[] script)
    {
        private readonly Queue<object> _script = new(script);

        public int Opened { get; private set; }

        public Task<IVirtualFileSystem> OpenAsync(ConnectionInfo info, CancellationToken ct)
        {
            Opened++;
            return _script.Dequeue() switch
            {
                IVirtualFileSystem vfs => Task.FromResult(vfs),
                Exception ex => Task.FromException<IVirtualFileSystem>(ex),
                _ => throw new InvalidOperationException(),
            };
        }
    }

    private static async Task<ReconnectFinished> FinishedAsync(PanelReconnector reconnector, Func<Task>? whileRunning = null)
    {
        var finished = new TaskCompletionSource<ReconnectFinished>(TaskCreationOptions.RunContinuationsAsynchronously);
        reconnector.Finished += f => finished.TrySetResult(f);
        if (whileRunning is not null)
        {
            await whileRunning();
        }

        var done = await Task.WhenAny(finished.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        done.Should().BeSameAs(finished.Task, "the reconnect must end");
        return await finished.Task;
    }

    // ── Reconnecting by itself ────────────────────────────────────────────────

    [Fact]
    public async Task DroppedListing_ReconnectsInItsTab_OnTheSecondTry()
    {
        var list = List();
        var dead = Server(drop: true);
        var fresh = Server();
        var opener = new ScriptedOpener(new SocketException((int)SocketError.HostUnreachable), fresh);
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries);
        var connection = Prod();

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(dead, "/srv", connection, MountKind.Remote);   // its listing drops
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Connected);
        finished.Attempts.Should().Be(2);
        list.ActiveVfs.Should().BeSameAs(fresh, "the fresh connection replaces the dead one under the tab");
        list.RemoteConnection.Should().BeSameAs(connection);
        list.CurrentPath.Should().Be("/srv");
        list.LoadError.Should().BeNull();
        list.Files.Should().Contain(f => f.Name == "app.log");
        dead.IsDisposed.Should().BeTrue();
        reconnector.IsRunning.Should().BeFalse();

        list.LeaveConnection();
        list.CurrentPath.Should().Be("/C:/work", "the folder the server was opened from is still under it");
    }

    [Fact]
    public async Task FreshConnectionWhoseListingDropsToo_IsTriedAgain()
    {
        var list = List();
        var flaky = Server();
        flaky.FailListingOf = "/srv";
        flaky.FailListingWith = () => new SocketException((int)SocketError.ConnectionReset);
        var fresh = Server();
        var reconnector = new PanelReconnector(list, new ScriptedOpener(flaky, fresh).OpenAsync, ThreeTries);

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(Server(drop: true), "/srv", Prod(), MountKind.Remote);
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Connected);
        finished.Attempts.Should().Be(2, "a stat that answers is not enough: the listing decides");
        list.ActiveVfs.Should().BeSameAs(fresh);
        flaky.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task ChangedHostKey_IsARefusal_NotRetried()
    {
        var list = List();
        var changed = new HostKeyChangedException("prod.example:22", "SHA256:old", "SHA256:new");
        var opener = new ScriptedOpener(changed, Server());
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries);
        var dead = Server(drop: true);

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(dead, "/srv", Prod(), MountKind.Remote);
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Refused);
        finished.LastError.Should().BeSameAs(changed, "TRAP-43: shown as it is, never retried or asked about");
        finished.StillOnConnection.Should().BeTrue();
        opener.Opened.Should().Be(1);
        list.ActiveVfs.Should().BeSameAs(dead);
    }

    [Fact]
    public async Task ServerStaysAway_GivesUpAfterTheLastTry()
    {
        var list = List();
        var opener = new ScriptedOpener(new SocketException(), new SocketException(), new SocketException());
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries);

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(Server(drop: true), "/srv", Prod(), MountKind.Remote);
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.GaveUp);
        finished.Attempts.Should().Be(3);
        finished.LastError.Should().BeOfType<SocketException>();
    }

    [Fact]
    public void AFolderThatMayNotBeRead_IsNoDroppedConnection()
    {
        var list = List();
        var server = Server();
        server.FailListingOf = "/srv";   // access denied
        var opener = new ScriptedOpener();
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries);

        list.Mount(server, "/srv", Prod(), MountKind.Remote);

        reconnector.IsRunning.Should().BeFalse();
        list.HasLoadError.Should().BeTrue();
        opener.Opened.Should().Be(0);
    }

    [Fact]
    public async Task LeavingTheConnection_StopsTheReconnect()
    {
        var list = List();
        var opener = new ScriptedOpener(Server());
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries,
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(Server(drop: true), "/srv", Prod(), MountKind.Remote);
            reconnector.IsRunning.Should().BeTrue();
            list.LeaveConnection();   // Disconnect, another tab, a workspace switch
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Cancelled);
        finished.StoppedByUser.Should().BeFalse();
        opener.Opened.Should().Be(0);
    }

    [Fact]
    public async Task Stop_LeavesTheErrorOnScreen()
    {
        var list = List();
        var reconnector = new PanelReconnector(list, new ScriptedOpener(Server()).OpenAsync, ThreeTries,
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(Server(drop: true), "/srv", Prod(), MountKind.Remote);
            reconnector.Stop().Should().BeTrue();
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Cancelled);
        finished.StoppedByUser.Should().BeTrue();
        list.HasLoadError.Should().BeTrue("the user sees why, with Reconnect and Disconnect under it");
    }

    [Fact]
    public async Task Stop_WhileATryHangsInItsConnect_EndsAtOnce_AndTheTryLeavesThePanelAlone()
    {
        var list = List();
        var dead = Server(drop: true);
        var connecting = new TaskCompletionSource<IVirtualFileSystem>(TaskCreationOptions.RunContinuationsAsynchronously);
        var trying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnector = new PanelReconnector(list, (_, _) =>
        {
            trying.TrySetResult();
            return connecting.Task;   // a connect that sits out its timeout, deaf to the token
        }, ThreeTries);
        var ended = new List<ReconnectFinished>();
        reconnector.Finished += ended.Add;

        list.Mount(dead, "/srv", Prod(), MountKind.Remote);   // its listing drops
        await trying.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var running = reconnector.Running!;

        reconnector.Stop().Should().BeTrue();

        ended.Should().ContainSingle().Which.StoppedByUser.Should().BeTrue();
        reconnector.IsRunning.Should().BeFalse("Reconnect and the next drop must not wait for the hung try");
        reconnector.RetryNow().Should().BeFalse();

        var late = Server();
        connecting.SetResult(late);   // the connect returns after all
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        ended.Should().ContainSingle("a reconnect ends once");
        list.ActiveVfs.Should().BeSameAs(dead, "a stopped try does not put its connection in");
        late.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task RetryNow_DoesNotWaitOutThePause()
    {
        var list = List();
        var fresh = Server();
        var reconnector = new PanelReconnector(list, new ScriptedOpener(fresh).OpenAsync, ThreeTries,
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var finished = await FinishedAsync(reconnector, () =>
        {
            list.Mount(Server(drop: true), "/srv", Prod(), MountKind.Remote);
            reconnector.RetryNow().Should().BeTrue();
            return Task.CompletedTask;
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Connected);
        list.ActiveVfs.Should().BeSameAs(fresh);
    }

    [Fact]
    public async Task ARefreshThatReadsTheFolder_EndsTheReconnectAsConnected()
    {
        var list = List();
        var dead = Server(drop: true);
        var opener = new ScriptedOpener();
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries,
            delay: (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var finished = await FinishedAsync(reconnector, async () =>
        {
            list.Mount(dead, "/srv", Prod(), MountKind.Remote);
            dead.FailListingOf = null;   // the line came back, and the session with it
            await list.LoadAsync("/srv");
        });

        finished.Outcome.Should().Be(ReconnectOutcome.Connected);
        opener.Opened.Should().Be(0);
        list.ActiveVfs.Should().BeSameAs(dead);
    }

    // ── Giving up on a server that keeps the panel waiting ────────────────────

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 2000 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        condition().Should().BeTrue();
    }

    [Fact]
    public async Task Stop_WhileAFreshListingHangs_FreesThePanel_AndItsFailureStartsNoNewReconnect()
    {
        var list = List();
        var fresh = Server(drop: true);
        var hold = fresh.HoldNextListing();
        var opener = new ScriptedOpener(fresh, Server());
        var reconnector = new PanelReconnector(list, opener.OpenAsync, ThreeTries);
        var ended = new List<ReconnectFinished>();
        reconnector.Finished += ended.Add;

        list.Mount(Server(drop: true), "/srv", Prod(), MountKind.Remote);   // drops; the try's listing then hangs
        await UntilAsync(() => ReferenceEquals(list.ActiveVfs, fresh) && list.IsOpeningFolder);
        var theDrop = list.LoadError;
        theDrop.Should().NotBeNull();

        reconnector.Stop().Should().BeTrue();

        ended.Should().ContainSingle().Which.StoppedByUser.Should().BeTrue();
        list.IsLoading.Should().BeFalse("Stop must not leave the panel waiting for the server's timeout");
        list.IsOpeningFolder.Should().BeFalse();
        list.HasNewerLoadInFlight.Should().BeFalse();
        list.LoadError.Should().Be(theDrop, "the reason stays, with Reconnect and Disconnect under it");

        hold.SetResult();   // the server answers at last, with the failure
        await Task.Delay(100);

        reconnector.IsRunning.Should().BeFalse("the user stopped it: a late failure does not start it over");
        opener.Opened.Should().Be(1);
        list.LoadError.Should().Be(theDrop);
    }

    [Fact]
    public void ShowsLoadingOverlay_WhileReconnecting_LeavesTheStatusAndItsButtonsFree()
    {
        var list = List();
        var server = Server();
        var hold = server.HoldNextListing();

        list.Mount(server, "/srv", Prod(), MountKind.Remote);
        list.ShowsLoadingOverlay.Should().BeTrue();

        list.ReconnectStatus = "Reconnecting to “prod” — attempt 1 of 8…";
        list.ShowsLoadingOverlay.Should().BeFalse("it would take the clicks of Stop and Retry now");
        list.EmptyStateText.Should().Be(list.ReconnectStatus);

        list.ReconnectStatus = null;
        list.ShowsLoadingOverlay.Should().BeTrue();
        hold.SetResult();
    }

    [Fact]
    public async Task StopLoading_WhileAServerKeepsTheListingWaiting_ReleasesThePanelAtOnce_AndTheLateListingDoesNotLand()
    {
        var list = List();
        var server = Server();
        var opener = new ScriptedOpener();
        _ = new PanelReconnector(list, opener.OpenAsync, ThreeTries);
        var hold = server.HoldNextListing();
        list.Mount(server, "/srv", Prod(), MountKind.Remote);

        list.StopLoading().Should().BeTrue();

        list.IsLoading.Should().BeFalse();
        list.ShowsLoadingOverlay.Should().BeFalse();
        list.HasNewerLoadInFlight.Should().BeFalse();
        list.HasLoadError.Should().BeTrue();
        list.IsConnectionFailed.Should().BeTrue("Reconnect and Disconnect show under the reason");
        list.EmptyStateText.Should().Be(list.LoadError);
        list.RemoteConnection.Should().NotBeNull("giving up on a listing is not leaving the connection");

        hold.SetResult();   // the listing comes back after all
        await Task.Delay(100);

        list.Files.Should().NotContain(f => f.Name == "app.log", "it was given up on");
        list.HasLoadError.Should().BeTrue();
        opener.Opened.Should().Be(0, "the user stopped reading: nothing dropped");

        await list.LoadAsync("/srv");   // Refresh

        list.HasLoadError.Should().BeFalse();
        list.Files.Should().Contain(f => f.Name == "app.log");
    }

    [Fact]
    public void StopLoading_ANewErrorReplacesAnOlderOne_UnlessAskedToKeepIt()
    {
        var list = List();
        var server = Server(drop: true);
        list.Mount(server, "/srv", Prod(), MountKind.Remote);
        var older = list.LoadError;
        older.Should().NotBeNull();

        server.HoldNextListing();
        _ = list.LoadAsync("/srv");
        list.StopLoading(keepError: true).Should().BeTrue();
        list.LoadError.Should().Be(older);

        server.HoldNextListing();
        _ = list.LoadAsync("/srv");
        list.StopLoading().Should().BeTrue();
        list.LoadError.Should().NotBe(older, "the panel now says the reading was stopped, not what failed before");
    }

    [Fact]
    public void StopLoading_WithNothingBeingRead_ChangesNothing()
    {
        var list = List();

        list.StopLoading().Should().BeFalse();

        list.HasLoadError.Should().BeFalse();
    }

    [Fact]
    public void StopLoading_OnADiskFolderThatKeepsItWaiting_SaysSo_WithNoConnectionToReopen()
    {
        // A share that stopped answering is a disk path: no mount, no Reconnect, and it can be given up too.
        var disk = new InMemoryFileSystem("file");
        disk.AddFile("/C:/share/a.txt", [1]);
        var list = new FileListViewModel(disk, NullLogger<FileListViewModel>.Instance);
        var hold = disk.HoldNextListing();
        list.CurrentPath = "/C:/share";
        list.ShowsLoadingOverlay.Should().BeTrue();

        list.StopLoading().Should().BeTrue();

        list.IsLoading.Should().BeFalse();
        list.ShowsLoadingOverlay.Should().BeFalse();
        list.HasLoadError.Should().BeTrue();
        list.IsConnectionFailed.Should().BeFalse("there is nothing to reconnect to");
        hold.SetResult();
    }

    [Fact]
    public async Task StopLoading_WithAFileSystemDeafToTheToken_EndsTheLoadAtOnce_AndRefreshIsEnabledAgain()
    {
        var list = List();
        var server = Server();
        list.Mount(server, "/srv", Prod(), MountKind.Remote);
        var hold = server.HoldNextListing();

        var refreshing = list.RefreshCommand.ExecuteAsync(null);
        list.RefreshCommand.CanExecute(null).Should().BeFalse("the listing is being read");

        list.StopLoading().Should().BeTrue();

        await refreshing.WaitAsync(TimeSpan.FromSeconds(10));
        list.RefreshCommand.CanExecute(null).Should().BeTrue("the user just gave up on it: nothing holds the panel now");
        hold.SetResult();
    }

    [Fact]
    public async Task ReloadForFolderChange_AfterStopLoading_LeavesTheFolderGivenUpOn_UntilALoadIsAskedFor()
    {
        var disk = new InMemoryFileSystem("file");
        disk.AddFile("/C:/share/a.txt", [1]);
        var list = new FileListViewModel(disk, NullLogger<FileListViewModel>.Instance);
        var hold = disk.HoldNextListing();
        list.CurrentPath = "/C:/share";
        list.StopLoading().Should().BeTrue();
        var reads = disk.ListCalls;

        list.ReloadForFolderChange();

        disk.ListCalls.Should().Be(reads, "Cancel is final: the next change in the folder does not start it over");
        list.IsLoading.Should().BeFalse();
        list.HasLoadError.Should().BeTrue();
        hold.SetResult();

        await list.LoadAsync("/C:/share");   // Refresh
        list.HasLoadError.Should().BeFalse();
        reads = disk.ListCalls;

        list.ReloadForFolderChange();

        disk.ListCalls.Should().Be(reads + 1, "the user asked for this folder again, and it is watched as usual");
    }

    [Fact]
    public void ConnectingText_FollowsTheConnect_AndTheSignInInTheBrowser()
    {
        var panel = Panel();
        var heard = new List<string?>();
        panel.PropertyChanged += (_, e) => heard.Add(e.PropertyName);
        panel.ConnectingText.Should().BeNull();

        panel.ConnectingTo = Prod();

        var connecting = panel.ConnectingText;
        connecting.Should().NotBeNullOrEmpty();
        heard.Should().Contain(nameof(PanelViewModel.ConnectingText));

        panel.IsSigningIn = true;
        panel.ConnectingText.Should().NotBe(connecting, "the wait is now the user's own sign-in");

        panel.IsSigningIn = false;
        panel.ConnectingTo = null;
        panel.ConnectingText.Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_OfAFolderThatFailed_HidesTheOlderErrorUntilItsOwnOutcomeIsIn()
    {
        var list = List();
        var server = Server(drop: true);
        list.Mount(server, "/srv", Prod(), MountKind.Remote);
        list.EmptyStateText.Should().Be(list.LoadError);

        var hold = server.HoldNextListing();
        var opening = list.LoadAsync("/srv");

        list.ShowsLoadingOverlay.Should().BeTrue();
        list.EmptyStateText.Should().BeEmpty("the error and its buttons would lie under the overlay");

        server.FailListingOf = null;
        hold.SetResult();
        await opening;

        list.ShowsLoadingOverlay.Should().BeFalse();
        list.HasLoadError.Should().BeFalse();
    }

    // ── Swapping the connection under the tab ─────────────────────────────────

    [Fact]
    public void ReplaceRemoteFileSystem_OnAnArchive_RefusesAndLeavesItToTheCaller()
    {
        var list = List();
        list.Mount(Server(), "/srv", Prod(), MountKind.Remote);
        list.Mount(new InMemoryFileSystem("zip"), "/", kind: MountKind.Archive);
        var fresh = Server();

        list.ReplaceRemoteFileSystem(fresh, "/srv").Should().BeFalse();
        fresh.IsDisposed.Should().BeFalse();
    }

    [Fact]
    public void ReplaceRemoteFileSystem_WhileAnOperationStreams_ClosesTheOldOneOnlyAfterIt()
    {
        var list = List();
        var old = Server();
        list.Mount(old, "/srv", Prod(), MountKind.Remote);
        var lease = list.LeaseActiveVfs();

        list.ReplaceRemoteFileSystem(Server(), "/srv").Should().BeTrue();
        old.IsDisposed.Should().BeFalse("INV-09: a running operation still reads from it");

        lease.Dispose();
        old.IsDisposed.Should().BeTrue();
    }

    // ── Keep-alive of every tab's connection ──────────────────────────────────

    private static PanelViewModel Panel()
    {
        var local = new InMemoryFileSystem("file");
        local.AddDirectory("/C:/a");
        var drives = new DriveManager();
        var panel = new PanelViewModel("Left", new FileListViewModel(local, NullLogger<FileListViewModel>.Instance),
            new DriveBarViewModel(drives), new StatusBarViewModel(drives), new AppSettings(), new FileWatcher());
        panel.LoadInitialPath("/C:/a");
        return panel;
    }

    [Fact]
    public void OpenFileSystems_IncludesTheOnesBackgroundTabsKeep()
    {
        var panel = Panel();
        var parkedServer = Server();
        panel.FileList.Mount(parkedServer, "/srv", Prod(), MountKind.Remote);
        panel.NewTabCommand.Execute(null);   // the server waits, open, in the tab left behind (ADR-0032)
        var onScreen = Server();
        panel.FileList.Mount(onScreen, "/srv", Prod(), MountKind.Remote);

        panel.OpenFileSystems().Should().BeEquivalentTo(new[] { parkedServer, onScreen });
        panel.OpenFileSystems().Should().NotContain(panel.FileList.DiskVfs);
    }

    [Fact]
    public void LeaseFileSystem_KeepsAParkedConnectionOpenWhileItsTabCloses()
    {
        var panel = Panel();
        var server = Server();
        panel.FileList.Mount(server, "/srv", Prod(), MountKind.Remote);
        var serverTab = panel.ActiveTab!;
        panel.NewTabCommand.Execute(null);
        var lease = panel.FileList.LeaseFileSystem(server);   // a ping is out

        panel.CloseTabCommand.Execute(serverTab);
        server.IsDisposed.Should().BeFalse("the keep-alive still talks to it");

        lease.Dispose();
        server.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task PingAsync_PingsWhatCanBeKeptAlive_EachLeasedOnce()
    {
        var pinged = new List<string>();
        var leases = 0;
        var released = 0;
        var pingedWithoutLease = false;
        IVirtualFileSystem KeepAlive(string name, Exception? fails = null)
        {
            var mock = new Mock<IVirtualFileSystem>();
            mock.SetupGet(v => v.DisplayName).Returns(name);
            mock.As<IKeepAlive>().Setup(k => k.KeepAliveAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    lock (pinged)
                    {
                        pinged.Add(name);
                        pingedWithoutLease |= Volatile.Read(ref leases) <= Volatile.Read(ref released);
                    }

                    return fails is null ? Task.CompletedTask : Task.FromException(fails);
                });
            return mock.Object;
        }

        var ftp = KeepAlive("ftp");
        var broken = KeepAlive("broken", new IOException("gone"));
        var http = new InMemoryFileSystem("s3");
        KeepAliveTarget Target(IVirtualFileSystem vfs) => new(vfs, () =>
        {
            Interlocked.Increment(ref leases);
            return new Release(() => Interlocked.Increment(ref released));
        });

        await ConnectionKeepAlive.PingAsync(
            [Target(ftp), Target(broken), Target(http), Target(ftp)], NullLogger.Instance);

        pinged.Should().BeEquivalentTo(["ftp", "broken"], "a failed ping does not stop the others, and HTTP has nothing to keep");
        pingedWithoutLease.Should().BeFalse("a target is held open while its ping is out (INV-09)");
        leases.Should().Be(2);
        released.Should().Be(2);
    }

    private sealed class Release(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    // ── The chip ──────────────────────────────────────────────────────────────

    [Fact]
    public void Chip_WhileReconnecting_SaysSoAndOffersToStop()
    {
        var panel = Panel();
        panel.FileList.Mount(Server(), "/srv", Prod(), MountKind.Remote);

        panel.FileList.ReconnectStatus = "Lost the connection to “prod” — next try in 5 s (2 of 8).";

        panel.ChipState.Should().Be(ConnectionChipState.Reconnecting);
        panel.IsReconnecting.Should().BeTrue();
        panel.ChipToolTip.Should().Contain("next try in 5 s");
        panel.FileList.EmptyStateText.Should().StartWith("Lost the connection");
        panel.FileList.IsConnectionFailed.Should().BeFalse("Retry now and Stop replace Reconnect and Disconnect meanwhile");

        panel.FileList.ReconnectStatus = null;
        panel.ChipState.Should().NotBe(ConnectionChipState.Reconnecting);
    }
}
