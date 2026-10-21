using FluentAssertions;
using FluentFTP.Exceptions;
using FluentFTP.Helpers;

namespace TCV.Network.Tests;

/// <summary>
/// How the app's FTP clients are set up: an empty login goes in as anonymous, and although FluentFTP
/// checks every path it sends, ordinary names still go out; a line break, which would put a second
/// command on the control connection, is refused — and so is a name the library would quietly send as
/// another one (white space at its end, TRAP-94).
/// </summary>
public class FtpClientSetupTests
{
    private static string Sent(string path, NetworkProtocol protocol = NetworkProtocol.Ftp)
    {
        using var client = FtpFileSystem.CreateClient(new ConnectionInfo { Protocol = protocol, Host = "example.org" });
        return SanitizerModule.SanitizePath(client, path);
    }

    [Theory]
    [InlineData("/music/Song | Live.mp3")]
    [InlineData("/docs/a;b.txt")]
    [InlineData("/sale/50% off.jpg")]
    [InlineData("/sale/report%20final.pdf")]
    [InlineData("/releases/v1..2.zip")]
    [InlineData("/he/‮name.txt")]
    public void SanitizePath_OrdinaryName_GoesOutUnchanged(string path)
    {
        Sent(path).Should().Be(path);
    }

    [Fact]
    public void SanitizePath_OrdinaryNameOverFtps_GoesOutUnchanged()
    {
        Sent("/music/Song | Live.mp3", NetworkProtocol.Ftps).Should().Be("/music/Song | Live.mp3");
    }

    [Fact]
    public void CreateClient_EmptyLogin_LogsInAsAnonymous()
    {
        using var client = FtpFileSystem.CreateClient(new ConnectionInfo { Host = "example.org" });

        client.Credentials.UserName.Should().Be("anonymous");
        client.Credentials.Password.Should().Be("anonymous@");
    }

    [Fact]
    public void CreateClient_Login_IsKeptAsGiven()
    {
        using var client = FtpFileSystem.CreateClient(new ConnectionInfo { Host = "example.org", Username = "bob", Password = "pw" });

        client.Credentials.UserName.Should().Be("bob");
        client.Credentials.Password.Should().Be("pw");
    }

    [Theory]
    [InlineData("/a\r\nDELE /b")]
    [InlineData("/a\nb")]
    public void SanitizePath_LineBreak_IsRefusedNotTruncated(string path)
    {
        FluentActions.Invoking(() => Sent(path)).Should().Throw<FtpSanitizeException>();
    }

    [Fact]
    public void SanitizePath_TrailingSpace_IsTrimmedByTheLibrary()
    {
        // Why ToSendablePath exists: «notes » would reach the server as «notes», another file.
        Sent("/docs/notes ").Should().Be("/docs/notes");
    }

    [Theory]
    [InlineData("/docs/notes ")]
    [InlineData("/docs/notes\t")]
    [InlineData("/docs/notes ")]
    [InlineData("/a\r\nDELE /b")]
    [InlineData("/a\nb")]
    [InlineData("/a\0b")]
    public void ToSendablePath_NameTheServerWouldGetAsAnother_IsRefused(string path)
    {
        FluentActions.Invoking(() => FtpFileSystem.ToSendablePath(path)).Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("/music/Song | Live.mp3")]
    [InlineData("/docs/ leading space.txt")]
    [InlineData("/folder /inside.txt")]
    public void ToSendablePath_NameTheLibrarySendsAsIs_IsKept(string path)
    {
        FtpFileSystem.ToSendablePath(path).Should().Be(path);
    }
}
