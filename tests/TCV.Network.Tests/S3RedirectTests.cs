using System.Net;
using System.Text;
using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Network.Tests;

/// <summary>
/// A 307 (TemporaryRedirect): which addresses are followed and how the request is sent there. The answers
/// are written from the AWS documentation; no live store has seen this.
/// </summary>
public sealed class S3RedirectTests
{
    private const string Bucket = "bucket";
    private const string Global = "s3.amazonaws.com";
    private const string Regional = "s3.eu-west-1.amazonaws.com";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static S3Endpoint Endpoint(string host, bool useHttp = false, int? port = null) =>
        S3Endpoint.From(new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3,
            Host = host,
            UseHttp = useHttp,
            Port = port ?? (useHttp ? 80 : 443),
        });

    private static S3FileSystem Create(FakeS3 fake, bool withKeys = true) =>
        new(new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3,
            Host = fake.Endpoint,
            Port = 443,
            Bucket = Bucket,
            Region = "us-east-1",
            Username = withKeys ? "AKIDEXAMPLE" : string.Empty,
            Password = withKeys ? "secret" : null,
        }, new HttpClient(fake))
        {
            MultipartThreshold = 10,
            MinPartSize = 4,
            PartRetries = TestRetries.NoPauses,
            Clock = () => Now,
        };

    /// <summary>The store behind the global endpoint: the bucket answers at the regional one.</summary>
    private static FakeS3 Store(params (string Key, string Content)[] objects)
    {
        var fake = new FakeS3(Global);
        fake.OtherEndpoints.Add(Regional);
        fake.Buckets[Bucket] = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, content) in objects)
        {
            fake.Put(Bucket, key, content);
        }

        return fake;
    }

    /// <summary>The answer the AWS documentation gives for TemporaryRedirect.</summary>
    private static HttpResponseMessage TemporaryRedirect(string? location, string? endpoint = null, string? regionHeader = null)
    {
        var body = "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>TemporaryRedirect</Code>"
                   + "<Message>Please re-send this request to the specified temporary endpoint. Continue to use the original request endpoint for future requests.</Message>"
                   + (endpoint is null ? string.Empty : $"<Endpoint>{endpoint}</Endpoint>")
                   + "<Bucket>bucket</Bucket><RequestId>TEST</RequestId></Error>";
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml"),
        };
        if (location is not null)
        {
            response.Headers.TryAddWithoutValidation("Location", location);
        }

        if (regionHeader is not null)
        {
            response.Headers.TryAddWithoutValidation("x-amz-bucket-region", regionHeader);
        }

        return response;
    }

    private static bool AtGlobal(FakeS3.Seen r) => r.Uri.Host == $"{Bucket}.{Global}";

    // ── Which addresses ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("s3.amazonaws.com", "amazonaws.com")]
    [InlineData("s3.eu-west-1.amazonaws.com", "amazonaws.com")]
    [InlineData("s3.cn-north-1.amazonaws.com.cn", "amazonaws.com.cn")]
    [InlineData("abc123.r2.cloudflarestorage.com", "cloudflarestorage.com")]
    [InlineData("storage.example.co.uk", "example.co.uk")]
    [InlineData("example.com", "example.com")]
    [InlineData("s3.example.io", "example.io")]
    [InlineData("127.0.0.1", null)]
    [InlineData("[::1]", null)]
    [InlineData("localhost", null)]
    [InlineData("minio", null)]
    public void BaseZone_AHost_IsItsRegisteredDomain(string host, string? expected)
    {
        S3Endpoint.BaseZone(host).Should().Be(expected);
    }

    [Theory]
    [InlineData("s3.amazonaws.com", "s3.eu-west-1.amazonaws.com", true)]
    [InlineData("s3.amazonaws.com", "s3-eu-west-1.amazonaws.com", true)]
    [InlineData("s3.amazonaws.com", "amazonaws.com", true)]
    [InlineData("s3.cn-north-1.amazonaws.com.cn", "s3.cn-northwest-1.amazonaws.com.cn", true)]
    [InlineData("s3.amazonaws.com", "s3.amazonaws.com.evil.net", false)]
    [InlineData("s3.amazonaws.com", "evilamazonaws.com", false)]
    [InlineData("s3.amazonaws.com", "s3.cn-north-1.amazonaws.com.cn", false)]
    [InlineData("s3.amazonaws.com", "storage.example.com", false)]
    [InlineData("storage.example.co.uk", "storage.other.co.uk", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", false)]
    [InlineData("minio", "minio", true)]
    [InlineData("minio", "other", false)]
    [InlineData("localhost", "evil.localhost", false)]
    public void IsSameZone_TwoHosts_AnswersWithoutTrustingAPrefixOrASuffix(string host, string other, bool expected)
    {
        S3Endpoint.IsSameZone(host, other).Should().Be(expected);
    }

    [Theory]
    [InlineData("s3.eu-west-1.amazonaws.com", "eu-west-1")]
    [InlineData("s3-eu-west-1.amazonaws.com", "eu-west-1")]
    [InlineData("s3.dualstack.ap-southeast-2.amazonaws.com", "ap-southeast-2")]
    [InlineData("s3.cn-north-1.amazonaws.com.cn", "cn-north-1")]
    [InlineData("s3.us-west-004.backblazeb2.com", "us-west-004")]
    [InlineData("s3.amazonaws.com", null)]
    [InlineData("s3-accelerate.amazonaws.com", null)]
    [InlineData("s3-fips.amazonaws.com", null)]
    [InlineData("storage.googleapis.com", null)]
    [InlineData("nyc3.digitaloceanspaces.com", null)]
    [InlineData("minio", null)]
    public void RegionInHost_AHost_IsTheRegionItStates(string host, string? expected)
    {
        S3Endpoint.RegionInHost(host).Should().Be(expected);
    }

    [Fact]
    public void ReadRedirect_VirtualHostedLocation_TakesTheEndpointHostAndItsRegion()
    {
        var redirect = Endpoint(Global).ReadRedirect(Bucket, new Uri("https://bucket.s3-eu-west-1.amazonaws.com/some/key?x=1"), null);

        redirect.Should().Be(new S3Redirect("s3-eu-west-1.amazonaws.com", "eu-west-1", Allowed: true));
    }

    [Fact]
    public void ReadRedirect_NoLocation_TakesTheEndpointOfTheErrorBody()
    {
        var redirect = Endpoint(Global).ReadRedirect(Bucket, null, "bucket.s3-eu-west-1.amazonaws.com");

        redirect.Should().Be(new S3Redirect("s3-eu-west-1.amazonaws.com", "eu-west-1", Allowed: true));
    }

    [Fact]
    public void ReadRedirect_RelativeLocation_IsNoAddress()
    {
        Endpoint(Global).ReadRedirect(Bucket, new Uri("/key", UriKind.Relative), null).Should().BeNull();
    }

    [Fact]
    public void ReadRedirect_NoAddressAtAll_IsNull()
    {
        Endpoint(Global).ReadRedirect(Bucket, null, "  ").Should().BeNull();
    }

    [Theory]
    [InlineData("https://bucket.s3.eu-west-1.evil.net/key")]
    [InlineData("https://bucket.s3.amazonaws.com.evil.net/key")]
    [InlineData("http://bucket.s3.eu-west-1.amazonaws.com/key")]
    [InlineData("https://bucket.s3.eu-west-1.amazonaws.com:8443/key")]
    public void ReadRedirect_AnotherProviderSchemeOrPort_IsNotAllowed(string location)
    {
        Endpoint(Global).ReadRedirect(Bucket, new Uri(location), null)!.Allowed.Should().BeFalse();
    }

    [Fact]
    public void ReadRedirect_PlainHttpEndpointOnItsOwnPort_StaysAllowed()
    {
        Endpoint("minio.local", useHttp: true, port: 9000)
            .ReadRedirect(Bucket, new Uri("http://minio.local:9000/bucket/key"), null)!.Allowed.Should().BeTrue();
    }

    // ── How the request goes there ───────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_TemporaryRedirect_SendsTheWholeBodyAgainSignedForTheNewHostAndRegion()
    {
        var fake = Store();
        fake.On(r => AtGlobal(r) && r.Method == HttpMethod.Put
            ? TemporaryRedirect($"https://{Bucket}.{Regional}/a.txt", $"{Bucket}.{Regional}")
            : null);
        using var fs = Create(fake);

        await using (var output = await fs.OpenWriteAsync(new VfsPath("/a.txt"), overwrite: true))
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes("hello"));
        }

        fake.Text(Bucket, "a.txt").Should().Be("hello");
        fake.Requests.Select(r => (r.Uri.Host, r.SignedRegion, Encoding.UTF8.GetString(r.Body))).Should().Equal(
            ($"{Bucket}.{Global}", "us-east-1", "hello"),
            ($"{Bucket}.{Regional}", "eu-west-1", "hello"));

        var second = fake.Requests[1];
        second.Header("Authorization").Should().NotBe(fake.Requests[0].Header("Authorization"));
        second.Header("Authorization").Should().Be(
            S3Signer.Authorization(
                "PUT", second.Uri,
                second.Headers.Where(h => h.Key.StartsWith("x-amz-", StringComparison.OrdinalIgnoreCase))
                    .Append(new KeyValuePair<string, string>("host", second.Uri.Host)),
                second.Header("x-amz-content-sha256")!,
                new S3Credentials("AKIDEXAMPLE", "secret"), "eu-west-1", Now),
            "the signature is made for this host and this region, not carried along");
    }

    [Fact]
    public async Task ListAsync_RegionInAHeader_WinsOverTheHostName()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r => AtGlobal(r)
            ? TemporaryRedirect($"https://{Bucket}.{Regional}/", null, regionHeader: "eu-west-3")
            : null);
        using var fs = Create(fake);

        await fs.ListAsync(VfsPath.Root);

        fake.Requests[1].SignedRegion.Should().Be("eu-west-3");
    }

    [Fact]
    public async Task ListAsync_HostStatesNoRegion_KeepsTheRegionItHad()
    {
        var fake = new FakeS3("storage.example.com");
        fake.OtherEndpoints.Add("eu.example.com");
        fake.Buckets[Bucket] = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        fake.On(r => r.Uri.Host == $"{Bucket}.storage.example.com" ? TemporaryRedirect($"https://{Bucket}.eu.example.com/") : null);
        using var fs = Create(fake);

        await fs.ListAsync(VfsPath.Root);

        fake.Requests.Select(r => (r.Uri.Host, r.SignedRegion)).Should().Equal(
            ($"{Bucket}.storage.example.com", "us-east-1"),
            ($"{Bucket}.eu.example.com", "us-east-1"));
    }

    [Fact]
    public async Task ListAsync_AfterARedirect_TheNextCallStartsAtTheOriginalEndpointAgain()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r => AtGlobal(r) ? TemporaryRedirect($"https://{Bucket}.{Regional}/") : null);
        using var fs = Create(fake);

        await fs.ListAsync(VfsPath.Root);
        await fs.ListAsync(VfsPath.Root);

        fake.Requests.Select(r => r.Uri.Host).Should().Equal(
            [$"{Bucket}.{Global}", $"{Bucket}.{Regional}", $"{Bucket}.{Global}", $"{Bucket}.{Regional}"],
            "the provider asks to keep using the original endpoint");
    }

    [Fact]
    public async Task OpenReadAsync_TemporaryRedirect_ReadsFromTheNewAddress()
    {
        var fake = Store(("a.txt", "hello"));
        fake.On(r => AtGlobal(r) ? TemporaryRedirect($"https://{Bucket}.{Regional}/a.txt") : null);
        using var fs = Create(fake);

        await using var stream = await fs.OpenReadAsync(new VfsPath("/a.txt"));

        new StreamReader(stream).ReadToEnd().Should().Be("hello");
    }

    [Fact]
    public async Task GetEntryAsync_HeadRedirectedWithoutABody_FollowsTheLocationHeader()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r =>
        {
            if (!AtGlobal(r))
            {
                return null;
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Content = new ByteArrayContent([]) };
            redirect.Headers.TryAddWithoutValidation("Location", $"https://{Bucket}.{Regional}/a.txt");
            return redirect;
        });
        using var fs = Create(fake);

        (await fs.GetEntryAsync(new VfsPath("/a.txt"))).Should().NotBeNull();
    }

    [Fact]
    public async Task CommitAsync_PartRedirected_GoesThereWithItsOwnBytes()
    {
        var fake = Store();
        fake.On(r => AtGlobal(r) && r.Param("partNumber") == "2" ? TemporaryRedirect($"https://{Bucket}.{Regional}/big.bin") : null);
        using var fs = Create(fake);

        var output = (TCV.Core.Interfaces.IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await ((Stream)output).WriteAsync(Encoding.UTF8.GetBytes("abcdefghijk"));
        await output.CommitAsync(null, CancellationToken.None);
        await ((Stream)output).DisposeAsync();

        fake.Text(Bucket, "big.bin").Should().Be("abcdefghijk");
        var part = fake.Requests.Where(r => r.Param("partNumber") == "2").ToList();
        part.Select(r => Encoding.UTF8.GetString(r.Body)).Should().Equal("efgh", "efgh");
        part[1].Uri.Host.Should().Be($"{Bucket}.{Regional}");
    }

    [Fact]
    public async Task ListAsync_RedirectToAnotherProvider_IsRefusedAndNothingIsSentThere()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r => TemporaryRedirect($"https://{Bucket}.s3.evil.example.net/"));
        using var fs = Create(fake);

        var act = () => fs.ListAsync(VfsPath.Root);

        (await act.Should().ThrowAsync<S3Exception>())
            .WithMessage("*s3.evil.example.net*not sent there*")
            .Which.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        fake.Requests.Should().ContainSingle().Which.Uri.Host.Should().Be($"{Bucket}.{Global}");
    }

    [Fact]
    public async Task ListAsync_RedirectChainWalkingAway_IsRefusedAtTheStepThatLeavesTheZone()
    {
        var fake = Store();
        fake.On(r => AtGlobal(r)
            ? TemporaryRedirect($"https://{Bucket}.{Regional}/")
            : TemporaryRedirect($"https://{Bucket}.s3.evil.example.net/"));
        using var fs = Create(fake);

        var act = () => fs.ListAsync(VfsPath.Root);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("*not sent there*");
        fake.Requests.Select(r => r.Uri.Host).Should().Equal($"{Bucket}.{Global}", $"{Bucket}.{Regional}");
    }

    [Fact]
    public async Task ListAsync_RedirectsWithoutEnd_GivesUpAfterTheLimit()
    {
        var fake = Store();
        fake.On(r => TemporaryRedirect($"https://{Bucket}.{Regional}/"));
        using var fs = Create(fake);

        var act = () => fs.ListAsync(VfsPath.Root);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("*keeps redirecting*");
        fake.Requests.Should().HaveCount(4, "the request and three hops");
    }

    [Fact]
    public async Task ListAsync_RedirectWithoutAnAddress_IsAnErrorInWords()
    {
        var fake = Store();
        fake.On(r => TemporaryRedirect(null));
        using var fs = Create(fake);

        var act = () => fs.ListAsync(VfsPath.Root);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("*names no address (TemporaryRedirect)*");
        fake.Requests.Should().ContainSingle();
    }

    [Fact]
    public async Task ListAsync_WrongRegionAfterARedirect_IsCorrectedByTheStoresOwnAnswer()
    {
        var fake = new FakeS3("storage.example.com");
        fake.OtherEndpoints.Add("eu.example.com");
        fake.Buckets[Bucket] = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        fake.On(r =>
        {
            if (r.Uri.Host == $"{Bucket}.storage.example.com")
            {
                return TemporaryRedirect($"https://{Bucket}.eu.example.com/");
            }

            return r.SignedRegion == "us-east-1"
                ? FakeS3.Error(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", "wrong region", "eu-1")
                : null;
        });
        using var fs = Create(fake);

        await fs.ListAsync(VfsPath.Root);

        fake.Requests[^1].SignedRegion.Should().Be("eu-1");
    }

    [Fact]
    public async Task ListAsync_AnonymousConnection_SendsNoAuthorizationAfterARedirect()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r => AtGlobal(r) ? TemporaryRedirect($"https://{Bucket}.{Regional}/") : null);
        using var fs = Create(fake, withKeys: false);

        await fs.ListAsync(VfsPath.Root);

        fake.Requests.Should().HaveCount(2).And.OnlyContain(r => r.Header("Authorization") == null);
    }
}
