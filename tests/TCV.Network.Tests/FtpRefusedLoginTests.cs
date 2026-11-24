using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Network.Tests;

/// <summary>
/// FluentFTP leaves the socket open behind a failed connect: the session stayed connected and not logged
/// in, the server kept a slot for it, and the next command skipped the login and got a «530» (TRAP-101).
/// </summary>
public sealed class FtpRefusedLoginTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly FtpStubServer _server = new() { LoginReply = "530 Login incorrect" };

    public void Dispose() => _server.Dispose();

    [Fact]
    public async Task GetEntryAsync_LoginRefused_ClosesTheConnection()
    {
        using var fs = new FtpFileSystem(_server.Connection);

        var look = () => fs.GetEntryAsync(VfsPath.Root).WaitAsync(Patience);
        await look.Should().ThrowAsync<FluentFTP.Exceptions.FtpAuthenticationException>();

        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 1)).Should()
            .BeTrue("the server keeps a slot for an open connection, and it is not logged in");
    }

    [Fact]
    public async Task GetEntryAsync_AfterARefusedLogin_LogsInAgain()
    {
        using var fs = new FtpFileSystem(_server.Connection);
        var look = () => fs.GetEntryAsync(VfsPath.Root).WaitAsync(Patience);
        await look.Should().ThrowAsync<FluentFTP.Exceptions.FtpAuthenticationException>();

        _server.LoginReply = "230 Logged in";

        (await look()).Should().NotBeNull("the password was fixed on the server, and this session may log in now");
        _server.ConnectionCount.Should().Be(2);
    }

    [Fact]
    public async Task GetEntryAsync_LibraryReconnectsAndIsRefused_ClosesThatConnectionToo()
    {
        // FluentFTP reconnects by itself on a command that finds stale data on the socket.
        _server.LoginReply = "230 Logged in";
        using var fs = new FtpFileSystem(_server.Connection);
        await fs.GetEntryAsync(VfsPath.Root).WaitAsync(Patience);

        _server.LoginReply = "530 Login incorrect";
        await _server.SayOnEveryConnectionAsync("421 Timeout");
        await Task.Delay(200);

        var look = () => fs.GetEntryAsync(VfsPath.Root).WaitAsync(Patience);
        await look.Should().ThrowAsync<FluentFTP.Exceptions.FtpAuthenticationException>();

        _server.ConnectionCount.Should().Be(2);
        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == 2)).Should().BeTrue();
    }

    [Fact]
    public async Task OpenWriteAsync_LoginRefused_LeavesNoConnectionOpen()
    {
        using var fs = new FtpFileSystem(_server.Connection);

        var open = () => fs.OpenWriteAsync(new VfsPath("/a.bin"), overwrite: true).WaitAsync(Patience);
        await open.Should().ThrowAsync<FluentFTP.Exceptions.FtpAuthenticationException>();

        (await FtpStubServer.EventuallyAsync(() => _server.ClosedCount == _server.ConnectionCount)).Should().BeTrue();
    }
}
