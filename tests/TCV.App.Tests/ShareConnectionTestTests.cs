using System.ComponentModel;
using FluentAssertions;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>
/// The connection test of a Windows share closes what it opened and nothing else, and its Cancel does not
/// wait for a server that does not answer (TRAP-96). Windows itself is replaced: a share here is a set of names.
/// </summary>
public class ShareConnectionTestTests
{
    private const string Root = @"\\nas\projects";
    private const string Folder = @"\\nas\projects\app";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly HashSet<string> _connected = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _closed = [];
    private readonly HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);

    private ManualResetEventSlim? _connectHangs;
    private ManualResetEventSlim? _lookHangs;
    private bool _folderExists = true;
    private Exception? _connectFails;
    private Exception? _closeFails;

    private ShareConnectionTest Create() => new(
        root =>
        {
            lock (_connected)
            {
                return _connected.Contains(root);
            }
        },
        (root, _, _) =>
        {
            _connectHangs?.Wait(Patience);
            if (_connectFails is { } failure)
            {
                throw failure;
            }

            lock (_connected)
            {
                _connected.Add(root);
            }
        },
        root =>
        {
            if (_closeFails is { } failure)
            {
                throw failure;
            }

            lock (_connected)
            {
                _closed.Add(root);
                _connected.Remove(root);
            }
        },
        _ =>
        {
            _lookHangs?.Wait(Patience);
            return _folderExists;
        },
        _wanted.Contains);

    private Task<bool> RunAsync(ShareConnectionTest? test = null, CancellationToken ct = default) =>
        (test ?? Create()).FolderExistsAsync(Root, Folder, "bob", "pw", ct);

    private async Task ClosedEventuallyAsync()
    {
        var started = Environment.TickCount64;
        while (!Closed())
        {
            Environment.TickCount64.Should().BeLessThan(started + 10_000, "the share has to be closed by now");
            await Task.Delay(10);
        }
    }

    private bool Closed()
    {
        lock (_connected)
        {
            return _closed.Count > 0;
        }
    }

    [Fact]
    public async Task FolderExistsAsync_ShareOpenedByTheTest_IsClosedAgain()
    {
        (await RunAsync()).Should().BeTrue();

        _closed.Should().Equal(Root);
        _connected.Should().BeEmpty();
    }

    [Fact]
    public async Task FolderExistsAsync_FolderMissing_SaysSoAndStillCloses()
    {
        _folderExists = false;

        (await RunAsync()).Should().BeFalse();

        _closed.Should().Equal(Root);
    }

    [Fact]
    public async Task FolderExistsAsync_ShareConnectedBeforeTheTest_IsLeftAlone()
    {
        _connected.Add(Root);

        (await RunAsync()).Should().BeTrue();

        _closed.Should().BeEmpty("one disconnect would end the connection someone else stands on");
        _connected.Should().Contain(Root);
    }

    [Fact]
    public async Task FolderExistsAsync_ConnectRefused_ThrowsAndClosesNothing()
    {
        _connectFails = new Win32Exception(1326);

        var run = () => RunAsync();

        await run.Should().ThrowAsync<Win32Exception>();
        _closed.Should().BeEmpty();
    }

    [Fact]
    public async Task FolderExistsAsync_WantedByThePanelsByNow_IsLeftOpen()
    {
        _wanted.Add(Root);

        await RunAsync();

        _closed.Should().BeEmpty();
    }

    [Fact]
    public async Task FolderExistsAsync_CloseRefusedBecauseInUse_IsNotTheTestsFailure()
    {
        _closeFails = new Win32Exception(2404);

        (await RunAsync()).Should().BeTrue();
    }

    [Fact]
    public async Task FolderExistsAsync_CancelWhileConnecting_ReturnsAtOnceAndClosesWhatComesBack()
    {
        using var hang = new ManualResetEventSlim();
        _connectHangs = hang;
        using var cts = new CancellationTokenSource();

        var run = RunAsync(ct: cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        var waited = () => run.WaitAsync(TimeSpan.FromSeconds(3));
        await waited.Should().ThrowAsync<OperationCanceledException>();
        _closed.Should().BeEmpty("the connect is still out there");

        hang.Set();
        await ClosedEventuallyAsync();
        _connected.Should().BeEmpty();
    }

    [Fact]
    public async Task FolderExistsAsync_CancelledConnectComesBackToAShareWantedByNow_LeavesItOpen()
    {
        using var hang = new ManualResetEventSlim();
        _connectHangs = hang;
        using var cts = new CancellationTokenSource();
        var run = RunAsync(ct: cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        var waited = () => run.WaitAsync(TimeSpan.FromSeconds(3));
        await waited.Should().ThrowAsync<OperationCanceledException>();

        _wanted.Add(Root);   // Connect, right after the Cancel
        hang.Set();
        await Task.Delay(200);

        _closed.Should().BeEmpty();
    }

    [Fact]
    public async Task FolderExistsAsync_CancelWhileLooking_ReturnsAtOnceAndCloses()
    {
        using var hang = new ManualResetEventSlim();
        _lookHangs = hang;
        using var cts = new CancellationTokenSource();

        var run = RunAsync(ct: cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        var waited = () => run.WaitAsync(TimeSpan.FromSeconds(3));
        await waited.Should().ThrowAsync<OperationCanceledException>();
        await ClosedEventuallyAsync();
        hang.Set();
    }

    [Fact]
    public async Task FolderExistsAsync_CancelledConnectComesBackDuringASecondTest_DoesNotCloseUnderIt()
    {
        var test = Create();
        using var hang = new ManualResetEventSlim();
        using var look = new ManualResetEventSlim();
        _connectHangs = hang;
        using var cts = new CancellationTokenSource();
        var first = RunAsync(test, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        var waited = () => first.WaitAsync(TimeSpan.FromSeconds(3));
        await waited.Should().ThrowAsync<OperationCanceledException>();

        _lookHangs = look;
        var second = RunAsync(test);
        hang.Set();
        await Task.Delay(200);

        _closed.Should().BeEmpty("the second test is looking through this connection");

        look.Set();
        (await second.WaitAsync(Patience)).Should().BeTrue();
    }
}
