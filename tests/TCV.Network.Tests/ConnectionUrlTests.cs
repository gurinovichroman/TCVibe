using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// An address typed into the path bar or Quick connect: what it is read as, and which saved
/// connection lends it credentials.
/// </summary>
public sealed class ConnectionUrlTests
{
    [Fact]
    public void TryParse_SftpWithUserPortAndPath_ReadsEveryPart()
    {
        ConnectionUrl.TryParse("sftp://deploy@prod.example:2222/var/log", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.Sftp);
        info.Host.Should().Be("prod.example");
        info.Port.Should().Be(2222);
        info.Username.Should().Be("deploy");
        info.Password.Should().BeNull();
        path.Should().Be("/var/log");
    }

    [Fact]
    public void TryParse_FtpWithoutPort_TakesTheProtocolsDefault()
    {
        ConnectionUrl.TryParse("ftp://files.example/pub", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.Ftp);
        info.Port.Should().Be(21);
        path.Should().Be("/pub");
    }

    [Fact]
    public void TryParse_PasswordAndEscapedCharacters_AreDecoded()
    {
        ConnectionUrl.TryParse("ftps://me:p%40ss@h/My%20Docs", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.Ftps);
        info.Password.Should().Be("p@ss");
        path.Should().Be("/My Docs");
    }

