using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;
using TCV.Network.Tests;

namespace TCV.App.Tests;

/// <summary>
/// F5 and F6 onto an FTP server: the operation ends when the server has answered for the file, and a
/// refusal in that answer fails it (TRAP-100). A real <see cref="FtpFileSystem"/> against a stub of the protocol.
/// </summary>
public sealed class FtpUploadEndToEndTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static readonly VfsPath Folder = new("/");
    private const string Target = "/report.bin";

    private readonly FtpStubServer _server = new();
    private readonly FtpFileSystem _ftp;
    private readonly InMemoryFileSystem _local = new("mem");
    private readonly IFileEntry _source;

    public FtpUploadEndToEndTests()
    {
        _ftp = new FtpFileSystem(_server.Connection);
        _local.AddFile("/src/report.bin", new byte[4096]);
        _source = _local.GetEntryAsync(new VfsPath("/src/report.bin")).GetAwaiter().GetResult()!;
    }

    public void Dispose()
    {
        _ftp.Dispose();
        _server.Dispose();
    }

    private CopyOperation Copy() =>
        new(_local, _ftp, [_source], Folder, new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);

    private Task<bool> ServerHasTheBytesAsync(string? path = null) =>
        FtpStubServer.EventuallyAsync(() => path is null
            ? !_server.ReceivedToEnd.IsEmpty
            : _server.ReceivedToEnd.ContainsKey(path));

    [Fact]
    public async Task Copy_ServerStillWriting_DoesNotEndUntilItAnswers()
    {
        var gate = new TaskCompletionSource();
        _server.FinalReplyGate = gate;
        var op = Copy();

        var running = op.ExecuteAsync(CancellationToken.None);
        (await ServerHasTheBytesAsync(Target)).Should().BeTrue();
        await Task.Delay(300);

        running.IsCompleted.Should().BeFalse("the server has not said it has the file");

        gate.SetResult();
        await running.WaitAsync(Patience);
        op.State.Should().Be(OperationState.Completed);
        (await _ftp.GetEntryAsync(new VfsPath(Target)))!.Size.Should().Be(4096, "a listing after the copy shows the whole file");
    }

    [Fact]
    public async Task Copy_ServerRefusesTheFile_FailsAndLeavesNoHalfFile()
    {
        _server.FinalReply = "552 Disk full";
        var op = Copy();

        var running = () => op.ExecuteAsync(CancellationToken.None).WaitAsync(Patience);

        (await running.Should().ThrowAsync<IOException>()).Which.Message.Should().Contain("552 Disk full");
        op.State.Should().Be(OperationState.Failed);
        _server.Saw("DELE " + Target).Should().BeTrue("what the server kept of the file is not the file");
    }

    [Fact]
    public async Task Move_ServerRefusesTheFile_SourceStays()
    {
        _server.FinalReply = "552 Disk full";
        var op = new MoveOperation(_local, _ftp, [_source], Folder, new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);

        var running = () => op.ExecuteAsync(CancellationToken.None).WaitAsync(Patience);

        await running.Should().ThrowAsync<IOException>();
        op.State.Should().Be(OperationState.Failed);
        _local.HasFile("/src/report.bin").Should().BeTrue("the server does not have it");
    }

    [Fact]
    public async Task Copy_OverwritingAFile_SwapsItInOnlyAfterTheServerAnswered()
    {
        _server.Seed(Target, 10);
        var gate = new TaskCompletionSource();
        _server.FinalReplyGate = gate;
        var op = Copy();

        var running = op.ExecuteAsync(CancellationToken.None);
        (await ServerHasTheBytesAsync()).Should().BeTrue();
        await Task.Delay(300);

        _server.Saw("RNFR").Should().BeFalse("a rename would catch the file while the server is still writing it");
        _server.StoredBytes(Target).Should().Be(10, "the old file stays until the new one is whole");

        gate.SetResult();
        await running.WaitAsync(Patience);
        op.State.Should().Be(OperationState.Completed);
        _server.StoredBytes(Target).Should().Be(4096);
    }

    [Fact]
    public async Task Copy_OverwritingAFile_ServerRefuses_OldFileStays()
    {
        _server.Seed(Target, 10);
        _server.FinalReply = "552 Disk full";
        var op = Copy();

        var running = () => op.ExecuteAsync(CancellationToken.None).WaitAsync(Patience);

        await running.Should().ThrowAsync<IOException>();
        _server.StoredBytes(Target).Should().Be(10);
        _server.Saw("RNFR").Should().BeFalse();
    }
}
