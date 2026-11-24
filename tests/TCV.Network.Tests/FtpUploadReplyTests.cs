using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Network.Tests;

/// <summary>
/// The end of an FTP upload is the server's reply, not the closing of the data connection: the server may
/// still be writing the file, and only that reply says whether it succeeded (TRAP-100).
/// </summary>
public sealed class FtpUploadReplyTests : IDisposable
{
    private static readonly VfsPath Target = new("/upload.bin");
    private static readonly byte[] Body = new byte[64 * 1024];
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly FtpStubServer _server = new();

    public void Dispose() => _server.Dispose();

    private async Task<Stream> OpenAndWriteAsync(FtpFileSystem fs, CancellationToken ct = default)
    {
        var output = await fs.OpenWriteAsync(Target, overwrite: true, ct);
        await output.WriteAsync(Body, ct);
        return output;
    }

    private Task ServerHasTheBytesAsync() =>
        FtpStubServer.EventuallyAsync(() => _server.ReceivedToEnd.ContainsKey(Target.Value));

    [Fact]
    public async Task DisposeAsync_ServerStillWriting_WaitsForItsReply()
    {
        var gate = new TaskCompletionSource();
        _server.FinalReplyGate = gate;
        using var fs = new FtpFileSystem(_server.Connection);
        var output = await OpenAndWriteAsync(fs);

        var closing = output.DisposeAsync().AsTask();
        await ServerHasTheBytesAsync();
        await Task.Delay(300);

        closing.IsCompleted.Should().BeFalse("the upload has not ended until the server says so");

        gate.SetResult();
        await closing.WaitAsync(Patience);
        _server.StoredBytes(Target.Value).Should().Be(Body.Length);
    }

