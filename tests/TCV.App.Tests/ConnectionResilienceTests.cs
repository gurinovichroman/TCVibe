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
