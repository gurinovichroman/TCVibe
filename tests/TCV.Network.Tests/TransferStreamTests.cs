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

    /// <summary>An upload whose close reads a refusal from the server ("552 quota exceeded").</summary>
    private sealed class FailingCloseStream : MemoryStream
    {
        public override ValueTask DisposeAsync() => ValueTask.FromException(new IOException("552 Quota exceeded"));

        protected override void Dispose(bool disposing) => throw new IOException("552 Quota exceeded");
    }
}
