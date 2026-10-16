using FluentAssertions;
using TCV.Core.Domain;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>Which URL <see cref="AzureBlobEndpoint"/> gives a path, and which container and blob a path names.</summary>
public sealed class AzureBlobEndpointTests
{
    private static ConnectionInfo Connection(
        string account = "myaccount", string host = "", int port = 443, bool useHttp = false,
        string? container = null, bool pathStyle = false) => new()
        {
            Protocol = NetworkProtocol.AzureBlob,
            Username = account,
            Host = host,
            Port = port,
            UseHttp = useHttp,
            Bucket = container,
            PathStyle = pathStyle,
        };

    [Fact]
    public void Blob_EmptyHost_UsesThePublicEndpointOfTheAccount()
    {
        var endpoint = new AzureBlobEndpoint(Connection(), null);

        endpoint.Blob("photos", "a.jpg", null).AbsoluteUri.Should().Be("https://myaccount.blob.core.windows.net/photos/a.jpg");
        endpoint.PathStyle.Should().BeFalse();
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    public void Blob_EmulatorHost_PutsTheAccountInThePath(string host)
    {
        var endpoint = new AzureBlobEndpoint(Connection("devstoreaccount1", host, 10000, useHttp: true), null);

        endpoint.Blob("photos", "a.jpg", null).AbsoluteUri.Should().Be($"http://{host}:10000/devstoreaccount1/photos/a.jpg");
        endpoint.Service("comp=list").AbsoluteUri.Should().Be($"http://{host}:10000/devstoreaccount1?comp=list");
    }

    [Fact]
    public void Blob_PathStyleFlagOnACustomHost_PutsTheAccountInThePath()
    {
        var endpoint = new AzureBlobEndpoint(Connection(host: "storage.contoso.internal", pathStyle: true), null);

        endpoint.Container("c", "restype=container").AbsoluteUri
            .Should().Be("https://storage.contoso.internal/myaccount/c?restype=container");
    }

    [Fact]
    public void Service_HostStyle_IsTheRootPath()
    {
        var endpoint = new AzureBlobEndpoint(Connection(host: "myaccount.blob.core.chinacloudapi.cn"), null);

        endpoint.Service("comp=list").AbsoluteUri.Should().Be("https://myaccount.blob.core.chinacloudapi.cn/?comp=list");
    }

    [Theory]
    [InlineData("https://myaccount.blob.core.windows.net/", 443, "https://myaccount.blob.core.windows.net/c/x")]
    [InlineData("127.0.0.1:10000", 0, "https://127.0.0.1:10000/myaccount/c/x")]
    [InlineData("[::1]:10000", 0, "https://[::1]:10000/myaccount/c/x")]
    public void Blob_HostTypedWithSchemeOrPort_IsReadForHostAndPort(string host, int port, string expected)
    {
        var endpoint = new AzureBlobEndpoint(Connection(host: host, port: port), null);

        endpoint.Blob("c", "x", null).AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData("a b.txt", "/c/a%20b.txt")]
    [InlineData("dir/résumé.txt", "/c/dir/r%C3%A9sum%C3%A9.txt")]
    [InlineData("1+1=2.txt", "/c/1%2B1%3D2.txt")]
    [InlineData("100%.txt", "/c/100%25.txt")]
    [InlineData("why?#.txt", "/c/why%3F%23.txt")]
    [InlineData("文件/データ.bin", "/c/%E6%96%87%E4%BB%B6/%E3%83%87%E3%83%BC%E3%82%BF.bin")]
    [InlineData("dir/", "/c/dir/")]
    [InlineData("a/../b", "/c/a/../b")]
    public void Blob_AwkwardName_IsSentAndSignedAsItself(string name, string expectedPath)
    {
        var endpoint = new AzureBlobEndpoint(Connection(), null);

        var uri = endpoint.Blob("c", name, null);

        uri.AbsolutePath.Should().Be(expectedPath, "the path is signed as encoded, so it must not be re-encoded or folded");
        uri.Query.Should().BeEmpty();
    }

    [Fact]
    public void Container_SystemContainer_KeepsItsDollar()
    {
        var endpoint = new AzureBlobEndpoint(Connection(), null);

        endpoint.Container("$web", "restype=container").AbsoluteUri
            .Should().Be("https://myaccount.blob.core.windows.net/$web?restype=container");
    }

    [Fact]
    public void Blob_SasToken_IsAppendedAfterTheRequestsOwnQuery()
    {
        var endpoint = new AzureBlobEndpoint(Connection(), "sv=2023-11-03&sp=rl&sig=abc%2B");

        endpoint.Container("c", "restype=container&comp=list").Query
            .Should().Be("?restype=container&comp=list&sv=2023-11-03&sp=rl&sig=abc%2B");
        endpoint.Blob("c", "x", null).Query.Should().Be("?sv=2023-11-03&sp=rl&sig=abc%2B");
        endpoint.Blob("c", "x", "a=1", includeSas: false).Query.Should().Be("?a=1");
    }

    [Theory]
    [InlineData("?sv=1&sig=x", "sv=1&sig=x")]
    [InlineData("sv=1&sig=x", "sv=1&sig=x")]
    [InlineData("  https://acct.blob.core.windows.net/c?sv=1&sig=x  ", "sv=1&sig=x")]
    public void NormalizeSasToken_AsTyped_IsTheBareQuery(string typed, string expected) =>
        AzureBlobEndpoint.NormalizeSasToken(typed).Should().Be(expected);

    [Fact]
    public void Locate_WithoutFixedContainer_FirstSegmentIsTheContainer()
    {
        var endpoint = new AzureBlobEndpoint(Connection(), null);

        endpoint.Locate(VfsPath.Root).IsServiceRoot.Should().BeTrue();
        endpoint.Locate(new VfsPath("/photos")).Should().Be(new AzureBlobLocation("photos", ""));
        endpoint.Locate(new VfsPath("/photos/2026/a.jpg")).Should().Be(new AzureBlobLocation("photos", "2026/a.jpg"));
        endpoint.Locate(new VfsPath("/photos/2026")).Prefix.Should().Be("2026/");
    }

    [Fact]
    public void Locate_WithFixedContainer_RootIsTheContainersTop()
    {
        var endpoint = new AzureBlobEndpoint(Connection(container: "/photos/"), null);

        endpoint.Locate(VfsPath.Root).Should().Be(new AzureBlobLocation("photos", ""));
        endpoint.Locate(VfsPath.Root).IsContainerRoot.Should().BeTrue();
        endpoint.Locate(new VfsPath("/2026/a.jpg")).Should().Be(new AzureBlobLocation("photos", "2026/a.jpg"));
    }

    [Fact]
    public void Constructor_NoAccountAndNoHost_SaysWhatIsMissing()
    {
        var act = () => new AzureBlobEndpoint(Connection(account: ""), null);

        act.Should().Throw<ArgumentException>().WithMessage("*account name*");
    }
}
