using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>The connection the transfer queue keeps between items, so a folder logs in once.</summary>
public class KeptConnectionTests
{
    private sealed class Client(int number)
    {
        public int Number { get; } = number;
        public bool Alive { get; set; } = true;
    }

    private sealed class Server
    {
        public int Opened { get; private set; }
        public List<Client> Closed { get; } = [];
        public Exception? RefuseWith { get; set; }

        public KeptConnection<Client> Kept() => new(
            (_, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return RefuseWith is { } refusal ? throw refusal : Task.FromResult(new Client(++Opened));
            },
            c => c.Alive,
            c =>
            {
                Closed.Add(c);
                return Task.CompletedTask;
            });
    }

    private static readonly ConnectionInfo Login = new() { Host = "h", Username = "u", Password = "p" };

    private static Task RunAsync(KeptConnection<Client> kept, ConnectionInfo connection, Action<Client> work) =>
        kept.RunAsync(connection, c =>
        {
            work(c);
            return Task.CompletedTask;
        }, _ => true, CancellationToken.None);

    [Fact]
    public async Task RunAsync_ItemsOfOneServer_LogInOnce()
    {
        var server = new Server();
        var kept = server.Kept();

        for (var i = 0; i < 3; i++)
        {
            await RunAsync(kept, Login, _ => { });
        }

        server.Opened.Should().Be(1);
        server.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_SameLoginInACopy_KeepsTheConnection()
    {
        var server = new Server();
        var kept = server.Kept();

        await RunAsync(kept, new ConnectionInfo { Host = "h", Username = "u", Password = "p" }, _ => { });
        await RunAsync(kept, new ConnectionInfo { Host = "h", Username = "u", Password = "p" }, _ => { });

        server.Opened.Should().Be(1);
    }

    [Theory]
    [InlineData("other", "u", "p")]
    [InlineData("h", "other", "p")]
    [InlineData("h", "u", "corrected")]
    public async Task RunAsync_AnotherLogin_ClosesTheKeptOneAndOpensItsOwn(string host, string user, string password)
    {
        var server = new Server();
        var kept = server.Kept();

        await RunAsync(kept, Login, _ => { });
        await RunAsync(kept, new ConnectionInfo { Host = host, Username = user, Password = password }, _ => { });

        server.Opened.Should().Be(2);
        server.Closed.Select(c => c.Number).Should().Equal(1);
    }

    [Fact]
    public async Task RunAsync_KeptOneClosedMeanwhile_OpensAnother()
    {
        var server = new Server();
        var kept = server.Kept();
        Client? first = null;
        await RunAsync(kept, Login, c => first = c);

        first!.Alive = false;
        await RunAsync(kept, Login, _ => { });

        server.Opened.Should().Be(2);
        server.Closed.Should().Equal(first);
    }

    [Fact]
    public async Task RunAsync_KeptOneFailsWithoutSaying_TheWorkGetsANewOne()
    {
        var server = new Server();
        var kept = server.Kept();
        await RunAsync(kept, Login, _ => { });
        var on = new List<int>();

        await RunAsync(kept, Login, c =>
        {
            on.Add(c.Number);
            if (c.Number == 1)
            {
                throw new IOException("421 Timeout");
            }
        });

        on.Should().Equal(1, 2);
        server.Closed.Select(c => c.Number).Should().Equal(1);
    }

    [Fact]
    public async Task RunAsync_NewConnectionFails_IsTheRealAnswer()
    {
        var server = new Server();
        var kept = server.Kept();
        var tries = 0;

        var act = () => RunAsync(kept, Login, _ =>
        {
            tries++;
            throw new IOException("550 No such file");
        });

        await act.Should().ThrowAsync<IOException>();
        tries.Should().Be(1);
        server.Closed.Should().HaveCount(1, "a connection left by a failed transfer may still owe an answer");
    }

    [Fact]
    public async Task RunAsync_KeptOneFailsAndTheNewOneToo_IsTriedOnlyOnce()
    {
        var server = new Server();
        var kept = server.Kept();
        await RunAsync(kept, Login, _ => { });
        var tries = 0;

        var act = () => RunAsync(kept, Login, _ =>
        {
            tries++;
            throw new IOException("550 No such file");
        });

        await act.Should().ThrowAsync<IOException>();
        tries.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_NotTheConnectionsFault_DoesNotRetry()
    {
        var server = new Server();
        var kept = server.Kept();
        await RunAsync(kept, Login, _ => { });
        var tries = 0;

        var act = () => kept.RunAsync(Login, _ =>
        {
            tries++;
            throw new TransferTargetExistsException("/a", onServer: true);
        }, ex => ex is not TransferTargetExistsException, CancellationToken.None);

        await act.Should().ThrowAsync<TransferTargetExistsException>();
        tries.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_Cancelled_DoesNotRetry()
    {
        var server = new Server();
        var kept = server.Kept();
        await RunAsync(kept, Login, _ => { });
        using var cts = new CancellationTokenSource();
        var tries = 0;

        var act = () => kept.RunAsync(Login, _ =>
        {
            tries++;
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        }, _ => true, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        tries.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_LoginRefused_SaysSoAndKeepsNothing()
    {
        var server = new Server { RefuseWith = new IOException("530 Login incorrect") };
        var kept = server.Kept();

        var act = () => RunAsync(kept, Login, _ => { });

        (await act.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("530");
        server.Closed.Should().BeEmpty();
    }

    [Fact]
    public async Task Close_AfterTheLastItem_ClosesWhatIsKept()
    {
        var server = new Server();
        var kept = server.Kept();
        await RunAsync(kept, Login, _ => { });

        kept.Close();
        kept.Close();

        server.Closed.Should().HaveCount(1);

        await RunAsync(kept, Login, _ => { });
        server.Opened.Should().Be(2, "nothing is kept after closing");
    }
}

/// <summary>The queue against the FTP stub: logging in once, and a final reply that takes its time.</summary>
public sealed class TransferQueueFtpTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-queue-ftp-" + Guid.NewGuid().ToString("N"));
    private readonly FtpStubServer _server = new();
    private readonly TransferQueue _queue = new();

    public TransferQueueFtpTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _queue.CancelAllAsync().Wait(TimeSpan.FromSeconds(15));
        _queue.Dispose();
        _server.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort cleanup (TRAP-112).
        }
    }

    private TransferItem Download(string name) => new()
    {
        SourcePath = "/" + name,
        DestinationPath = Path.Combine(_dir, name),
        Connection = _server.Connection,
        Direction = TransferDirection.Download,
    };

    private Task<bool> FinishedAsync(params TransferItem[] items) =>
        FtpStubServer.EventuallyAsync(() => items.All(i => i.IsFinished), timeoutMs: 40_000);

    [Fact]
    public async Task Folder_OfSeveralFiles_LogsInOnceAndHangsUpAfterTheLast()
    {
        var items = Enumerable.Range(0, 5).Select(i => Download($"f{i}.bin")).ToList();
        foreach (var item in items)
        {
            _server.Seed(item.SourcePath, 100);
        }

        _queue.EnqueueRange(items);

        (await FinishedAsync([.. items])).Should().BeTrue();
        items.Should().AllSatisfy(i => i.Status.Should().Be(TransferStatus.Complete));
        _server.ConnectionCount.Should().Be(1);
        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 1)).Should().BeTrue("the queue has run dry");
    }

    [Fact]
    public async Task Item_AfterTheServerHungUp_StillGoesThrough()
    {
        var gate = new TaskCompletionSource();
        _server.Seed("/a.bin", 100);
        _server.Seed("/b.bin", 100);
        var (first, second) = (Download("a.bin"), Download("b.bin"));
        _queue.Finished += (_, e) =>
        {
            if (e.Item == first)
            {
                _server.HangUp();
                gate.TrySetResult();
            }
        };

        _queue.EnqueueRange([first, second]);

        (await FinishedAsync(first, second)).Should().BeTrue();
        await gate.Task;
        second.Status.Should().Be(TransferStatus.Complete, second.ErrorMessage);
    }

    private TransferItem Upload(string name)
    {
        var local = Path.Combine(_dir, name);
        File.WriteAllBytes(local, new byte[1000]);
        return new TransferItem
        {
            SourcePath = local,
            DestinationPath = "/" + name,
            Connection = _server.Connection,
            Direction = TransferDirection.Upload,
            OverwriteExisting = true,
        };
    }

    /// <summary>The reply comes this long after the server has all of the data. FluentFTP's own limit is 15 s (TRAP-100).</summary>
    private async Task ReplyAfterAsync(string name, TimeSpan delay)
    {
        (await FtpStubServer.EventuallyAsync(() => _server.ReceivedToEnd.ContainsKey("/" + name))).Should().BeTrue();
        await Task.Delay(delay);
        _server.FinalReplyGate!.SetResult();
    }

    [Fact]
    public async Task Upload_ReplyWithinThePatience_IsNoError()
    {
        _server.FinalReplyGate = new TaskCompletionSource();
        using var queue = new TransferQueue { FtpReplyWait = TimeSpan.FromSeconds(30) };
        var item = Upload("up.bin");

        queue.Enqueue(item);
        await ReplyAfterAsync("up.bin", TimeSpan.FromSeconds(1));

        (await FinishedAsync(item)).Should().BeTrue();
        item.Status.Should().Be(TransferStatus.Complete, item.ErrorMessage);
        _server.StoredBytes("/up.bin").Should().Be(1000);
    }

    [Fact]
    public async Task Upload_ReplyLaterThanThePatience_IsAnError()
    {
        // What proves the limit on the reply is the one the queue sets, and not the library's 15 s.
        _server.FinalReplyGate = new TaskCompletionSource();
        using var queue = new TransferQueue { FtpReplyWait = TimeSpan.FromSeconds(1) };
        var item = Upload("late.bin");

        queue.Enqueue(item);

        (await FinishedAsync(item)).Should().BeTrue();
        item.Status.Should().Be(TransferStatus.Failed);
    }

    [Fact]
    public async Task Download_ServerGivesUpMidway_IsAFailure()
    {
        _server.Seed("/cut.bin", 100_000);
        _server.DownloadSendsOnly = 50_000;
        _server.DownloadFinalReply = "426 Connection closed; transfer aborted";
        var item = Download("cut.bin");

        _queue.Enqueue(item);

        (await FinishedAsync(item)).Should().BeTrue();
        item.Status.Should().Be(TransferStatus.Failed);
    }
}
