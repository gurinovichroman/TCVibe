using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>
/// <see cref="S3Signer"/> against the worked examples of the AWS documentation: "Signature
/// Calculations for the Authorization Header: Transferring Payload in a Single Chunk" (GET Object,
/// PUT Object, GET Bucket Lifecycle, Get Bucket) and "Authenticating Requests: Using Query
/// Parameters" (the presigned URL). Every expected value below is copied from those pages.
/// </summary>
public sealed class S3SignerTests
{
    private const string Region = "us-east-1";
    private const string EmptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static readonly S3Credentials ExampleKeys = new("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY");
    private static readonly DateTimeOffset ExampleTime = new(2013, 5, 24, 0, 0, 0, TimeSpan.Zero);

    // ── The documented examples ──────────────────────────────────────────────

    [Fact]
    public void CanonicalRequest_GetObjectExample_MatchesTheDocumentedTextAndStringToSign()
    {
        var uri = new Uri("https://examplebucket.s3.amazonaws.com/test.txt");
        var headers = new KeyValuePair<string, string>[]
        {
            new("Host", "examplebucket.s3.amazonaws.com"),
            new("Range", "bytes=0-9 "),
            new("x-amz-content-sha256", EmptyHash),
            new("x-amz-date", "20130524T000000Z "),
        };

        var canonical = S3Signer.CanonicalRequest("GET", S3Signer.CanonicalPath(uri), string.Empty, headers, EmptyHash, out var signed);

        canonical.Should().Be(
            "GET\n" +
            "/test.txt\n" +
            "\n" +
            "host:examplebucket.s3.amazonaws.com\n" +
            "range:bytes=0-9\n" +
            "x-amz-content-sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\n" +
            "x-amz-date:20130524T000000Z\n" +
            "\n" +
            "host;range;x-amz-content-sha256;x-amz-date\n" +
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
        signed.Should().Be("host;range;x-amz-content-sha256;x-amz-date");
        S3Signer.StringToSign(ExampleTime, S3Signer.Scope(ExampleTime, Region), canonical).Should().Be(
            "AWS4-HMAC-SHA256\n" +
            "20130524T000000Z\n" +
            "20130524/us-east-1/s3/aws4_request\n" +
            "7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972");
    }

    [Fact]
    public void Authorization_GetObjectExample_GivesTheDocumentedSignature()
    {
        var headers = new KeyValuePair<string, string>[]
        {
            new("host", "examplebucket.s3.amazonaws.com"),
            new("range", "bytes=0-9"),
            new("x-amz-content-sha256", EmptyHash),
            new("x-amz-date", "20130524T000000Z"),
        };

        var authorization = S3Signer.Authorization(
            "GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), headers, EmptyHash, ExampleKeys, Region, ExampleTime);

        authorization.Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request," +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date," +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41");
    }

    [Fact]
    public void Authorization_PutObjectExample_EncodesTheDollarAndGivesTheDocumentedSignature()
    {
        var payloadHash = S3Signer.Sha256Hex(Encoding.UTF8.GetBytes("Welcome to Amazon S3."));
        var headers = new KeyValuePair<string, string>[]
        {
            new("Host", "examplebucket.s3.amazonaws.com"),
            new("Date", "Fri, 24 May 2013 00:00:00 GMT"),
            new("x-amz-date", "20130524T000000Z"),
            new("x-amz-storage-class", "REDUCED_REDUNDANCY"),
            new("x-amz-content-sha256", payloadHash),
        };
        var uri = new Uri("https://examplebucket.s3.amazonaws.com/test$file.text");

        var authorization = S3Signer.Authorization("PUT", uri, headers, payloadHash, ExampleKeys, Region, ExampleTime);

        payloadHash.Should().Be("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072");
        S3Signer.CanonicalPath(uri).Should().Be("/test%24file.text");
        authorization.Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request," +
            "SignedHeaders=date;host;x-amz-content-sha256;x-amz-date;x-amz-storage-class," +
            "Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd");
    }

    [Fact]
    public void Sign_GetBucketLifecycleExample_SetsTheDocumentedAuthorizationHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?lifecycle");

        S3Signer.Sign(request, ExampleKeys, Region, ExampleTime, EmptyHash);

        Header(request, "x-amz-date").Should().Be("20130524T000000Z");
        Header(request, "x-amz-content-sha256").Should().Be(EmptyHash);
        Header(request, "Authorization").Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request," +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date," +
            "Signature=fea454ca298b7da1c68078a5d1bdbfbbe0d65c699e0f91ac7a200a0136783543");
    }

    [Fact]
    public void Sign_ListObjectsExample_SortsTheQueryAndSetsTheDocumentedAuthorizationHeader()
    {
        // Parameters out of order on purpose: the canonical query sorts them.
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/?prefix=J&max-keys=2");

        S3Signer.Sign(request, ExampleKeys, Region, ExampleTime, EmptyHash);

        Header(request, "Authorization").Should().Be(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request," +
            "SignedHeaders=host;x-amz-content-sha256;x-amz-date," +
            "Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7");
    }

    [Fact]
    public void Presign_DocumentationExample_GivesTheDocumentedUrl()
    {
        var url = S3Signer.Presign(
            "GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), ExampleKeys, Region, ExampleTime,
            TimeSpan.FromSeconds(86400));

        url.AbsoluteUri.Should().Be(
            "https://examplebucket.s3.amazonaws.com/test.txt" +
            "?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20130524T000000Z" +
            "&X-Amz-Expires=86400" +
            "&X-Amz-SignedHeaders=host" +
            "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404");
    }

    // ── Beyond the examples ──────────────────────────────────────────────────

    [Fact]
    public void Presign_WithSessionToken_SignsTheTokenInsideTheQuery()
    {
        var keys = ExampleKeys with { SessionToken = "token/with+chars" };

        var url = S3Signer.Presign(
            "GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), keys, Region, ExampleTime, TimeSpan.FromHours(1))
            .AbsoluteUri;

        url.Should().Contain("&X-Amz-Security-Token=token%2Fwith%2Bchars&X-Amz-SignedHeaders=host&X-Amz-Signature=");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(604801)]
    public void Presign_ExpiryOutsideOneSecondToSevenDays_Throws(int seconds)
    {
        var act = () => S3Signer.Presign(
            "GET", new Uri("https://examplebucket.s3.amazonaws.com/test.txt"), ExampleKeys, Region, ExampleTime,
            TimeSpan.FromSeconds(seconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Sign_SessionTokenAndContentMd5_AreSentAndSigned()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://bucket.s3.amazonaws.com/?delete")
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentMD5 = [9, 9] } },
        };

        S3Signer.Sign(request, ExampleKeys with { SessionToken = "TOKEN" }, Region, ExampleTime, EmptyHash);

        Header(request, "x-amz-security-token").Should().Be("TOKEN");
        Header(request, "Authorization").Should().Contain("SignedHeaders=content-md5;host;x-amz-content-sha256;x-amz-date;x-amz-security-token,");
    }

    [Fact]
    public void Sign_SignedTwice_ReplacesRatherThanDuplicatesItsHeaders()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://bucket.s3.amazonaws.com/key");

        S3Signer.Sign(request, ExampleKeys, Region, ExampleTime, EmptyHash);
        S3Signer.Sign(request, ExampleKeys, "eu-west-1", ExampleTime, EmptyHash);

        request.Headers.GetValues("x-amz-date").Should().ContainSingle();
        request.Headers.GetValues("Authorization").Should().ContainSingle().Which.Should().Contain("/eu-west-1/s3/");
    }

    [Theory]
    [InlineData("a b", "a%20b")]
    [InlineData("a+b", "a%2Bb")]
    [InlineData("ü", "%C3%BC")]
    [InlineData("AZaz09-_.~", "AZaz09-_.~")]
    [InlineData("!*'()", "%21%2A%27%28%29")]
    [InlineData("a/b=c&d", "a%2Fb%3Dc%26d")]
    public void UriEncode_Value_EncodesAllButTheUnreservedCharacters(string value, string expected)
    {
        S3Signer.UriEncode(value).Should().Be(expected);
    }

    [Fact]
    public void CanonicalPath_EscapedEitherWay_SignsWhatTheServerDecodes()
    {
        // .NET leaves "(" alone and a plus as it is; the canonical form encodes both, exactly once.
        S3Signer.CanonicalPath(new Uri("https://h/dir/a b(1)+ü.txt")).Should().Be("/dir/a%20b%281%29%2B%C3%BC.txt");
        S3Signer.CanonicalPath(S3Signer.VerbatimUri("https://h/dir/a%20b%281%29%2B%C3%BC.txt")).Should().Be("/dir/a%20b%281%29%2B%C3%BC.txt");
    }

    [Theory]
    [InlineData("http://localhost:9000/b", "localhost:9000")]
    [InlineData("https://s3.amazonaws.com:443/b", "s3.amazonaws.com")]
    [InlineData("http://10.0.0.5/b", "10.0.0.5")]
    [InlineData("http://[::1]:9000/b", "[::1]:9000")]
    public void HostHeader_Port_AppearsOnlyWhenNotTheDefault(string uri, string expected)
    {
        S3Signer.HostHeader(new Uri(uri)).Should().Be(expected);
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;
}
