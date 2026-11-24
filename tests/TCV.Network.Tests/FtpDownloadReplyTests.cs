using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Network.Tests;

/// <summary>
/// A server that gives up on a download closes the data connection just as it does at the end of the
/// file. Only the reply on the control connection tells them apart: without reading it a short copy
/// passed for the whole file.
/// </summary>
public sealed class FtpDownloadReplyTests : IDisposable
{
    private static readonly VfsPath Source = new("/file.bin");
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private const long Size = 64 * 1024;

    private readonly FtpStubServer _server = new();

    public FtpDownloadReplyTests() => _server.Seed(Source.Value, Size);

    public void Dispose() => _server.Dispose();

    private static async Task<long> ReadAllAsync(Stream input)
    {
        using var copy = new MemoryStream();
        await input.CopyToAsync(copy);
        return copy.Length;
    }

    [Fact]
    public async Task ReadToTheEnd_ServerSaysComplete_GivesTheWholeFile()
    {
        using var fs = new FtpFileSystem(_server.Connection);

        await using var input = await fs.OpenReadAsync(Source);

        (await ReadAllAsync(input).WaitAsync(Patience)).Should().Be(Size);
    }

    [Theory]
    [InlineData("426 Connection closed; transfer aborted")]
    [InlineData("451 Local error in processing")]
    public async Task ReadToTheEnd_ServerGivesUpMidway_IsAnError(string reply)
    {
        _server.DownloadSendsOnly = Size / 2;
        _server.DownloadFinalReply = reply;
        using var fs = new FtpFileSystem(_server.Connection);

        await using var input = await fs.OpenReadAsync(Source);
        var reading = () => ReadAllAsync(input).WaitAsync(Patience);

        (await reading.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain(reply);
    }

    [Fact]
    public async Task ReadToTheEnd_Synchronously_ServerGivesUp_IsAnError()
    {
        _server.DownloadSendsOnly = Size / 2;
        _server.DownloadFinalReply = "426 Connection closed";
        using var fs = new FtpFileSystem(_server.Connection);

        await using var input = await fs.OpenReadAsync(Source);
        var buffer = new byte[8192];
        var reading = () => Task.Run(() =>
        {
            while (input.Read(buffer, 0, buffer.Length) > 0)
            {
            }
        }).WaitAsync(Patience);

        (await reading.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("426");
    }

    [Fact]
    public async Task ReadToTheEnd_ServerSaysNothing_IsNotTakenForTheWholeFile()
    {
        _server.DownloadReplyGate = new TaskCompletionSource();
        using var fs = new FtpFileSystem(_server.Connection) { DownloadReplyWait = TimeSpan.FromMilliseconds(500) };

        await using var input = await fs.OpenReadAsync(Source);
        var reading = () => ReadAllAsync(input).WaitAsync(Patience);

        (await reading.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("did not confirm");
    }

    [Fact]
    public async Task ReadToTheEnd_Cancelled_StopsWaiting()
    {
        _server.DownloadReplyGate = new TaskCompletionSource();
        using var fs = new FtpFileSystem(_server.Connection);
        using var cts = new CancellationTokenSource();

        await using var input = await fs.OpenReadAsync(Source);
        using var copy = new MemoryStream();
        var reading = input.CopyToAsync(copy, cts.Token);
        (await FtpStubServer.EventuallyAsync(() => copy.Length == Size)).Should().BeTrue();
        cts.Cancel();

        var waiting = () => reading.WaitAsync(Patience);
        await waiting.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task NextTransfer_AfterACompleteDownload_RidesTheSameConnection()
    {
        using var fs = new FtpFileSystem(_server.Connection);

        await using (var first = await fs.OpenReadAsync(Source))
        {
            await ReadAllAsync(first);
        }

        var connections = _server.ConnectionCount;
        await using (var second = await fs.OpenReadAsync(Source))
        {
            (await ReadAllAsync(second).WaitAsync(Patience)).Should().Be(Size);
        }

        _server.ConnectionCount.Should().Be(connections, "the reply has been read, nothing is left in the way");
    }

    [Fact]
    public async Task NextTransfer_AfterTheServerGaveUp_RidesTheSameConnection()
    {
        _server.DownloadFinalReply = "426 Connection closed";
        using var fs = new FtpFileSystem(_server.Connection);
        await using (var first = await fs.OpenReadAsync(Source))
        {
            var reading = () => ReadAllAsync(first).WaitAsync(Patience);
            await reading.Should().ThrowAsync<IOException>();
        }

        var connections = _server.ConnectionCount;
        _server.DownloadFinalReply = "226 Transfer complete";
        await using (var second = await fs.OpenReadAsync(Source))
        {
            (await ReadAllAsync(second).WaitAsync(Patience)).Should().Be(Size);
        }

        _server.ConnectionCount.Should().Be(connections, "the server has answered, so the connection is clean");
    }

    [Fact]
    public async Task Close_BeforeTheEnd_DoesNotWaitForAReply()
    {
        // The viewer stops reading when it has enough; nothing has been claimed about the file.
        _server.DownloadReplyGate = new TaskCompletionSource();
        using var fs = new FtpFileSystem(_server.Connection);

        var input = await fs.OpenReadAsync(Source);
        await input.ReadExactlyAsync(new byte[100]);

        await input.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    [Fact]
    public async Task SynchronousClose_AfterTheEnd_DoesNotReadTheReplyAgain()
    {
        using var fs = new FtpFileSystem(_server.Connection);

        var input = await fs.OpenReadAsync(Source);
        await ReadAllAsync(input);
        var started = System.Diagnostics.Stopwatch.StartNew();
        input.Dispose();

        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(12), "there is no second reply to wait 15 s for");
    }
}