    [Fact]
    public void TryParse_FtpsOnPort990_IsImplicitTls()
    {
        ConnectionUrl.TryParse("ftps://h:990/", out var info, out _).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.FtpsImplicit);
    }

    [Fact]
    public void TryParse_S3_TakesTheHostAsTheBucket()
    {
        ConnectionUrl.TryParse("s3://my-bucket/logs/2026", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.S3);
        info.Bucket.Should().Be("my-bucket");
        info.Host.Should().BeEmpty("the endpoint comes from a saved connection");
        path.Should().Be("/logs/2026");
    }

    [Theory]
    [InlineData("s3://my-bucket/logs", NetworkProtocol.S3)]
    [InlineData("https://my-bucket.s3.eu-west-1.amazonaws.com/a", NetworkProtocol.S3)]
    [InlineData("azblob://acct/builds", NetworkProtocol.AzureBlob)]
    [InlineData("https://acct.blob.core.windows.net/builds", NetworkProtocol.AzureBlob)]
    [InlineData("gdrive://me@example.com/docs", NetworkProtocol.GoogleDrive)]
    public void TryParse_StorageAddressWithNoPort_DoesNotHandOnFtpsPort(string address, NetworkProtocol protocol)
    {
        ConnectionUrl.TryParse(address, out var info, out _).Should().BeTrue();

        // ConnectionInfo starts at port 21; the editor copies a parsed port into its fields.
        info.Protocol.Should().Be(protocol);
        info.Port.Should().Be(443);
    }

    [Theory]
    [InlineData("gdrive://me@example.com/docs", NetworkProtocol.GoogleDrive)]
    [InlineData("dropbox://me@example.com/Apps", NetworkProtocol.Dropbox)]
    [InlineData("onedrive://me@example.com", NetworkProtocol.OneDrive)]
    public void TryParse_DriveAddressWithAnEmail_ReadsTheWholeEmailAsTheAccount(string address, NetworkProtocol protocol)
    {
        ConnectionUrl.TryParse(address, out var info, out _).Should().BeTrue();

        info.Protocol.Should().Be(protocol);
        info.Username.Should().Be("me@example.com", "a URI splits an e-mail at the @ into user and host");
    }

    [Fact]
    public void TryParse_DriveAddressWrittenByDescribeAddress_ComesBackAsTheSameAccount()
    {
        var drive = new ConnectionInfo { Protocol = NetworkProtocol.GoogleDrive, Username = "me@example.com" };

        ConnectionUrl.TryParse(drive.DescribeAddress("/docs"), out var parsed, out var path).Should().BeTrue();

        parsed.Username.Should().Be(drive.Username);
        path.Should().Be("/docs");
        ConnectionUrl.FindSaved([drive], parsed, ref path).Should().BeSameAs(drive);
    }

    [Theory]
    [InlineData("https://my-bucket.s3.eu-west-1.amazonaws.com/a/b", "my-bucket", "eu-west-1", "s3.eu-west-1.amazonaws.com")]
    [InlineData("https://s3.eu-west-1.amazonaws.com/my-bucket/a/b", "my-bucket", "eu-west-1", "s3.eu-west-1.amazonaws.com")]
    [InlineData("https://my-bucket.s3.amazonaws.com/a/b", "my-bucket", "us-east-1", "s3.amazonaws.com")]
    public void TryParse_AnAwsBucketAddress_IsS3(string address, string bucket, string region, string host)
    {
        ConnectionUrl.TryParse(address, out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.S3);
        info.Bucket.Should().Be(bucket);
        info.Region.Should().Be(region);
        info.Host.Should().Be(host);
        path.Should().Be("/a/b");
    }

    [Fact]
    public void TryParse_AnAzureBlobAddress_NamesTheAccountAndContainer()
    {
        ConnectionUrl.TryParse("https://acct.blob.core.windows.net/builds/x/y.zip", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.AzureBlob);
        info.Username.Should().Be("acct");
        info.Bucket.Should().Be("builds");
        path.Should().Be("/x/y.zip");
    }

    [Fact]
    public void TryParseS3Endpoint_AHostWithNoPattern_IsTheEndpoint()
    {
        ConnectionUrl.TryParseS3Endpoint("https://eu2.contabostorage.com", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.S3);
        info.Host.Should().Be("eu2.contabostorage.com");
        info.Port.Should().Be(443);
        info.UseHttp.Should().BeFalse();
        info.PathStyle.Should().BeFalse("no bucket is named, so nothing says how it is addressed");
        info.Bucket.Should().BeNull();
        path.Should().Be("/");
    }

    [Fact]
    public void TryParseS3Endpoint_HttpWithPortAndBucketInThePath_IsPathStyle()
    {
        ConnectionUrl.TryParseS3Endpoint("http://localhost:9000/logs/2026/jan", out var info, out var path).Should().BeTrue();

        info.Host.Should().Be("localhost");
        info.Port.Should().Be(9000);
        info.UseHttp.Should().BeTrue();
        info.Bucket.Should().Be("logs");
        info.PathStyle.Should().BeTrue();
        path.Should().Be("/2026/jan");
    }

    [Theory]
    [InlineData("s3://bucket")]
    [InlineData("sftp://host/path")]
    [InlineData(@"C:\work")]
    [InlineData("not an address")]
    public void TryParseS3Endpoint_NotAWebAddress_IsRefused(string text)
    {
        ConnectionUrl.TryParseS3Endpoint(text, out _, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\work")]
    [InlineData(@"\\server\share")]
    [InlineData("https://example.com/page")]
    [InlineData("not an address")]
    [InlineData("mailto:me@example.com")]
    public void TryParse_AnythingElse_IsNoConnection(string text)
    {
        ConnectionUrl.TryParse(text, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryParse_Smb_KeepsTheShareInThePath()
    {
        ConnectionUrl.TryParse("smb://nas/projects/web", out var info, out var path).Should().BeTrue();

        info.Protocol.Should().Be(NetworkProtocol.Smb);
        info.Host.Should().Be("nas");
        path.Should().Be("/projects/web");
    }

    // ── Which saved connection reaches the same place ────────────────────────

    private static readonly ConnectionInfo Prod = new()
    {
        Name = "prod", Protocol = NetworkProtocol.Sftp, Host = "prod.example", Port = 22, Username = "deploy", Password = "pw",
    };

    [Fact]
    public void FindSaved_SameHostPortAndUser_IsFound()
    {
        ConnectionUrl.TryParse("sftp://deploy@PROD.example/var", out var parsed, out var path);

        ConnectionUrl.FindSaved([Prod], parsed, ref path).Should().BeSameAs(Prod);
        path.Should().Be("/var");
    }

    [Fact]
    public void FindSaved_AnotherUser_IsNotTheSameConnection()
    {
        ConnectionUrl.TryParse("sftp://root@prod.example/", out var parsed, out var path);

        ConnectionUrl.FindSaved([Prod], parsed, ref path).Should().BeNull();
    }

    [Fact]
    public void FindSaved_NoUserInTheAddress_MatchesTheSavedOne()
    {
        ConnectionUrl.TryParse("sftp://prod.example/", out var parsed, out var path);

        ConnectionUrl.FindSaved([Prod], parsed, ref path).Should().BeSameAs(Prod);
    }

    [Fact]
    public void FindSaved_ExplicitAndImplicitFtps_AreOneFamily()
    {
        var saved = new ConnectionInfo { Protocol = NetworkProtocol.FtpsImplicit, Host = "h", Port = 990 };
        ConnectionUrl.TryParse("ftps://h:990/", out var parsed, out var path);

        ConnectionUrl.FindSaved([saved], parsed, ref path).Should().BeSameAs(saved);
    }

    [Fact]
    public void FindSaved_S3BucketOfAConnectionLimitedToIt_KeepsThePath()
    {
        var saved = new ConnectionInfo { Protocol = NetworkProtocol.S3, Host = "s3.amazonaws.com", Bucket = "logs" };
        ConnectionUrl.TryParse("s3://logs/2026/10", out var parsed, out var path);

        ConnectionUrl.FindSaved([saved], parsed, ref path).Should().BeSameAs(saved);
        path.Should().Be("/2026/10");
    }

    [Fact]
    public void FindSaved_S3BucketOfAnAccountWideConnection_PutsTheBucketInThePath()
    {
        var saved = new ConnectionInfo { Protocol = NetworkProtocol.S3, Host = "s3.amazonaws.com" };
        ConnectionUrl.TryParse("s3://logs/2026", out var parsed, out var path);

        ConnectionUrl.FindSaved([saved], parsed, ref path).Should().BeSameAs(saved);
        path.Should().Be("/logs/2026", "without a fixed bucket the first segment is the bucket");
    }

    [Fact]
    public void FindSaved_AzureContainerOfAnotherAccount_IsNotFound()
    {
        var saved = new ConnectionInfo { Protocol = NetworkProtocol.AzureBlob, Username = "other" };
        ConnectionUrl.TryParse("azblob://acct/builds", out var parsed, out var path);

        ConnectionUrl.FindSaved([saved], parsed, ref path).Should().BeNull();
    }

    // ── How an address is shown ──────────────────────────────────────────────

    [Theory]
    [InlineData(NetworkProtocol.Sftp, "h", 22, "me", null, "/var/log", "sftp://me@h/var/log")]
    [InlineData(NetworkProtocol.Sftp, "h", 2222, "me", null, "/", "sftp://me@h:2222/")]
    [InlineData(NetworkProtocol.S3, "s3.amazonaws.com", 443, "AKIAEXAMPLE", "logs", "/2026", "s3://logs/2026")]
    [InlineData(NetworkProtocol.S3, "s3.amazonaws.com", 443, "AKIAEXAMPLE", null, "/logs/2026", "s3://logs/2026")]
    [InlineData(NetworkProtocol.S3, "s3.amazonaws.com", 443, "AKIAEXAMPLE", null, "/", "s3://")]
    [InlineData(NetworkProtocol.AzureBlob, "", 443, "acct", "builds", "/x", "azblob://acct/builds/x")]
    [InlineData(NetworkProtocol.Dropbox, "", 443, "me@example.com", null, "/Apps", "dropbox://me@example.com/Apps")]
    [InlineData(NetworkProtocol.Smb, "nas", 445, "", null, "/projects/web", @"\\nas\projects\web")]
    public void DescribeAddress_NamesThePlaceWithoutSecrets(
        NetworkProtocol protocol, string host, int port, string user, string? bucket, string path, string expected)
    {
        var info = new ConnectionInfo
        {
            Protocol = protocol, Host = host, Port = port, Username = user, Bucket = bucket, Password = "secret",
        };

        info.DescribeAddress(path).Should().Be(expected);
    }
}
