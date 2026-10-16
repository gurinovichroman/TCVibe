using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Tests;

/// <summary>What <see cref="S3FileSystem"/> puts on the wire and how it reacts to answers, against <see cref="FakeS3"/>.</summary>
public sealed class S3FileSystemTests
{
    private const string Bucket = "bucket";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static S3FileSystem Create(
        FakeS3 fake, string? bucket = Bucket, Action<ConnectionInfo>? configure = null,
        long multipartThreshold = 64L * 1024 * 1024, long minPartSize = 8L * 1024 * 1024,
        long maxServerSideCopy = 5L * 1024 * 1024 * 1024, TimeSpan? timeout = null,
        string? credentialsFile = null, string? configFile = null)
    {
        var connection = new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3,
            Host = fake.Endpoint,
            Port = 443,
            Bucket = bucket,
            Region = "us-east-1",
            Username = "AKIDEXAMPLE",
            Password = "secret",
        };
        configure?.Invoke(connection);
        return new S3FileSystem(connection, new HttpClient(fake))
        {
            MultipartThreshold = multipartThreshold,
            MinPartSize = minPartSize,
            MaxServerSideCopy = maxServerSideCopy,
            MetadataTimeout = timeout ?? TimeSpan.FromSeconds(30),
            Clock = () => Now,
            CredentialsFilePath = credentialsFile,
            ConfigFilePath = configFile,
        };
    }

    private static FakeS3 Store(params (string Key, string Content)[] objects)
    {
        var fake = new FakeS3();
        fake.Buckets[Bucket] = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (key, content) in objects)
        {
            fake.Put(Bucket, key, content);
        }

        return fake;
    }

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    // ── Listing ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_FolderWithFilesAndSubfolders_ReturnsBothAndSkipsTheFolderMarker()
    {
        var fake = Store(("docs/", ""), ("docs/a.txt", "abc"), ("docs/sub/", ""), ("docs/sub/b.txt", "b"), ("top.txt", "t"));
        using var fs = Create(fake);

        var entries = await fs.ListAsync(new VfsPath("/docs"));

        entries.Select(e => (e.Path.Value, e.Name, e.IsDirectory, e.Size)).Should().BeEquivalentTo(new[]
        {
            ("/docs/sub", "sub", true, 0L),
            ("/docs/a.txt", "a.txt", false, 3L),
        });
        entries.Single(e => !e.IsDirectory).LastModified.Should().Be(FakeS3.Modified.UtcDateTime.ToLocalTime());
        var list = fake.Requests.Single();
        list.Uri.Host.Should().Be("bucket.s3.example.com");
        list.Param("prefix").Should().Be("docs/");
        list.Param("delimiter").Should().Be("/");
        list.Param("encoding-type").Should().Be("url");
    }

    [Fact]
    public async Task ListAsync_KeysWithSpacesUnicodeAndPlus_ShowsTheRealNames()
    {
        var fake = Store(("a b.txt", "1"), ("ü+1.txt", "2"), ("x%y&z.txt", "3"), ("dir with space/f", "4"));
        using var fs = Create(fake);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => e.Name).Should().BeEquivalentTo("a b.txt", "ü+1.txt", "x%y&z.txt", "dir with space");
        entries.Single(e => e.Name == "ü+1.txt").Path.Value.Should().Be("/ü+1.txt");
    }

    [Fact]
    public async Task ListAsync_MoreKeysThanAPage_FollowsTheContinuationTokens()
    {
        var fake = Store(("1", "a"), ("2", "a"), ("3", "a"), ("4", "a"), ("5", "a"));
        fake.PageSize = 2;
        using var fs = Create(fake);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Select(e => e.Name).Should().Equal("1", "2", "3", "4", "5");
        fake.Requests.Should().HaveCount(3);
        fake.Requests[0].Param("continuation-token").Should().BeNull();
        fake.Requests[1].Param("continuation-token").Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ListAsync_NoBucketInTheConnection_RootListsTheBuckets()
    {
        var fake = new FakeS3();
        fake.Put("alpha", "x", "1");
        fake.Put("beta", "dir/y", "2");
        using var fs = Create(fake, bucket: null);

        var root = await fs.ListAsync(VfsPath.Root);
        var inBeta = await fs.ListAsync(new VfsPath("/beta"));

        root.Select(e => (e.Path.Value, e.IsDirectory)).Should().Equal(("/alpha", true), ("/beta", true));
        fake.Requests[0].Uri.AbsoluteUri.Should().Be("https://s3.example.com/");
        inBeta.Should().ContainSingle().Which.Path.Value.Should().Be("/beta/dir");
        fake.Requests[1].Uri.Host.Should().Be("beta.s3.example.com");
        fs.DisplayName.Should().Be("s3.example.com");
    }

    [Fact]
    public async Task ListAsync_PathStyleConnection_PutsTheBucketInThePath()
    {
        var fake = Store(("dir/a", "1"));
        using var fs = Create(fake, configure: c => c.PathStyle = true);

        await fs.ListAsync(new VfsPath("/dir"));

        var request = fake.Requests.Single();
        request.Uri.Host.Should().Be("s3.example.com");
        request.Uri.AbsolutePath.Should().Be("/bucket");
        fs.DisplayName.Should().Be(Bucket);
    }

    [Fact]
    public async Task ListAsync_SignedRequest_CarriesTheSigV4Headers()
    {
        var fake = Store();
        using var fs = Create(fake);

        await fs.ListAsync(VfsPath.Root);

        var request = fake.Requests.Single();
        request.Header("x-amz-date").Should().Be("20261013T080000Z");
        request.Header("x-amz-content-sha256").Should().Be(S3Signer.EmptyPayloadHash);
        request.Header("Authorization").Should().StartWith(
            "AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20261013/us-east-1/s3/aws4_request,SignedHeaders=host;x-amz-content-sha256;x-amz-date,Signature=");
    }

    [Fact]
    public async Task ListAsync_NoKeysAndNoProfile_SendsUnsignedRequestsForAPublicBucket()
    {
        var fake = Store(("a", "1"));
        using var fs = Create(fake, configure: c => { c.Username = string.Empty; c.Password = null; });

        await fs.ListAsync(VfsPath.Root);

        fake.Requests.Single().Header("Authorization").Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_ServerSilent_ThrowsTimeoutException()
    {
        var fake = Store();
        fake.Intercept = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return null;
        };
        using var fs = Create(fake, timeout: TimeSpan.FromMilliseconds(100));

        var act = () => fs.ListAsync(VfsPath.Root);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task ListAsync_SignatureDoesNotMatch_SaysWhatToCheck()
    {
        var fake = Store();
        fake.On(_ => FakeS3.Error(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", "The request signature we calculated does not match"));
        using var fs = Create(fake);

        var act = () => fs.ListAsync(VfsPath.Root);

        var thrown = await act.Should().ThrowAsync<S3Exception>().WithMessage("*check the secret key, the region and the addressing style*");
        thrown.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        thrown.Which.ErrorCode.Should().Be("SignatureDoesNotMatch");
    }

    // ── Entries ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEntryAsync_Object_ReturnsAFileWithItsSize()
    {
        using var fs = Create(Store(("dir/a.txt", "hello")));

        var entry = await fs.GetEntryAsync(new VfsPath("/dir/a.txt"));

        entry.Should().NotBeNull();
        (entry!.Name, entry.IsDirectory, entry.Size).Should().Be(("a.txt", false, 5L));
        entry.LastModified.Should().Be(FakeS3.Modified.UtcDateTime.ToLocalTime());
    }

    [Fact]
    public async Task GetEntryAsync_OnlyKeysUnderIt_ReturnsAFolder()
    {
        using var fs = Create(Store(("dir/deep/a.txt", "x")));

        var entry = await fs.GetEntryAsync(new VfsPath("/dir"));

        entry!.IsDirectory.Should().BeTrue();
    }

    [Fact]
    public async Task GetEntryAsync_NothingThere_ReturnsNull()
    {
        using var fs = Create(Store(("other", "x")));

        (await fs.GetEntryAsync(new VfsPath("/missing"))).Should().BeNull();
    }

    [Fact]
    public async Task GetEntryAsync_AccessDenied_ThrowsInsteadOfCallingItAbsent()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r => r.Method == HttpMethod.Head ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null);
        using var fs = Create(fake);

        var act = () => fs.GetEntryAsync(new VfsPath("/a.txt"));

        await act.Should().ThrowAsync<S3Exception>().WithMessage("Access denied*");
    }

    [Fact]
    public async Task GetEntryAsync_BucketRootRefusedWithoutBody_ExplainsWithTheListingsError()
    {
        var fake = Store();
        fake.On(r => r.Method == HttpMethod.Head
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : FakeS3.Error(HttpStatusCode.Forbidden, "InvalidAccessKeyId", "The AWS Access Key Id you provided does not exist in our records."));
        using var fs = Create(fake);

        var act = () => fs.GetEntryAsync(VfsPath.Root);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("*InvalidAccessKeyId*access key ID*");
    }

    [Fact]
    public async Task GetEntryAsync_MissingBucket_ReturnsNull()
    {
        using var fs = Create(new FakeS3(), bucket: "nope");

        (await fs.GetEntryAsync(VfsPath.Root)).Should().BeNull();
        (await fs.GetEntryAsync(new VfsPath("/a.txt"))).Should().BeNull();
    }

    // ── Region ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_BucketInAnotherRegion_RetriesThereAndRemembersIt()
    {
        var fake = new FakeS3("s3.us-east-1.amazonaws.com");
        var moved = new FakeS3("s3.eu-central-1.amazonaws.com");
        moved.Put(Bucket, "a.txt", "x");
        fake.Intercept = async (r, ct) =>
        {
            if (r.Uri.Host.Contains("us-east-1", StringComparison.Ordinal))
            {
                var redirect = FakeS3.Error(HttpStatusCode.MovedPermanently, "PermanentRedirect",
                    "The bucket you are attempting to access must be addressed using the specified endpoint.");
                redirect.Headers.Add("x-amz-bucket-region", "eu-central-1");
                return redirect;
            }

            using var forward = new HttpRequestMessage(r.Method, r.Uri);
            return await new HttpMessageInvoker(moved).SendAsync(forward, ct);
        };
        using var fs = Create(fake);

        var first = await fs.ListAsync(VfsPath.Root);
        var second = await fs.ListAsync(VfsPath.Root);

        first.Should().ContainSingle().Which.Name.Should().Be("a.txt");
        second.Should().ContainSingle();
        fake.Requests.Select(r => (r.Uri.Host, r.SignedRegion)).Should().Equal(
            ("bucket.s3.us-east-1.amazonaws.com", "us-east-1"),
            ("bucket.s3.eu-central-1.amazonaws.com", "eu-central-1"),
            ("bucket.s3.eu-central-1.amazonaws.com", "eu-central-1"));
    }

    [Fact]
    public async Task ListAsync_AuthorizationHeaderMalformed_TakesTheRegionFromTheErrorBody()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r => r.SignedRegion == "us-east-1"
            ? FakeS3.Error(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed",
                "The authorization header is malformed; the region 'us-east-1' is wrong; expecting 'ap-southeast-2'", "ap-southeast-2")
            : null);
        using var fs = Create(fake);

        var entries = await fs.ListAsync(VfsPath.Root);

        entries.Should().ContainSingle();
        fake.Requests.Select(r => r.SignedRegion).Should().Equal("us-east-1", "ap-southeast-2");
    }

    [Fact]
    public async Task GetEntryAsync_HeadRefusedWithTheRegionInAHeader_RetriesThere()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r =>
        {
            if (r.SignedRegion != "us-east-1")
            {
                return null;
            }

            // What AWS answers a HEAD signed for the wrong region: 400, no body, the region in a header.
            var refused = new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new ByteArrayContent([]) };
            refused.Headers.Add("x-amz-bucket-region", "ap-southeast-2");
            return refused;
        });
        using var fs = Create(fake);

        var entry = await fs.GetEntryAsync(new VfsPath("/a.txt"));

        entry.Should().NotBeNull();
        fake.Requests.Select(r => r.SignedRegion).Should().Equal("us-east-1", "ap-southeast-2");
    }

    [Fact]
    public async Task OpenReadAsync_RegionInAnError_IsRetriedAndRemembered()
    {
        var fake = Store(("a.txt", "hello"));
        fake.On(r => r.SignedRegion == "us-east-1"
            ? FakeS3.Error(HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", "wrong region", "eu-west-1")
            : null);
        using var fs = Create(fake);

        await using (var stream = await fs.OpenReadAsync(new VfsPath("/a.txt")))
        {
            new StreamReader(stream).ReadToEnd().Should().Be("hello");
        }

        (await fs.GetEntryAsync(new VfsPath("/a.txt"))).Should().NotBeNull();
        fake.Requests.Select(r => r.SignedRegion).Should().Equal("us-east-1", "eu-west-1", "eu-west-1");
    }

    [Fact]
    public async Task GetEntryAsync_HeadRedirectedWithoutRegion_AsksTheBucketForIt()
    {
        var fake = Store(("a.txt", "x"));
        fake.On(r =>
        {
            if (r.SignedRegion != "us-east-1")
            {
                return null;
            }

            var redirect = new HttpResponseMessage(HttpStatusCode.MovedPermanently) { Content = new ByteArrayContent([]) };
            if (r.Key.Length == 0)
            {
                redirect.Headers.Add("x-amz-bucket-region", "sa-east-1");
            }

            return redirect;
        });
        using var fs = Create(fake);

        var entry = await fs.GetEntryAsync(new VfsPath("/a.txt"));

        entry.Should().NotBeNull();
        fake.Requests.Select(r => (r.Method.Method, r.Key, r.SignedRegion)).Should().Equal(
            ("HEAD", "a.txt", "us-east-1"),
            ("HEAD", "", "us-east-1"),
            ("HEAD", "a.txt", "sa-east-1"));
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenReadAsync_MissingObject_ThrowsWithTheKeyInTheMessage()
    {
        using var fs = Create(Store());

        var act = () => fs.OpenReadAsync(new VfsPath("/dir/gone.txt"));

        await act.Should().ThrowAsync<S3Exception>().WithMessage("\"dir/gone.txt\" does not exist (NoSuchKey).");
    }

    [Fact]
    public async Task OpenReadFromAsync_SameETag_ResumesAtTheOffset()
    {
        var fake = Store(("f.bin", "0123456789"));
        using var fs = Create(fake);

        var (first, _, total, etag) = await fs.OpenReadFromAsync(new VfsPath("/f.bin"), 0, null, CancellationToken.None);
        await first.DisposeAsync();
        var (rest, offset, restTotal, restETag) = await fs.OpenReadFromAsync(new VfsPath("/f.bin"), 4, etag, CancellationToken.None);

        total.Should().Be(10);
        etag.Should().Be(FakeS3.ETagOf(Encoding.UTF8.GetBytes("0123456789")));
        (offset, restTotal, restETag).Should().Be((4L, 10L, etag));
        new StreamReader(rest).ReadToEnd().Should().Be("456789");
        var ranged = fake.Requests.Last();
        ranged.Header("Range").Should().Be("bytes=4-");
        ranged.Header("If-Match").Should().Be(etag);
    }

    [Fact]
    public async Task OpenReadFromAsync_ObjectChanged_StartsOverFromZero()
    {
        var fake = Store(("f.bin", "new content"));
        using var fs = Create(fake);

        var (stream, offset, total, etag) = await fs.OpenReadFromAsync(
            new VfsPath("/f.bin"), 4, "\"stale\"", CancellationToken.None);

        (offset, total).Should().Be((0L, 11L));
        etag.Should().Be(FakeS3.ETagOf(Encoding.UTF8.GetBytes("new content")));
        new StreamReader(stream).ReadToEnd().Should().Be("new content");
        fake.Requests.Should().HaveCount(2);
        fake.Requests[1].Header("Range").Should().BeNull();
    }

    // ── Uploads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_SmallBody_SendsOneSignedPutWithItsHash()
    {
        var fake = Store();
        using var fs = Create(fake);
        string bufferPath;

        await using (var output = await fs.OpenWriteAsync(new VfsPath("/dir/a b.txt"), overwrite: true))
        {
            bufferPath = ((S3UploadStream)output).BufferPath;
            await output.WriteAsync(Encoding.UTF8.GetBytes("hel"));
            output.Write(Encoding.UTF8.GetBytes("lo"));
        }

        var put = fake.Requests.Should().ContainSingle().Subject;
        put.Method.Should().Be(HttpMethod.Put);
        put.Uri.AbsolutePath.Should().Be("/dir/a%20b.txt");
        put.Header("x-amz-content-sha256").Should().Be(Sha256("hello"));
        put.Header("Content-Type").Should().Be("text/plain");
        put.Header("If-None-Match").Should().BeNull();
        fake.Text(Bucket, "dir/a b.txt").Should().Be("hello");
        File.Exists(bufferPath).Should().BeFalse("the temporary copy goes once the body is sent");
    }

    [Fact]
    public async Task CommitAsync_BodyOverTheThreshold_UploadsInPartsWithProgress()
    {
        var fake = Store();
        using var fs = Create(fake, multipartThreshold: 10, minPartSize: 4);
        var reported = new List<long>();

        var output = (IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await ((Stream)output).WriteAsync(Encoding.UTF8.GetBytes("abcdefghijk"));
        await output.CommitAsync(new InlineProgress<long>(reported.Add), CancellationToken.None);
        await ((Stream)output).DisposeAsync();

        fake.Text(Bucket, "big.bin").Should().Be("abcdefghijk");
        fake.Requests.Select(r => (r.Method.Method, r.Param("partNumber"), r.Body.Length)).Should().Equal(
            ("POST", null, 0),
            ("PUT", "1", 4),
            ("PUT", "2", 4),
            ("PUT", "3", 3),
            ("POST", null, fake.Requests[^1].Body.Length));
        fake.Requests[0].Query.Should().ContainKey("uploads");
        fake.Requests[^1].Param("uploadId").Should().Be("upload-1");
        reported.Should().BeInAscendingOrder().And.EndWith(11);
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_PartFails_AbortsTheMultipartUpload()
    {
        var fake = Store();
        fake.On(r => r.Param("partNumber") == "2"
            ? FakeS3.Error(HttpStatusCode.InternalServerError, "InternalError", "We encountered an internal error. Please try again.")
            : null);
        using var fs = Create(fake, multipartThreshold: 10, minPartSize: 4);

        var output = (IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await ((Stream)output).WriteAsync(new byte[12]);
        var act = () => output.CommitAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("We encountered an internal error*");
        var abort = fake.Requests.Last();
        (abort.Method, abort.Param("uploadId")).Should().Be((HttpMethod.Delete, "upload-1"));
        fake.PendingUploads.Should().Be(0, "parts left behind are billed without being seen");
        fake.Keys(Bucket).Should().BeEmpty();
    }

    [Fact]
    public async Task CommitAsync_CancelledBetweenParts_AbortsTheMultipartUpload()
    {
        var fake = Store();
        using var cts = new CancellationTokenSource();
        fake.On(r =>
        {
            if (r.Param("partNumber") == "1")
            {
                cts.Cancel();
            }

            return null;
        });
        using var fs = Create(fake, multipartThreshold: 10, minPartSize: 4);

        var output = (IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await ((Stream)output).WriteAsync(new byte[12]);
        var act = () => output.CommitAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fake.Requests.Last().Method.Should().Be(HttpMethod.Delete);
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task OpenWriteAsync_WithoutOverwriteOnAnExistingObject_RefusesBeforeCollecting()
    {
        var fake = Store(("a.txt", "old"));
        using var fs = Create(fake);

        var act = () => fs.OpenWriteAsync(new VfsPath("/a.txt"), overwrite: false);

        await act.Should().ThrowAsync<IOException>().WithMessage("\"a.txt\" already exists.");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task OpenWriteAsync_WithoutOverwrite_AsksTheStoreToRefuseAnObjectThatAppearedMeanwhile()
    {
        var fake = Store();
        using var fs = Create(fake);

        var act = async () =>
        {
            await using var output = await fs.OpenWriteAsync(new VfsPath("/a.txt"), overwrite: false);
            fake.Put(Bucket, "a.txt", "theirs");
            await output.WriteAsync(Encoding.UTF8.GetBytes("mine"));
        };

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        fake.Requests.Last().Header("If-None-Match").Should().Be("*");
        fake.Text(Bucket, "a.txt").Should().Be("theirs");
    }

    [Fact]
    public async Task OpenWriteAsync_StoreWithoutConditionalWrites_SendsAgainWithoutTheCondition()
    {
        var fake = Store();
        fake.RejectsIfNoneMatch = true;
        using var fs = Create(fake);

        await using (var output = await fs.OpenWriteAsync(new VfsPath("/a.txt"), overwrite: false))
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes("x"));
        }

        fake.Text(Bucket, "a.txt").Should().Be("x");
        fake.Requests.Where(r => r.Method == HttpMethod.Put).Select(r => r.Header("If-None-Match")).Should().Equal("*", null);
    }

    [Fact]
    public async Task CommitAsync_MultipartWithoutOverwrite_CompletesOnlyIfNothingIsThere()
    {
        var fake = Store();
        using var fs = Create(fake, multipartThreshold: 10, minPartSize: 4);

        var output = (IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: false);
        await ((Stream)output).WriteAsync(new byte[12]);
        fake.Put(Bucket, "big.bin", "theirs");
        var act = () => output.CommitAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        fake.Text(Bucket, "big.bin").Should().Be("theirs");
        fake.PendingUploads.Should().Be(0);
    }

    // ── Folders and deletes ──────────────────────────────────────────────────

    [Fact]
    public async Task CreateDirectoryAsync_InABucket_PutsAZeroByteMarker()
    {
        var fake = Store();
        using var fs = Create(fake);

        await fs.CreateDirectoryAsync(new VfsPath("/new folder"), CancellationToken.None);

        var put = fake.Requests.Single();
        (put.Method, put.Uri.AbsolutePath, put.Body.Length).Should().Be((HttpMethod.Put, "/new%20folder/", 0));
        fake.Keys(Bucket).Should().Equal("new folder/");
    }

    [Fact]
    public async Task CreateDirectoryAsync_AtTheBucketLevel_IsNotSupported()
    {
        using var fs = Create(new FakeS3(), bucket: null);

        var act = () => fs.CreateDirectoryAsync(new VfsPath("/new-bucket"), CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>().WithMessage("Buckets are created in the provider's console*");
    }

    [Fact]
    public async Task DeleteAsync_RecursiveFolder_DeletesInBatchesOfAThousandWithContentMd5()
    {
        var fake = Store(("keep.txt", "k"), ("big/", ""));
        for (var i = 0; i < 1500; i++)
        {
            fake.Put(Bucket, $"big/{i:D4}.txt", "x");
        }

        using var fs = Create(fake);

        await fs.DeleteAsync(new VfsPath("/big"), recursive: true, CancellationToken.None);

        var batches = fake.Requests.Where(r => r.Method == HttpMethod.Post).ToList();
        batches.Should().HaveCount(2);
        batches.Should().OnlyContain(r => r.Header("Content-MD5") == Convert.ToBase64String(MD5.HashData(r.Body)));
        fake.Keys(Bucket).Should().Equal("keep.txt");
    }

    [Fact]
    public async Task DeleteAsync_StoreWithoutDeleteObjects_FallsBackToOneDeletePerKey()
    {
        var fake = Store(("a/1", "x"), ("a/2", "x"), ("a/3", "x"), ("b/1", "x"), ("b/2", "x"));
        fake.BulkDeleteSupported = false;
        using var fs = Create(fake);

        await fs.DeleteAsync(new VfsPath("/a"), recursive: true, CancellationToken.None);
        await fs.DeleteAsync(new VfsPath("/b"), recursive: true, CancellationToken.None);

        fake.Keys(Bucket).Should().BeEmpty();
        fake.Requests.Count(r => r.Method == HttpMethod.Post).Should().Be(1, "after the first 501 the store is not asked again");
        fake.Requests.Count(r => r.Method == HttpMethod.Delete).Should().Be(5);
    }

    [Fact]
    public async Task DeleteAsync_NotRecursiveOnAFolderWithFiles_RefusesAndDeletesNothing()
    {
        var fake = Store(("dir/", ""), ("dir/a.txt", "x"));
        using var fs = Create(fake);

        var act = () => fs.DeleteAsync(new VfsPath("/dir"), recursive: false, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("The folder \"dir\" is not empty.");
        fake.Keys(Bucket).Should().HaveCount(2);
    }

    [Fact]
    public async Task DeleteAsync_NotRecursiveOnAnEmptyFolder_RemovesItsMarker()
    {
        var fake = Store(("dir/", ""), ("other", "x"));
        using var fs = Create(fake);

        await fs.DeleteAsync(new VfsPath("/dir"), recursive: false, CancellationToken.None);

        fake.Keys(Bucket).Should().Equal("other");
    }

    [Fact]
    public async Task DeleteAsync_File_DeletesTheObjectAndRaisesChanged()
    {
        var fake = Store(("dir/a.txt", "x"));
        using var fs = Create(fake);
        VfsChangedEventArgs? raised = null;
        fs.Changed += (_, e) => raised = e;

        await fs.DeleteAsync(new VfsPath("/dir/a.txt"), recursive: false, CancellationToken.None);

        fake.Keys(Bucket).Should().BeEmpty();
        raised!.ChangeType.Should().Be(WatcherChangeTypes.Deleted);
    }

    // ── Copy, move, rename ───────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_SameStore_CopiesServerSideWithAnEncodedSource()
    {
        var fake = Store(("dir/a b+ü.txt", "data"));
        using var fs = Create(fake);
        FileProgress? last = null;

        await fs.CopyAsync(new VfsPath("/dir/a b+ü.txt"), new VfsPath("/copy.txt"),
            new InlineProgress<FileProgress>(p => last = p), CancellationToken.None);

        var copy = fake.Requests.Single(r => r.Method == HttpMethod.Put);
        copy.Header("x-amz-copy-source").Should().Be("/bucket/dir/a%20b%2B%C3%BC.txt");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Get, "the bytes never come down");
        fake.Text(Bucket, "copy.txt").Should().Be("data");
        last!.BytesCopied.Should().Be(4);
    }

    [Fact]
    public async Task CopyAsync_SourceOverTheServerSideLimit_StreamsThrough()
    {
        var fake = Store(("big.bin", "0123456789"));
        using var fs = Create(fake, maxServerSideCopy: 5);

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/big2.bin"), null, CancellationToken.None);

        fake.Text(Bucket, "big2.bin").Should().Be("0123456789");
        fake.Requests.Should().NotContain(r => r.Header("x-amz-copy-source") != null);
    }

    [Fact]
    public async Task MoveAsync_Folder_MovesEveryObjectUnderItAndKeepsEmptySubfolders()
    {
        var fake = Store(("src/", ""), ("src/a.txt", "a"), ("src/empty/", ""), ("src/sub/b.txt", "b"), ("srcx", "not inside"));
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/src"), new VfsPath("/dst"), CancellationToken.None);

        fake.Keys(Bucket).Should().BeEquivalentTo("dst/", "dst/a.txt", "dst/empty/", "dst/sub/b.txt", "srcx");
        fake.Text(Bucket, "dst/sub/b.txt").Should().Be("b");
    }

    [Fact]
    public async Task MoveAsync_FolderIntoItself_IsRefused()
    {
        var fake = Store(("src/a.txt", "a"));
        using var fs = Create(fake);

        var act = () => fs.MoveAsync(new VfsPath("/src"), new VfsPath("/src/inner"), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*into itself*");
        fake.Keys(Bucket).Should().Equal("src/a.txt");
    }

    [Fact]
    public async Task MoveAsync_OntoAnExistingFile_ReplacesIt()
    {
        var fake = Store(("tmp.part", "new"), ("a.txt", "old"));
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/tmp.part"), new VfsPath("/a.txt"), CancellationToken.None);

        fake.Keys(Bucket).Should().Equal("a.txt");
        fake.Text(Bucket, "a.txt").Should().Be("new");
    }

    [Fact]
    public async Task RenameAsync_File_CopiesAndDeletes()
    {
        var fake = Store(("dir/old.txt", "x"));
        using var fs = Create(fake);

        await fs.RenameAsync(new VfsPath("/dir/old.txt"), "new name.txt", CancellationToken.None);

        fake.Keys(Bucket).Should().Equal("dir/new name.txt");
    }

    [Fact]
    public async Task RenameAsync_TargetExists_RefusesAndKeepsBoth()
    {
        var fake = Store(("a.txt", "a"), ("b.txt", "b"));
        using var fs = Create(fake);

        var act = () => fs.RenameAsync(new VfsPath("/a.txt"), "b.txt", CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("\"b.txt\" already exists.");
        (fake.Text(Bucket, "a.txt"), fake.Text(Bucket, "b.txt")).Should().Be(("a", "b"));
    }

    [Fact]
    public async Task RenameAsync_OnlyTheCaseChanges_StillRenames()
    {
        var fake = Store(("readme", "x"));
        using var fs = Create(fake);

        await fs.RenameAsync(new VfsPath("/readme"), "README", CancellationToken.None);

        fake.Keys(Bucket).Should().Equal("README");
    }

    // ── Presigned links and credentials ──────────────────────────────────────

    [Fact]
    public void Presign_Object_SignsALinkToTheBucketHost()
    {
        using var fs = Create(Store());

        var link = fs.Presign(new VfsPath("/dir/a b.txt"), TimeSpan.FromHours(1)).AbsoluteUri;

        link.Should().StartWith(
            "https://bucket.s3.example.com/dir/a%20b.txt?X-Amz-Algorithm=AWS4-HMAC-SHA256" +
            "&X-Amz-Credential=AKIDEXAMPLE%2F20261013%2Fus-east-1%2Fs3%2Faws4_request" +
            "&X-Amz-Date=20261013T080000Z&X-Amz-Expires=3600&X-Amz-SignedHeaders=host&X-Amz-Signature=");
    }

    [Fact]
    public void Presign_ConnectionWithoutKeys_Throws()
    {
        using var fs = Create(Store(), configure: c => c.Username = string.Empty);

        var act = () => fs.Presign(new VfsPath("/a.txt"), TimeSpan.FromHours(1));

        act.Should().Throw<InvalidOperationException>().WithMessage("*no access key*");
    }

    [Fact]
    public void ReadLink_ConnectionWithKeys_IsThePresignedLink()
    {
        // What «Copy link» in the file menu asks for (6.81).
        using var fs = Create(Store());
        IReadLinkSource source = fs;

        source.CanCreateReadLink.Should().BeTrue();
        source.CreateReadLink(new VfsPath("/a.txt"), TimeSpan.FromDays(1))
            .Should().Be(fs.Presign(new VfsPath("/a.txt"), TimeSpan.FromDays(1)));
    }

    [Fact]
    public void ReadLink_ConnectionWithoutKeys_CannotBeMade()
    {
        using var fs = Create(Store(), configure: c => c.Username = string.Empty);

        ((IReadLinkSource)fs).CanCreateReadLink.Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ProfileConnection_SignsWithTheProfileKeysAndRegion()
    {
        var folder = Directory.CreateTempSubdirectory("tcv-s3-profile-").FullName;
        try
        {
            var credentials = Path.Combine(folder, "credentials");
            var config = Path.Combine(folder, "config");
            await File.WriteAllTextAsync(credentials, "[dev]\naws_access_key_id = AKIDPROFILE\naws_secret_access_key = s\naws_session_token = TOK\n");
            await File.WriteAllTextAsync(config, "[profile dev]\nregion = eu-west-3\n");
            var fake = Store();
            using var fs = Create(fake, configure: c => { c.Username = string.Empty; c.Region = null; c.Profile = "dev"; },
                credentialsFile: credentials, configFile: config);

            await fs.ListAsync(VfsPath.Root);

            var request = fake.Requests.Single();
            request.Header("Authorization").Should().Contain("Credential=AKIDPROFILE/20261013/eu-west-3/s3/aws4_request");
            request.Header("x-amz-security-token").Should().Be("TOK");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task ListAsync_SsoProfile_FailsWithAClearMessage()
    {
        var folder = Directory.CreateTempSubdirectory("tcv-s3-profile-").FullName;
        try
        {
            var config = Path.Combine(folder, "config");
            await File.WriteAllTextAsync(config, "[profile work]\nsso_session = corp\nsso_account_id = 1\n");
            var fake = Store();
            using var fs = Create(fake, configure: c => { c.Username = string.Empty; c.Profile = "work"; },
                credentialsFile: Path.Combine(folder, "credentials"), configFile: config);

            var act = () => fs.ListAsync(VfsPath.Root);

            await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*IAM Identity Center (SSO)*static keys*");
            fake.Requests.Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void CreateFileSystem_S3Connection_IsAnS3FileSystem()
    {
        using var fs = NetworkPlugin.CreateFileSystem(new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3, Host = "s3.eu-west-1.amazonaws.com", Port = 443, Bucket = "logs",
        });

        fs.Should().BeOfType<S3FileSystem>().Which.DisplayName.Should().Be("logs");
        fs.Scheme.Should().Be("s3");
        new NetworkPlugin().Schemes.Should().Contain("s3");
    }
}
