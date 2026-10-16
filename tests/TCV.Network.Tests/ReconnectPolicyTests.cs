using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using FluentAssertions;
using FluentFTP.Exceptions;
using Renci.SshNet.Common;
using Renci.SshNet.Messages.Transport;
using TCV.Network.Cloud;

namespace TCV.Network.Tests;

/// <summary>
/// When a dropped connection is tried again by itself and when it is not (6.82): the pauses grow and
/// end, a dropped line is worth another try, a refusal — a changed host key above all (TRAP-43) — is not.
/// </summary>
public class ReconnectPolicyTests
{
    [Fact]
    public void Default_PausesGrowAndAreCappedAtHalfAMinute()
    {
        var policy = ReconnectPolicy.Default;

        policy.MaxAttempts.Should().Be(8);
        Enumerable.Range(1, policy.MaxAttempts).Select(a => policy.DelayBefore(a)!.Value.TotalSeconds)
            .Should().Equal(1, 2, 5, 10, 20, 30, 30, 30);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void DelayBefore_OutsideTheTries_IsNull(int attempt) =>
        ReconnectPolicy.Default.DelayBefore(attempt).Should().BeNull();

    [Fact]
    public void Constructor_WithoutTries_IsRejected()
    {
        var act = () => new ReconnectPolicy([]);

        act.Should().Throw<ArgumentException>();
    }

    public static TheoryData<Exception> Drops() => new()
    {
        new SocketException((int)SocketError.ConnectionReset),
        new SocketException((int)SocketError.HostNotFound),
        new SshConnectionException("Client not connected.", DisconnectReason.ConnectionLost),
        new SshConnectionException("An established connection was aborted.", DisconnectReason.None),
        new SshOperationTimeoutException("Session operation has timed out"),
        new TimeoutException("Timed out trying to read data from the socket stream!"),
        new IOException("Unable to read data from the transport connection.", new SocketException((int)SocketError.ConnectionAborted)),
        new FtpCommandException("421", "Timeout - closing control connection."),
        new FtpMissingSocketException(new SocketException((int)SocketError.ConnectionRefused)),
        new HttpRequestException("No such host is known."),
        new HttpRequestException("Service Unavailable", null, HttpStatusCode.ServiceUnavailable),
        new S3Exception("The server is overloaded (SlowDown).", HttpStatusCode.ServiceUnavailable, "SlowDown"),
        new AzureStorageException("Server busy", HttpStatusCode.InternalServerError, "ServerBusy"),
        new CloudDriveException("Too many requests", HttpStatusCode.TooManyRequests, "rateLimit"),
        new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException()),
        new AggregateException(new SocketException((int)SocketError.TimedOut)),
    };

    [Theory]
    [MemberData(nameof(Drops))]
    public void IsWorthRetrying_ADroppedOrUnreachableConnection_IsRetried(Exception error) =>
        ReconnectPolicy.IsWorthRetrying(error).Should().BeTrue(error.GetType().Name);

    public static TheoryData<Exception?> Refusals() => new()
    {
        null,
        new HostKeyChangedException("prod.example:22", "SHA256:old", "SHA256:new"),
        new IOException("Handshake failed", new HostKeyChangedException("ftp.example:21", "SHA256:old", "SHA256:new")),
        new SshAuthenticationException("Permission denied (password)."),
        new SshConnectionException("Key exchange negotiation failed.", DisconnectReason.HostKeyNotVerifiable),
        new SshConnectionException("Too many authentication failures", DisconnectReason.NoMoreAuthenticationMethodsAvailable),
        new SftpPermissionDeniedException("Permission denied"),
        new SftpPathNotFoundException("No such file"),
        new FtpCommandException("530", "Login incorrect."),
        new FtpCommandException("550", "Failed to change directory."),
        new IOException("The handshake failed", new AuthenticationException("The remote certificate was rejected")),
        new UnauthorizedAccessException("Access denied"),
        new ReadOnlyConnectionException(),
        new DirectoryNotFoundException("/gone"),
        new S3Exception("Access denied (AccessDenied)", HttpStatusCode.Forbidden, "AccessDenied"),
        new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized),
        new SignInRequiredException("Google Drive"),
        new InvalidOperationException("Key-file authentication is selected but no key file is set."),
        new OperationCanceledException(),
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void IsWorthRetrying_ARefusalOrAnUnknownError_IsNotRetried(Exception? error) =>
        ReconnectPolicy.IsWorthRetrying(error).Should().BeFalse(error?.GetType().Name ?? "null");

    [Fact]
    public void IsWorthRetrying_ARefusalInsideADrop_WinsOverTheDrop()
    {
        // A changed key found while the line was also going down is still a changed key (TRAP-43).
        var error = new AggregateException(
            new SocketException((int)SocketError.ConnectionReset),
            new HostKeyChangedException("prod.example:22", "SHA256:old", "SHA256:new"));

        ReconnectPolicy.IsWorthRetrying(error).Should().BeFalse();
    }
}
