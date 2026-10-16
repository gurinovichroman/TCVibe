using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>Where <see cref="S3Endpoint"/> sends a request: virtual-hosted or path-style, port, key encoding.</summary>
public sealed class S3EndpointTests
{
    private static S3Endpoint Endpoint(string host, bool pathStyle = false, bool useHttp = false, int? port = null) =>
        S3Endpoint.From(new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3,
            Host = host,
            PathStyle = pathStyle,
            UseHttp = useHttp,
            Port = port ?? (useHttp ? 80 : 443),
        });

    private static string Url(S3Endpoint endpoint, string? bucket, string key, params (string Name, string Value)[] query) =>
        endpoint.BuildUri(bucket, key, query.Select(q => new KeyValuePair<string, string>(q.Name, q.Value)).ToList(), endpoint.Host)
            .AbsoluteUri;

    [Fact]
    public void BuildUri_PlainBucket_IsVirtualHosted()
    {
        Url(Endpoint("s3.eu-central-1.amazonaws.com"), "my-bucket", "dir/file.txt")
            .Should().Be("https://my-bucket.s3.eu-central-1.amazonaws.com/dir/file.txt");
    }

    [Fact]
    public void BuildUri_PathStyleAskedFor_PutsTheBucketInThePath()
    {
        var endpoint = Endpoint("s3.example.com", pathStyle: true);

        Url(endpoint, "my-bucket", "dir/file.txt").Should().Be("https://s3.example.com/my-bucket/dir/file.txt");
        Url(endpoint, "my-bucket", string.Empty).Should().Be("https://s3.example.com/my-bucket");
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]")]
    [InlineData("minio.localhost")]
    public void BuildUri_AddressOrLocalhost_UsesPathStyleOnItsOwn(string host)
    {
        var endpoint = Endpoint(host, useHttp: true, port: 9000);

        Url(endpoint, "bucket", "a.txt").Should().Be($"http://{host}:9000/bucket/a.txt");
    }

    [Fact]
    public void BuildUri_DottedBucketOverHttps_UsesPathStyleBecauseTheWildcardCertificateDoesNotCoverIt()
    {
        Url(Endpoint("s3.amazonaws.com"), "my.dotted.bucket", "a").Should().Be("https://s3.amazonaws.com/my.dotted.bucket/a");
        Url(Endpoint("s3.amazonaws.com", useHttp: true), "my.dotted.bucket", "a")
            .Should().Be("http://my.dotted.bucket.s3.amazonaws.com/a");
    }

    [Theory]
    [InlineData("Legacy_Bucket")]
    [InlineData("ab")]
    [InlineData("-bucket")]
    public void BuildUri_NameDnsCannotCarry_UsesPathStyle(string bucket)
    {
        Url(Endpoint("s3.amazonaws.com"), bucket, "a").Should().StartWith($"https://s3.amazonaws.com/{bucket}/");
    }

    [Fact]
    public void BuildUri_KeyWithSpacesPlusAndUnicode_EncodesEachSegmentOnce()
    {
        Url(Endpoint("s3.example.com"), "bucket", "my dir/a+b ü(1).txt")
            .Should().Be("https://bucket.s3.example.com/my%20dir/a%2Bb%20%C3%BC%281%29.txt");
    }

    [Fact]
    public void BuildUri_KeyWithDotSegments_KeepsThemVerbatim()
    {
        Url(Endpoint("s3.example.com"), "bucket", "a/../b").Should().Be("https://bucket.s3.example.com/a/../b");
    }

    [Fact]
    public void BuildUri_Query_SendsAnEmptyValueBareAndEncodesTheRest()
    {
        Url(Endpoint("s3.example.com"), "bucket", "big.bin", ("uploads", string.Empty))
            .Should().Be("https://bucket.s3.example.com/big.bin?uploads");
        Url(Endpoint("s3.example.com"), "bucket", string.Empty, ("list-type", "2"), ("prefix", "a b/"))
            .Should().Be("https://bucket.s3.example.com/?list-type=2&prefix=a%20b%2F");
    }

    [Fact]
    public void BuildUri_NoBucket_AddressesTheEndpointForListBuckets()
    {
        Url(Endpoint("s3.example.com"), null, string.Empty).Should().Be("https://s3.example.com/");
    }

    [Theory]
    [InlineData("http://localhost:9000/", "http", "localhost", 9000)]
    [InlineData("https://s3.example.com", "https", "s3.example.com", 443)]
    [InlineData("localhost:9000", "https", "localhost", 9000)]
    [InlineData("  S3.Example.com/ ", "https", "s3.example.com", 443)]
    public void From_HostPastedWithSchemeOrPort_IsTakenApart(string host, string scheme, string expectedHost, int port)
    {
        var endpoint = Endpoint(host);

        endpoint.Scheme.Should().Be(scheme);
        endpoint.Host.Should().Be(expectedHost);
        endpoint.Port.Should().Be(port);
    }

    [Fact]
    public void From_PortField_IsUsedWhenTheHostNamesNone()
    {
        Endpoint("minio.internal", useHttp: true, port: 9000).Port.Should().Be(9000);
    }

    [Fact]
    public void From_EmptyHost_Throws()
    {
        var act = () => Endpoint("  ");

        act.Should().Throw<ArgumentException>().WithMessage("*no endpoint host*");
    }

    [Theory]
    [InlineData("s3.us-east-1.amazonaws.com", "us-east-1", "eu-central-1", "s3.eu-central-1.amazonaws.com")]
    [InlineData("s3.amazonaws.com", "us-east-1", "ap-southeast-2", "s3.ap-southeast-2.amazonaws.com")]
    [InlineData("s3-eu-west-1.amazonaws.com", "eu-west-1", "us-west-2", "s3.us-west-2.amazonaws.com")]
    [InlineData("s3.dualstack.us-east-1.amazonaws.com", "us-east-1", "eu-west-3", "s3.dualstack.eu-west-3.amazonaws.com")]
    [InlineData("s3.us-east-1.wasabisys.com", "us-east-1", "eu-central-1", "s3.eu-central-1.wasabisys.com")]
    [InlineData("acct.r2.cloudflarestorage.com", "us-east-1", "auto", "acct.r2.cloudflarestorage.com")]
    [InlineData("localhost", "us-east-1", "eu-west-1", "localhost")]
    public void RetargetHost_RegionInTheHostName_FollowsTheNewRegion(string host, string from, string to, string expected)
    {
        S3Endpoint.RetargetHost(host, from, to).Should().Be(expected);
    }
}
