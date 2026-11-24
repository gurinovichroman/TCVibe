using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>
/// What a transfer stream tells its connection's owner when it is closed: the connection carries the
/// next transfer only after a download read to its end or an upload that was neither cut nor aborted.
/// </summary>
public class TransferStreamTests
{
    private readonly List<bool> _closed = [];

    private TransferStream Download(byte[] content) => new(new MemoryStream(content), download: true, _closed.Add);

    private TransferStream Upload(Stream? inner = null) => new(inner ?? new MemoryStream(), download: false, _closed.Add);

    [Fact]
    public async Task DisposeAsync_DownloadReadToTheEnd_ReportsFinished()
    {
        var stream = Download([1, 2, 3]);
        var buffer = new byte[16];
        while (await stream.ReadAsync(buffer) > 0)
        {
        }

        await stream.DisposeAsync();

        _closed.Should().Equal(true);
    }

    [Fact]
    public async Task DisposeAsync_DownloadLeftBeforeTheEnd_ReportsUnfinished()
    {
        // A viewer reads one window and closes: the server may still be sending the rest.
        var stream = Download([1, 2, 3, 4]);
        await stream.ReadAsync(new byte[2]);

        await stream.DisposeAsync();

        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task DisposeAsync_UploadWritten_ReportsFinished()
    {
        var stream = Upload();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });

        await stream.DisposeAsync();

        _closed.Should().Equal(true);
    }

    [Fact]
    public async Task DisposeAsync_UploadAborted_ReportsUnfinished()
    {
        var stream = Upload();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
        stream.Abort();

        await stream.DisposeAsync();

        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task DisposeAsync_UploadWhoseWriteFailed_ReportsUnfinished()
    {
        var stream = Upload(new MemoryStream(new byte[2], writable: false));
        var write = async () => await stream.WriteAsync(new byte[] { 1 });
        await write.Should().ThrowAsync<NotSupportedException>();

        await stream.DisposeAsync();

        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task DisposeAsync_ServerReplyFailsOnClose_ReportsUnfinishedAndThrows()
    {
        var stream = Upload(new FailingCloseStream());
        await stream.WriteAsync(new byte[] { 1 });

        var dispose = async () => await stream.DisposeAsync();

        await dispose.Should().ThrowAsync<IOException>();
        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task Dispose_Twice_ReportsOnce()
    {
        var stream = Upload();

        await stream.DisposeAsync();
        stream.Dispose();

        _closed.Should().ContainSingle();
    }

    // ── An upload that ends with the server's word on it (TRAP-100) ──

    [Fact]
    public async Task DisposeAsync_ConfirmedUpload_AsksAfterTheDataIsClosedAndTellsTheOwnerLast()
    {
        var steps = new List<string>();
        var inner = new ProbeStream(() => steps.Add("data closed"));
        var stream = new TransferStream(inner, download: false, finished => steps.Add($"owner told: {finished}"), () =>
        {
            steps.Add("reply read");
            return Task.CompletedTask;
        });

        await stream.DisposeAsync();

        steps.Should().Equal("data closed", "reply read", "owner told: True");
    }

    [Fact]
    public async Task DisposeAsync_NoWordFromTheServer_ThrowsAndReportsUnfinished()
    {
        var stream = new TransferStream(new MemoryStream(), download: false, _closed.Add,
            () => Task.FromException(new IOException("did not confirm")));

        var dispose = async () => await stream.DisposeAsync();

        await dispose.Should().ThrowAsync<IOException>();
        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task DisposeAsync_ServerSaysNo_ThrowsButLeavesTheConnectionClean()
    {
        var stream = new TransferStream(new MemoryStream(), download: false, _closed.Add,
            () => Task.FromException(new FtpTransferRefusedException("552 Disk full")));

        var dispose = async () => await stream.DisposeAsync();

        await dispose.Should().ThrowAsync<IOException>();
        _closed.Should().Equal(true);
    }

    [Fact]
    public async Task DisposeAsync_AbortedUpload_DoesNotAskTheServer()
    {
        var asked = false;
        var stream = new TransferStream(new MemoryStream(), download: false, _closed.Add, () =>
        {
            asked = true;
            return Task.CompletedTask;
        });
        stream.Abort();

        await stream.DisposeAsync();

        asked.Should().BeFalse("nobody is waiting for a transfer that was given up on");
        _closed.Should().Equal(false);
    }

    [Fact]
    public async Task DisposeAsync_Twice_AsksTheServerOnce()
    {
        var asked = 0;
        var stream = new TransferStream(new MemoryStream(), download: false, _closed.Add, () =>
        {
            asked++;
            return Task.CompletedTask;
        });

        await stream.DisposeAsync();
        await stream.DisposeAsync();

        asked.Should().Be(1, "a second read would wait for a reply that is not coming");
    }

    [Fact]
    public void Dispose_ConfirmedUpload_CannotWaitSoReportsUnfinished()
    {
        var stream = new TransferStream(new MemoryStream(), download: false, _closed.Add, () => Task.CompletedTask);

        stream.Dispose();

        _closed.Should().Equal(false);
    }

    /// <summary>Says when it is closed.</summary>
    private sealed class ProbeStream(Action closing) : MemoryStream
    {
        private bool _closed;

        public override ValueTask DisposeAsync()
        {
            if (!_closed)
            {
                _closed = true;
                closing();
            }

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>An upload whose close reads a refusal from the server ("552 quota exceeded").</summary>
    private sealed class FailingCloseStream : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("552 Quota exceeded"));

        protected override void Dispose(bool disposing) => throw new IOException("552 Quota exceeded");
    }
}