    [Theory]
    [InlineData("552 Disk full")]
    [InlineData("451 Local error in processing")]
    [InlineData("426 Connection closed; transfer aborted")]
    public async Task DisposeAsync_ServerRefusesTheFile_IsAnError(string refusal)
    {
        _server.FinalReply = refusal;
        using var fs = new FtpFileSystem(_server.Connection);
        var output = await OpenAndWriteAsync(fs);

        var closing = async () => await output.DisposeAsync().AsTask().WaitAsync(Patience);

        (await closing.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain(refusal);
    }

    [Fact]
    public async Task DisposeAsync_PreliminaryReplyBeforeTheFinalOne_KeepsWaiting()
    {
        _server.ExtraPreliminaryReply = "110 Still working";
        _server.FinalReply = "552 Disk full";
        using var fs = new FtpFileSystem(_server.Connection);
        var output = await OpenAndWriteAsync(fs);

        var closing = async () => await output.DisposeAsync().AsTask().WaitAsync(Patience);

        await closing.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task DisposeAsync_Cancelled_StopsWaitingAtOnce()
    {
        _server.NeverRepliesToStor = true;
        using var fs = new FtpFileSystem(_server.Connection);
        using var cts = new CancellationTokenSource();
        var output = await OpenAndWriteAsync(fs, cts.Token);

        var closing = output.DisposeAsync().AsTask();
        await ServerHasTheBytesAsync();
        cts.Cancel();

        var waited = async () => await closing.WaitAsync(TimeSpan.FromSeconds(5));
        await waited.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DisposeAsync_ServerSilentPastTheWait_IsAnErrorNotSuccess()
    {
        _server.NeverRepliesToStor = true;
        using var fs = new FtpFileSystem(_server.Connection) { UploadReplyWait = TimeSpan.FromMilliseconds(500) };
        var output = await OpenAndWriteAsync(fs);

        var closing = async () => await output.DisposeAsync().AsTask().WaitAsync(Patience);

        (await closing.Should().ThrowAsync<IOException>())
            .Which.Message.Should().Contain("did not confirm");
    }

    [Fact]
    public async Task ConfirmUploadAsync_ReplyLaterThanTheLibrarysReadTimeout_StillCounts()
    {
        // FluentFTP's own wait (15 s) would call this a timeout, and it would close the connection.
        var gate = new TaskCompletionSource();
        _server.FinalReplyGate = gate;
        using var client = FtpFileSystem.CreateClient(_server.Connection);
        client.Config.ReadTimeout = 500;
        await client.Connect();
        var output = await client.OpenWrite(Target.Value);
        await output.WriteAsync(Body);
        await output.DisposeAsync();

        var confirming = FtpFinalReply.ConfirmUploadAsync(client, Target.Value, TimeSpan.FromSeconds(15), CancellationToken.None);
        await Task.Delay(1500);
        confirming.IsCompleted.Should().BeFalse();
        gate.SetResult();

        await confirming.WaitAsync(Patience);
        client.Config.ReadTimeout.Should().Be(500, "the longer wait is for this reply only");
        client.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task OpenWriteAsync_AfterAGoodUpload_ReusesTheConnection()
    {
        using var fs = new FtpFileSystem(_server.Connection);

        await (await OpenAndWriteAsync(fs)).DisposeAsync();
        await (await OpenAndWriteAsync(fs)).DisposeAsync();

        _server.ConnectionCount.Should().Be(1, "the reply was read, so nothing stale is left on the connection");
    }

    [Fact]
    public async Task OpenWriteAsync_AfterARefusedUpload_GoesOnOverTheSameConnection()
    {
        _server.FinalReply = "552 Disk full";
        using var fs = new FtpFileSystem(_server.Connection);
        var refused = async () => await (await OpenAndWriteAsync(fs)).DisposeAsync();
        await refused.Should().ThrowAsync<IOException>();

        _server.FinalReply = "226 Transfer complete";
        await (await OpenAndWriteAsync(fs)).DisposeAsync();

        _server.ConnectionCount.Should().Be(1, "the server has answered, so nothing is left on the connection");
        _server.StoredBytes(Target.Value).Should().Be(Body.Length);
    }

    [Fact]
    public async Task OpenWriteAsync_AfterAnUnansweredUpload_OpensANewConnection()
    {
        _server.NeverRepliesToStor = true;
        using var fs = new FtpFileSystem(_server.Connection) { UploadReplyWait = TimeSpan.FromMilliseconds(300) };
        var silent = async () => await (await OpenAndWriteAsync(fs)).DisposeAsync();
        await silent.Should().ThrowAsync<IOException>();

        _server.NeverRepliesToStor = false;
        await (await OpenAndWriteAsync(fs)).DisposeAsync();

        _server.ConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task DisposeAsync_Aborted_DoesNotWaitAndDropsTheConnection()
    {
        _server.NeverRepliesToStor = true;
        using var fs = new FtpFileSystem(_server.Connection);
        var output = await OpenAndWriteAsync(fs);

        ((TCV.Core.Interfaces.IAbortableWriteStream)output).Abort();
        await output.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 1)).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_Synchronous_DoesNotWaitAndDropsTheConnection()
    {
        // No reply can be awaited here, and a connection with one still coming must not carry the next transfer.
        _server.NeverRepliesToStor = true;
        using var fs = new FtpFileSystem(_server.Connection);
        var output = await OpenAndWriteAsync(fs);

        await Task.Run(output.Dispose).WaitAsync(TimeSpan.FromSeconds(5));

        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 1)).Should().BeTrue();
    }

    // ── A server that gives one connection only: the upload goes over the control connection ──

    private async Task<FtpFileSystem> OnTheOneConnectionAsync()
    {
        _server.MaxConnections = 1;
        var fs = new FtpFileSystem(_server.Connection);

        // The listing connection takes the only slot, so the pool's own connect is turned away.
        await fs.GetEntryAsync(VfsPath.Root);
        return fs;
    }

    [Fact]
    public async Task SharedConnection_WhileTheReplyIsAwaited_NothingElseIsSent()
    {
        var gate = new TaskCompletionSource();
        _server.FinalReplyGate = gate;
        using var fs = await OnTheOneConnectionAsync();
        var output = await OpenAndWriteAsync(fs);

        var closing = output.DisposeAsync().AsTask();
        await ServerHasTheBytesAsync();
        var listing = fs.GetEntryAsync(Target);

        // Quiet for an hour, as far as the keep-alive can tell: it would ping now if the gate were free.
        typeof(FtpFileSystem).GetField("_lastActivityTicks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(fs, DateTime.UtcNow.AddHours(-1).Ticks);
        var ping = fs.KeepAliveAsync(CancellationToken.None);
        await Task.Delay(300);

        listing.IsCompleted.Should().BeFalse("the gate is held until the server has answered");
        _server.Saw("MLST " + Target.Value).Should().BeFalse();
        _server.Saw("NOOP").Should().BeFalse("a NOOP inside a transfer breaks the order of the replies (TRAP-82)");

        gate.SetResult();
        await closing.WaitAsync(Patience);
        await ping.WaitAsync(Patience);
        var entry = await listing.WaitAsync(Patience);
        entry!.Size.Should().Be(Body.Length, "the listing after an upload shows the whole file");
    }

    [Fact]
    public async Task SharedConnection_ServerRefusesTheFile_ListingStillWorks()
    {
        _server.FinalReply = "552 Disk full";
        using var fs = await OnTheOneConnectionAsync();
        var output = await OpenAndWriteAsync(fs);

        var closing = async () => await output.DisposeAsync().AsTask().WaitAsync(Patience);
        await closing.Should().ThrowAsync<IOException>();

        // On this very connection: a server this strict may not give another one right away.
        var connections = _server.ConnectionCount;
        (await fs.GetEntryAsync(VfsPath.Root).WaitAsync(Patience)).Should().NotBeNull();
        _server.ConnectionCount.Should().Be(connections);
    }

    [Fact]
    public async Task SharedConnection_Aborted_NextCommandDoesNotReadTheOldReply()
    {
        var gate = new TaskCompletionSource();
        _server.FinalReply = "426 Transfer aborted";
        _server.FinalReplyGate = gate;
        using var fs = await OnTheOneConnectionAsync();
        var output = await OpenAndWriteAsync(fs);
        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 1)).Should().BeTrue("the pool's connection was turned away");
        var connections = _server.ConnectionCount;
        ((TCV.Core.Interfaces.IAbortableWriteStream)output).Abort();
        await output.DisposeAsync().AsTask().WaitAsync(Patience);

        // The reply to the aborted STOR comes later, and would be taken for the answer to the next command.
        gate.SetResult();
        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 2)).Should().BeTrue(
            "the connection with a reply still coming is dropped");

        (await fs.GetEntryAsync(VfsPath.Root).WaitAsync(Patience)).Should().NotBeNull();
        _server.ConnectionCount.Should().Be(connections + 1);
    }

    [Fact]
    public async Task CopyAsync_ServerRefusesTheCopy_IsAnError()
    {
        // The same reply through FluentFTP's own upload, whose answer is a return value that was dropped.
        _server.Seed("/source.bin", 1024);
        _server.FinalReply = "552 Disk full";
        using var fs = new FtpFileSystem(_server.Connection);

        var copy = () => fs.CopyAsync(new VfsPath("/source.bin"), Target, null, CancellationToken.None).WaitAsync(Patience);

        await copy.Should().ThrowAsync<IOException>();
    }
}
