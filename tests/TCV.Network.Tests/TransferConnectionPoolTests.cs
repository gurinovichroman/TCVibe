using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>
/// The spare connections FTP transfers ride on, so the panel's connection stays free for listing: a
/// finished transfer's connection is reused, a cut one is closed, a server that refuses one more is
/// not asked again, and a changed host identity is not mistaken for such a refusal.
/// </summary>
public class TransferConnectionPoolTests
{
    private sealed class Connection(int number)
    {
        public int Number { get; } = number;
        public bool Alive { get; set; } = true;
        public bool Closed { get; set; }
    }

    /// <summary>A server that opens numbered connections, or refuses with the next queued failure.</summary>
    private sealed class Server
    {
        private readonly Queue<Exception?> _answers = new();
        public int Opened { get; private set; }
        public List<Connection> Closed { get; } = [];
        public DateTime Now { get; set; } = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);

        public void ThenRefuse(Exception failure) => _answers.Enqueue(failure);
        public void ThenAccept() => _answers.Enqueue(null);

        public TransferConnectionPool<Connection> Pool() => new(
            ct =>
            {
                ct.ThrowIfCancellationRequested();
                if (_answers.Count > 0 && _answers.Dequeue() is { } failure)
                {
                    throw failure;
                }

                return Task.FromResult(new Connection(++Opened));
            },
            c => c.Alive,
            c =>
            {
                c.Closed = true;
                Closed.Add(c);
            },
            () => Now);
    }

    private static IOException TooManyConnections() => new("421 Too many connections from this IP");

    [Fact]
    public async Task RentAsync_NothingKept_OpensANewConnection()
    {
        var server = new Server();
        using var pool = server.Pool();

        var connection = await pool.RentAsync(CancellationToken.None);

        connection.Should().NotBeNull();
        server.Opened.Should().Be(1);
        pool.Count.Should().Be(1);
    }

    [Fact]
    public async Task Return_TransferFinished_ConnectionCarriesTheNextOne()
    {
        var server = new Server();
        using var pool = server.Pool();

        var first = await pool.RentAsync(CancellationToken.None);
        pool.Return(first!, reusable: true);
        var second = await pool.RentAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        server.Opened.Should().Be(1);
    }

    [Fact]
    public async Task Return_TransferCutShort_ConnectionIsClosed()
    {
        var server = new Server();
        using var pool = server.Pool();

        var cut = await pool.RentAsync(CancellationToken.None);
        pool.Return(cut!, reusable: false);

        cut!.Closed.Should().BeTrue();
        pool.Count.Should().Be(0);
        (await pool.RentAsync(CancellationToken.None)).Should().NotBeSameAs(cut);
    }

    [Fact]
    public async Task RentAsync_TwoTransfersAtOnce_EachGetsAConnection()
    {
        // A copy from one folder of a server to another reads and writes at the same time: on one
        // shared connection the write waited for the read to let go, and the read waited for the write.
        var server = new Server();
        using var pool = server.Pool();

        var read = await pool.RentAsync(CancellationToken.None);
        var write = await pool.RentAsync(CancellationToken.None);

        write.Should().NotBeSameAs(read);
        pool.Count.Should().Be(2);
    }

    [Fact]
    public async Task RentAsync_KeptConnectionDroppedByServer_IsReplaced()
    {
        var server = new Server();
        using var pool = server.Pool();
        var kept = await pool.RentAsync(CancellationToken.None);
        pool.Return(kept!, reusable: true);
        kept!.Alive = false;

        var next = await pool.RentAsync(CancellationToken.None);

        next.Should().NotBeSameAs(kept);
        kept.Closed.Should().BeTrue();
        pool.Count.Should().Be(1);
    }

    [Fact]
    public async Task RentAsync_ServerRefusesTheFirst_ReturnsNullAndDoesNotAskAgain()
    {
        var server = new Server();
        server.ThenRefuse(TooManyConnections());
        using var pool = server.Pool();

        (await pool.RentAsync(CancellationToken.None)).Should().BeNull();
        (await pool.RentAsync(CancellationToken.None)).Should().BeNull();

        server.Opened.Should().Be(0);
        pool.HasCeiling.Should().BeTrue();
    }

    [Fact]
    public async Task RentAsync_ServerRefusesTheSecond_TheFirstStaysInUse()
    {
        var server = new Server();
        server.ThenAccept();
        server.ThenRefuse(TooManyConnections());
        using var pool = server.Pool();

        var first = await pool.RentAsync(CancellationToken.None);
        (await pool.RentAsync(CancellationToken.None)).Should().BeNull();
        (await pool.RentAsync(CancellationToken.None)).Should().BeNull("one more was refused; it is not asked for again");

        pool.Return(first!, reusable: true);
        (await pool.RentAsync(CancellationToken.None)).Should().BeSameAs(first);
        server.Opened.Should().Be(1);
    }

    [Fact]
    public async Task RentAsync_RefusedWhileACutConnectionIsStillClosing_AsksAgainLater()
    {
        // A cut transfer's connection is closed in the background, and a server with a per-user limit
        // still counts it for a moment: that refusal must not stop transfer connections for good.
        var server = new Server();
        server.ThenAccept();
        server.ThenRefuse(TooManyConnections());
        using var pool = server.Pool();

        var cut = await pool.RentAsync(CancellationToken.None);
        pool.Return(cut!, reusable: false);
        (await pool.RentAsync(CancellationToken.None)).Should().BeNull("the closing one still counts on the server");

        var next = await pool.RentAsync(CancellationToken.None);

        next.Should().NotBeNull();
        server.Opened.Should().Be(2);
    }

    [Fact]
    public async Task RentAsync_CancelledConnectFailsWithAnotherError_ThrowsAndLeavesNoCeiling()
    {
        // A TLS handshake or a socket cut by the cancel surfaces as an IOException, not a cancellation.
        using var cts = new CancellationTokenSource();
        using var pool = new TransferConnectionPool<object>(
            async _ =>
            {
                await cts.CancelAsync();
                throw new IOException("The handshake was aborted");
            },
            _ => true,
            _ => { });

        var rent = () => pool.RentAsync(cts.Token);

        await rent.Should().ThrowAsync<IOException>();
        pool.HasCeiling.Should().BeFalse();
        pool.Count.Should().Be(0);
    }

    [Fact]
    public async Task RentAsync_HostIdentityChanged_ThrowsInsteadOfFallingBack()
    {
        // TRAP-43: a replaced certificate is a hard refusal of the server, not "no spare connection".
        var server = new Server();
        server.ThenRefuse(new AggregateException(new HostKeyChangedException("ftp.example.com:21", "SHA256:old", "SHA256:new")));
        using var pool = server.Pool();

        var rent = () => pool.RentAsync(CancellationToken.None);

        await rent.Should().ThrowAsync<HostKeyChangedException>();
        pool.HasCeiling.Should().BeFalse();
        pool.Count.Should().Be(0);
    }

    [Fact]
    public async Task RentAsync_Cancelled_ThrowsAndLeavesNoCeiling()
    {
        var server = new Server();
        using var pool = server.Pool();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var rent = () => pool.RentAsync(cts.Token);

        await rent.Should().ThrowAsync<OperationCanceledException>();
        pool.HasCeiling.Should().BeFalse();
        pool.Count.Should().Be(0);
    }

    [Fact]
    public async Task CloseIdle_ClosesOnlyConnectionsIdleLongEnough()
    {
        var server = new Server();
        using var pool = server.Pool();
        var old = await pool.RentAsync(CancellationToken.None);
        var recent = await pool.RentAsync(CancellationToken.None);
        var busy = await pool.RentAsync(CancellationToken.None);
        pool.Return(old!, reusable: true);
        server.Now += TimeSpan.FromSeconds(30);
        pool.Return(recent!, reusable: true);
        server.Now += TimeSpan.FromSeconds(30);

        var closed = pool.CloseIdle(TimeSpan.FromSeconds(60));

        closed.Should().Be(1);
        old!.Closed.Should().BeTrue();
        recent!.Closed.Should().BeFalse("it has been idle for half a minute");
        busy!.Closed.Should().BeFalse("a connection carrying a transfer is not idle");
        pool.Count.Should().Be(2);
    }

    [Fact]
    public async Task Dispose_ClosesKeptConnectionsAndThoseReturnedLater()
    {
        var server = new Server();
        var pool = server.Pool();
        var kept = await pool.RentAsync(CancellationToken.None);
        var busy = await pool.RentAsync(CancellationToken.None);
        pool.Return(kept!, reusable: true);

        pool.Dispose();
        kept!.Closed.Should().BeTrue();
        busy!.Closed.Should().BeFalse("its transfer is still running");

        pool.Return(busy, reusable: true);
        busy.Closed.Should().BeTrue();
        pool.Count.Should().Be(0);
    }
}
