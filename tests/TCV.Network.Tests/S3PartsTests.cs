using System.Globalization;
using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Tests;

/// <summary>
/// Multipart uploads side by side and tried again, and large copies on the server (UploadPartCopy),
/// against <see cref="FakeS3"/>, which answers as the AWS documentation says. No live store has seen this.
/// </summary>
public sealed class S3PartsTests
{
    private const string Bucket = "bucket";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static S3FileSystem Create(
        HttpMessageHandler handler, string endpoint = "s3.example.com", string? bucket = Bucket,
        long maxServerSideCopy = 5, int partConcurrency = 4, TimeSpan? stallLimit = null) =>
        new(new ConnectionInfo
        {
            Protocol = NetworkProtocol.S3,
            Host = endpoint,
            Port = 443,
            Bucket = bucket,
            Region = "us-east-1",
            Username = "AKIDEXAMPLE",
            Password = "secret",
        }, new HttpClient(handler))
        {
            MultipartThreshold = 10,
            MinPartSize = 4,
            CopyPartSize = 4,
            MaxServerSideCopy = maxServerSideCopy,
            PartConcurrency = partConcurrency,
            PartRetries = TestRetries.NoPauses,
            StallLimit = stallLimit ?? PartedTransfer.DefaultStallLimit,
            Clock = () => Now,
        };

    /// <summary>For a try meant to hang. Long enough that the tries that are not, on a busy machine, do not time out too.</summary>
    private static readonly TimeSpan ShortStall = TimeSpan.FromSeconds(2);

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

    private static async Task UploadAsync(IVirtualFileSystem fs, string path, string text, IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var output = (IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath(path), overwrite: true, ct);
        await ((Stream)output).WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await output.CommitAsync(progress, ct);
        await ((Stream)output).DisposeAsync();
    }

    private static List<FakeS3.Seen> Parts(FakeS3 fake, string number) =>
        fake.Requests.Where(r => r.Param("partNumber") == number).ToList();

    private static FakeS3.Seen Completion(FakeS3 fake) =>
        fake.Requests.Single(r => r.Method == HttpMethod.Post && r.Param("uploadId") is not null);

    private static List<int> ListedPartNumbers(FakeS3.Seen completion) =>
        XDocument.Parse(Encoding.UTF8.GetString(completion.Body)).Root!.Elements()
            .Select(p => int.Parse(p.Elements().First(e => e.Name.LocalName == "PartNumber").Value, CultureInfo.InvariantCulture))
            .ToList();

    // ── Uploads: side by side ────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_LargeBody_SendsPartsSideBySideWithinTheLimit()
    {
        var fake = Store();
        var inFlight = 0;
        var peak = 0;
        var twoIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Intercept = async (r, ct) =>
        {
            if (r.Param("partNumber") is null)
            {
                return null;
            }

            var now = Interlocked.Increment(ref inFlight);
            lock (twoIn)
            {
                peak = Math.Max(peak, now);
            }

            if (now >= 2)
            {
                twoIn.TrySetResult();
            }

            // A part alone waits for a second one here: sent one at a time, this would time out.
            await twoIn.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(2, ct);
            Interlocked.Decrement(ref inFlight);
            return null;
        };
        using var fs = Create(fake, partConcurrency: 2);

        await UploadAsync(fs, "/big.bin", "0123456789abcdefghijklmn");

        peak.Should().Be(2);
        fake.Text(Bucket, "big.bin").Should().Be("0123456789abcdefghijklmn");
    }

    [Fact]
    public async Task CommitAsync_PartsFinishOutOfOrder_CompletesThemByNumber()
    {
        var fake = Store();
        fake.Intercept = async (r, ct) =>
        {
            // The first part is the last to finish.
            if (r.Param("partNumber") == "1")
            {
                await Task.Delay(50, ct);
            }

            return null;
        };
        using var fs = Create(fake);

        await UploadAsync(fs, "/big.bin", "abcdefghijk");

        ListedPartNumbers(Completion(fake)).Should().Equal(1, 2, 3);
        fake.Text(Bucket, "big.bin").Should().Be("abcdefghijk", "each part in its own place in the file");
    }

    [Fact]
    public async Task CommitAsync_LargeBody_ReportsARisingTotalEndingAtTheWhole()
    {
        var fake = Store();
        using var fs = Create(fake);
        var reported = new List<long>();

        await UploadAsync(fs, "/big.bin", "0123456789abcdefghijklmn", new InlineProgress<long>(reported.Add));

        reported.Should().BeInAscendingOrder().And.EndWith(24);
    }

    // ── Uploads: tried again ─────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_LineDropsInTheMiddleOfAPart_SendsAllOfItsBytesAgain()
    {
        var fake = Store();
        var dropping = new DropsPartOnce(fake, "2", afterBytes: 2);
        using var fs = Create(dropping);

        await UploadAsync(fs, "/big.bin", "abcdefghijk");

        dropping.Dropped.Should().BeTrue();
        Parts(fake, "2").Should().ContainSingle("the failed try never got to the store").Which.Body
            .Should().Equal("efgh"u8.ToArray(), "the new try starts at the start of the part, not where the broken one stopped");
        fake.Text(Bucket, "big.bin").Should().Be("abcdefghijk");
    }

    [Fact]
    public async Task CommitAsync_ServerBusyOnce_SendsThePartAgainAndFinishes()
    {
        var fake = Store();
        var refusals = 0;
        fake.On(r => r.Param("partNumber") == "2" && Interlocked.Increment(ref refusals) == 1
            ? FakeS3.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.")
            : null);
        using var fs = Create(fake);
        var reported = new List<long>();

        await UploadAsync(fs, "/big.bin", "abcdefghijk", new InlineProgress<long>(reported.Add));

        var tries = Parts(fake, "2");
        tries.Should().HaveCount(2);
        tries[1].Body.Should().Equal(tries[0].Body, "a part sent again carries the same bytes");
        tries[1].Header("x-amz-content-sha256").Should().Be(tries[0].Header("x-amz-content-sha256"));
        fake.Text(Bucket, "big.bin").Should().Be("abcdefghijk");
        reported.Should().BeInAscendingOrder().And.EndWith(11);
    }

    [Fact]
    public async Task CommitAsync_PartKeepsFailing_GivesUpAfterTheLimitAndAborts()
    {
        var fake = Store();
        fake.On(r => r.Param("partNumber") == "2"
            ? FakeS3.Error(HttpStatusCode.InternalServerError, "InternalError", "We encountered an internal error. Please try again.")
            : null);
        using var fs = Create(fake);

        var act = () => UploadAsync(fs, "/big.bin", new string('x', 12));

        await act.Should().ThrowAsync<S3Exception>().WithMessage("We encountered an internal error*");
        Parts(fake, "2").Should().HaveCount(4);
        fake.Requests[^1].Method.Should().Be(HttpMethod.Delete, "aborted only after everything in flight has finished");
        fake.PendingUploads.Should().Be(0);
        fake.Keys(Bucket).Should().BeEmpty();
    }

    [Fact]
    public async Task CommitAsync_PartRefused_IsNotSentAgain()
    {
        var fake = Store();
        fake.On(r => r.Param("partNumber") == "2"
            ? FakeS3.Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied")
            : null);
        using var fs = Create(fake);

        var act = () => UploadAsync(fs, "/big.bin", new string('x', 12));

        await act.Should().ThrowAsync<S3Exception>().WithMessage("Access denied*");
        Parts(fake, "2").Should().ContainSingle();
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_Cancelled_StopsWithoutTryingAgainAndAborts()
    {
        var fake = Store();
        using var cts = new CancellationTokenSource();
        fake.Intercept = async (r, ct) =>
        {
            if (r.Param("partNumber") == "1")
            {
                await cts.CancelAsync();
            }

            if (r.Param("partNumber") is not null)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return null;
        };
        using var fs = Create(fake, partConcurrency: 1);

        var output = (IDeferredUploadStream)await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await ((Stream)output).WriteAsync(new byte[12]);
        var act = () => output.CommitAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fake.Requests.Count(r => r.Param("partNumber") is not null).Should().Be(1, "a cancellation is not tried again, and no further part starts");
        fake.Requests[^1].Method.Should().Be(HttpMethod.Delete);
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_TimeoutWrappedInCancellation_IsReportedAsAFailure()
    {
        var fake = Store();
        fake.Intercept = (r, _) => r.Param("partNumber") == "2"
            ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException())
            : Task.FromResult<HttpResponseMessage?>(null);
        using var fs = Create(fake);

        var act = () => UploadAsync(fs, "/big.bin", new string('x', 12));

        await act.Should().ThrowAsync<TimeoutException>("read as a cancellation, the failed upload would go unreported (TRAP-93)");
        Parts(fake, "2").Should().HaveCount(4);
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_AbortAnsweredBusy_IsAskedOnceMore()
    {
        var fake = Store();
        var aborts = 0;
        fake.On(r =>
        {
            if (r.Param("partNumber") == "1")
            {
                return FakeS3.Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied");
            }

            return r.Method == HttpMethod.Delete && Interlocked.Increment(ref aborts) == 1
                ? FakeS3.Error(HttpStatusCode.ServiceUnavailable, "SlowDown", "Please reduce your request rate.")
                : null;
        });
        using var fs = Create(fake);

        var act = () => UploadAsync(fs, "/big.bin", new string('x', 12));

        await act.Should().ThrowAsync<S3Exception>().WithMessage("Access denied*", "the failure that led to the abort is the one reported");
        aborts.Should().Be(2);
        fake.PendingUploads.Should().Be(0, "parts left behind are billed without being seen");
    }

    // ── Copies over the limit ────────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_OverTheLimit_CopiesInPartsOnTheServer()
    {
        var fake = Store(("big.bin", "0123456789"));
        using var fs = Create(fake);
        FileProgress? last = null;

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), new InlineProgress<FileProgress>(p => last = p), CancellationToken.None);

        fake.Text(Bucket, "copy.bin").Should().Be("0123456789");
        fake.Text(Bucket, "big.bin").Should().Be("0123456789");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Get, "the bytes never come down");
        fake.Requests.Where(r => r.Param("partNumber") is not null).Select(r => r.Header("x-amz-copy-source-range"))
            .Should().BeEquivalentTo(["bytes=0-3", "bytes=4-7", "bytes=8-9"], "a range counts both of its ends");
        fake.Requests.Should().NotContain(r => r.Param("partNumber") == null && r.Header("x-amz-copy-source") != null, "CopyObject refuses a source this large");
        ListedPartNumbers(Completion(fake)).Should().Equal(1, 2, 3);
        fake.PendingUploads.Should().Be(0);
        last!.BytesCopied.Should().Be(10);
    }

    [Fact]
    public async Task CopyAsync_ExactlyAtTheLimit_IsStillACopyObject()
    {
        var fake = Store(("five.bin", "01234"));
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/five.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        fake.Requests.Should().NotContain(r => r.Param("partNumber") != null);
        fake.Requests.Should().ContainSingle(r => r.Method == HttpMethod.Put && r.Header("x-amz-copy-source") != null);
        fake.Text(Bucket, "copy.bin").Should().Be("01234");
    }

    [Fact]
    public void CopyRange_BothEndsCount()
    {
        S3FileSystem.CopyRange(0, 10).Should().Be("bytes=0-9");
        S3FileSystem.CopyRange(10, 1).Should().Be("bytes=10-10");
        S3FileSystem.CopyRange(5368709120, 536870912).Should().Be("bytes=5368709120-5905580031");
    }

    [Fact]
    public async Task CopyAsync_KeyThatNeedsEncoding_NamesItEncodedOnceInEveryPart()
    {
        var fake = Store(("dir/a b+ü.bin", "0123456789"));
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/dir/a b+ü.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        fake.Requests.Where(r => r.Param("partNumber") is not null).Should()
            .OnlyContain(r => r.Header("x-amz-copy-source") == "/bucket/dir/a%20b%2B%C3%BC.bin");
        fake.Text(Bucket, "copy.bin").Should().Be("0123456789");
    }

    [Fact]
    public async Task CopyAsync_FromAnotherBucket_NamesThatBucketAsTheSource()
    {
        var fake = Store();
        fake.Put("other", "src/big.bin", "0123456789");
        using var fs = Create(fake, bucket: null);

        await fs.CopyAsync(new VfsPath("/other/src/big.bin"), new VfsPath("/bucket/copy.bin"), null, CancellationToken.None);

        fake.Text(Bucket, "copy.bin").Should().Be("0123456789");
        fake.Requests.Where(r => r.Param("partNumber") is not null).Should()
            .OnlyContain(r => r.Header("x-amz-copy-source") == "/other/src/big.bin");
    }

    [Fact]
    public async Task CopyAsync_OverTheLimit_StoresTheCopyAsTheSourceWasStored()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.On(r =>
        {
            if (r.Method != HttpMethod.Head)
            {
                return null;
            }

            var head = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            head.Content.Headers.ContentLength = 10;
            head.Content.Headers.TryAddWithoutValidation("Content-Type", "video/webm");
            head.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment");
            head.Headers.TryAddWithoutValidation("Cache-Control", "max-age=60");
            head.Headers.TryAddWithoutValidation("x-amz-meta-owner", "roman");
            head.Headers.TryAddWithoutValidation("ETag", FakeS3.ETagOf("0123456789"u8.ToArray()));
            return head;
        });
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.mp4"), null, CancellationToken.None);

        var created = fake.Requests.Single(r => r.Query.ContainsKey("uploads"));
        created.Header("Content-Type").Should().Be("video/webm", "UploadPartCopy carries nothing of this over by itself, and CopyObject did");
        created.Header("Content-Disposition").Should().Be("attachment");
        created.Header("Cache-Control").Should().Be("max-age=60");
        created.Header("x-amz-meta-owner").Should().Be("roman");
    }

    [Fact]
    public async Task CopyAsync_OverTheLimitAndTheSourceHasNoType_TypesItFromTheName()
    {
        var fake = Store(("big.bin", "0123456789"));
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.png"), null, CancellationToken.None);

        fake.Requests.Single(r => r.Query.ContainsKey("uploads")).Header("Content-Type").Should().Be("image/png");
    }

    // ── Copies: a 200 is not a success yet ───────────────────────────────────

    private static HttpResponseMessage ErrorIn200(string code, string message) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Error><Code>{code}</Code><Message>{message}</Message></Error>",
                Encoding.UTF8, "application/xml"),
        };

    [Fact]
    public async Task CopyAsync_PartAnswers200WithALateFailure_IsTriedAgain()
    {
        var fake = Store(("big.bin", "0123456789"));
        var failures = 0;
        fake.On(r => r.Param("partNumber") == "2" && Interlocked.Increment(ref failures) == 1
            ? ErrorIn200("InternalError", "We encountered an internal error. Please try again.")
            : null);
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        Parts(fake, "2").Should().HaveCount(2);
        fake.Text(Bucket, "copy.bin").Should().Be("0123456789");
    }

    [Fact]
    public async Task CopyAsync_PartAnswers200WithARefusal_FailsAndAborts()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.On(r => r.Param("partNumber") == "2" ? ErrorIn200("AccessDenied", "Access Denied") : null);
        using var fs = Create(fake);

        var act = () => fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("Access denied*");
        Parts(fake, "2").Should().ContainSingle();
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Get, "a refusal is no reason to stream the bytes instead");
        fake.Text(Bucket, "copy.bin").Should().BeNull();
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_CompletionAnswers200WithAFailure_FailsAndDoesNotCountAsCopied()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.On(r => r.Method == HttpMethod.Post && r.Param("uploadId") is not null
            ? ErrorIn200("InternalError", "We encountered an internal error. Please try again.")
            : null);
        using var fs = Create(fake);

        var act = () => fs.MoveAsync(new VfsPath("/big.bin"), new VfsPath("/moved.bin"), CancellationToken.None);

        await act.Should().ThrowAsync<S3Exception>();
        fake.Text(Bucket, "big.bin").Should().Be("0123456789", "the source of a move goes only once the copy is whole");
        fake.PendingUploads.Should().Be(0);
    }

    [Fact]
    public async Task CopyAsync_AnswersPaddedWithSpaces_AreStillRead()
    {
        var fake = Store(("big.bin", "0123456789"));
        using var fs = Create(new PadsAnswers(fake));

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        fake.Text(Bucket, "copy.bin").Should().Be("0123456789");
    }

    [Fact]
    public async Task CopyAsync_FailureInAnAnswerPaddedWithSpaces_IsStillAFailure()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.On(r => r.Param("partNumber") == "2" ? ErrorIn200("AccessDenied", "Access Denied") : null);
        using var fs = Create(new PadsAnswers(fake));

        var act = () => fs.MoveAsync(new VfsPath("/big.bin"), new VfsPath("/moved.bin"), CancellationToken.None);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("Access denied*");
        fake.Text(Bucket, "big.bin").Should().Be("0123456789");
    }

    [Fact]
    public async Task CopyAsync_StoreIgnoresTheRange_IsCaughtWhileTheBytesStillExist()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.IgnoresCopyRange = true;
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/big.bin"), new VfsPath("/moved.bin"), CancellationToken.None);

        fake.Text(Bucket, "moved.bin").Should().Be("0123456789", "three parts of the whole object would be three times its size");
        fake.Requests.Should().Contain(r => r.Method == HttpMethod.Get && r.Key == "big.bin", "the bytes were streamed through in the end");
        fake.Text(Bucket, "big.bin").Should().BeNull("the source went only after the right object was in place");
    }

    [Fact]
    public async Task CopyAsync_StoreIgnoresTheRangeAndTheStreamFailsToo_LeavesNoWrongObject()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.IgnoresCopyRange = true;
        fake.Intercept = (r, _) => Task.FromResult(r.Method == HttpMethod.Get && r.Key == "big.bin"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : null);
        using var fs = Create(fake);

        var act = () => fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        fake.Text(Bucket, "copy.bin").Should().BeNull("an object of the wrong size at that name passes for a good copy");
        fake.Text(Bucket, "big.bin").Should().Be("0123456789");
    }

    [Fact]
    public async Task CopyAsync_StreamFailsWithNothingWrongAtTheDestination_LeavesWhatIsThere()
    {
        var fake = Store(("big.bin", "0123456789"), ("copy.bin", "old"));
        fake.PartCopySupported = false;
        fake.Intercept = (r, _) => Task.FromResult(r.Method == HttpMethod.Get && r.Key == "big.bin"
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : null);
        using var fs = Create(fake);

        var act = () => fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        fake.Text(Bucket, "copy.bin").Should().Be("old");
    }

    [Fact]
    public async Task Upload_APartNobodyAnswers_IsSentAgain()
    {
        var fake = Store();
        var tries = 0;
        fake.Intercept = async (r, ct) =>
        {
            if (r.Param("partNumber") == "2" && Interlocked.Increment(ref tries) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return null;
        };
        using var fs = Create(fake, stallLimit: ShortStall);

        await UploadAsync(fs, "/big.bin", "0123456789abcdef");

        fake.Text(Bucket, "big.bin").Should().Be("0123456789abcdef");
        tries.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "SlowDown")]
    [InlineData(HttpStatusCode.BadRequest, "RequestTimeout")]
    public async Task Upload_SinglePutTheServerSaysLaterAbout_IsSentAgain(HttpStatusCode status, string code)
    {
        var fake = Store();
        var tries = 0;
        fake.Intercept = (r, _) => Task.FromResult(r.Method == HttpMethod.Put && Interlocked.Increment(ref tries) == 1
            ? new HttpResponseMessage(status)
            {
                Content = new StringContent($"<Error><Code>{code}</Code><Message>Later</Message></Error>"),
            }
            : null);
        using var fs = Create(fake);

        await UploadAsync(fs, "/small.bin", "12345");

        fake.Text(Bucket, "small.bin").Should().Be("12345");
        tries.Should().Be(2);
    }

    [Fact]
    public async Task Upload_SinglePutNobodyAnswers_IsSentAgain()
    {
        var fake = Store();
        var tries = 0;
        fake.Intercept = async (r, ct) =>
        {
            if (r.Method == HttpMethod.Put && Interlocked.Increment(ref tries) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return null;
        };
        using var fs = Create(fake, stallLimit: ShortStall);

        await UploadAsync(fs, "/small.bin", "12345");

        fake.Text(Bucket, "small.bin").Should().Be("12345");
    }

    [Fact]
    public async Task Upload_SinglePutToANameTaken_IsNotSentAgain()
    {
        var fake = Store();
        var tries = 0;
        fake.Intercept = (r, _) =>
        {
            if (r.Method != HttpMethod.Put)
            {
                return Task.FromResult<HttpResponseMessage?>(null);
            }

            Interlocked.Increment(ref tries);
            return Task.FromResult<HttpResponseMessage?>(new HttpResponseMessage(HttpStatusCode.PreconditionFailed));
        };
        using var fs = Create(fake);

        var act = async () =>
        {
            var output = await fs.OpenWriteAsync(new VfsPath("/small.bin"), overwrite: false);
            await output.WriteAsync("12345"u8.ToArray());
            await ((IDeferredUploadStream)output).CommitAsync(null, CancellationToken.None);
        };

        await act.Should().ThrowAsync<ObjectAlreadyExistsException>();
        tries.Should().Be(1);
    }

    [Fact]
    public async Task CopyAsync_SourceReplacedMidway_FailsInsteadOfSplicingTwoVersions()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.Intercept = (r, _) =>
        {
            if (r.Param("partNumber") is not null)
            {
                fake.Put(Bucket, "big.bin", "abcdefghij");
            }

            return Task.FromResult<HttpResponseMessage?>(null);
        };
        using var fs = Create(fake, partConcurrency: 1);

        var act = () => fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        await act.Should().ThrowAsync<S3Exception>().WithMessage("*changed on the server meanwhile*");
        fake.Requests.First(r => r.Param("partNumber") is not null).Header("x-amz-copy-source-if-match")
            .Should().Be(FakeS3.ETagOf("0123456789"u8.ToArray()));
        fake.Requests.Count(r => r.Param("partNumber") is not null).Should().Be(1, "it will not get better by asking again");
        fake.Text(Bucket, "copy.bin").Should().BeNull();
        fake.PendingUploads.Should().Be(0);
    }

    // ── Copies: a store without UploadPartCopy ───────────────────────────────

    [Fact]
    public async Task CopyAsync_StoreWithoutPartCopy_StreamsThroughAndDoesNotAskAgain()
    {
        var fake = Store(("big.bin", "0123456789"), ("big2.bin", "abcdefghij"));
        fake.PartCopySupported = false;
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);
        await fs.CopyAsync(new VfsPath("/big2.bin"), new VfsPath("/copy2.bin"), null, CancellationToken.None);

        fake.Text(Bucket, "copy.bin").Should().Be("0123456789");
        fake.Text(Bucket, "copy2.bin").Should().Be("abcdefghij");

        // How many of its parts went out before the first refusal cancelled the rest depends on timing.
        var partCopies = fake.Requests.Where(r => r.Header("x-amz-copy-source") != null).ToList();
        partCopies.Should().NotBeEmpty("the first copy tried");
        partCopies.Should().OnlyContain(r => r.Header("x-amz-copy-source")!.EndsWith("/big.bin"), "only the first copy tried");
        fake.Requests.Count(r => r.Method == HttpMethod.Get).Should().Be(2);
        fake.PendingUploads.Should().Be(0, "the failed attempt's upload was aborted");
    }

    [Fact]
    public async Task CopyAsync_StoreTakesThePartForAnEmptyOne_DoesNotBuildAnEmptyObject()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.On(r =>
        {
            if (r.Param("partNumber") is null || r.Header("x-amz-copy-source") is null)
            {
                return null;
            }

            // A store that knows nothing of the copy headers: an empty part, stored, with the ETag of nothing.
            var empty = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([]) };
            empty.Headers.TryAddWithoutValidation("ETag", FakeS3.ETagOf([]));
            return empty;
        });
        using var fs = Create(fake);

        await fs.CopyAsync(new VfsPath("/big.bin"), new VfsPath("/copy.bin"), null, CancellationToken.None);

        fake.Text(Bucket, "copy.bin").Should().Be("0123456789", "the bytes were streamed through after all");
        fake.Requests.Should().Contain(r => r.Method == HttpMethod.Get);
        fake.PendingUploads.Should().Be(0);
    }

    // ── Moves ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MoveAsync_OverTheLimit_DeletesTheSourceOnlyAfterTheCopyIsWhole()
    {
        var fake = Store(("big.bin", "0123456789"));
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/big.bin"), new VfsPath("/moved.bin"), CancellationToken.None);

        fake.Text(Bucket, "moved.bin").Should().Be("0123456789");
        fake.Text(Bucket, "big.bin").Should().BeNull();
        var order = fake.Requests.Select(r => r.Method.Method + (r.Param("uploadId") is null ? string.Empty : " upload")).ToList();
        order.LastIndexOf("POST upload").Should().BeLessThan(order.LastIndexOf("DELETE"));
    }

    [Fact]
    public async Task MoveAsync_PartCopyFails_LeavesTheSourceAlone()
    {
        var fake = Store(("big.bin", "0123456789"));
        fake.On(r => r.Param("partNumber") == "2" ? FakeS3.Error(HttpStatusCode.Forbidden, "AccessDenied", "Access Denied") : null);
        using var fs = Create(fake);

        var act = () => fs.MoveAsync(new VfsPath("/big.bin"), new VfsPath("/moved.bin"), CancellationToken.None);

        await act.Should().ThrowAsync<S3Exception>();
        fake.Text(Bucket, "big.bin").Should().Be("0123456789");
        fake.Text(Bucket, "moved.bin").Should().BeNull();
    }

    [Fact]
    public async Task MoveAsync_FolderWithAnObjectOverTheLimit_CopiesThatOneInParts()
    {
        var fake = Store(("src/small.txt", "ab"), ("src/big.bin", "0123456789"));
        using var fs = Create(fake);

        await fs.MoveAsync(new VfsPath("/src"), new VfsPath("/dst"), CancellationToken.None);

        fake.Text(Bucket, "dst/big.bin").Should().Be("0123456789");
        fake.Text(Bucket, "dst/small.txt").Should().Be("ab");
        fake.Keys(Bucket).Should().BeEquivalentTo("dst/big.bin", "dst/small.txt");
        fake.Requests.Should().NotContain(r => r.Method == HttpMethod.Get && r.Key.Length > 0, "the bytes never come down");
    }

    /// <summary>
    /// What S3 documents for a long copy: the 200 at once, then spaces to keep the connection open, and
    /// only then the outcome.
    /// </summary>
    private sealed class PadsAnswers(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var response = await base.SendAsync(request, ct);
            if (request.RequestUri!.Query.Contains("partNumber=", StringComparison.Ordinal) && response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                response.Content = new StringContent("   \n  " + body, Encoding.UTF8, "application/xml");
            }

            return response;
        }
    }

    /// <summary>Loses the line once while a part is on its way: the bytes read so far are gone with it.</summary>
    private sealed class DropsPartOnce(HttpMessageHandler inner, string part, int afterBytes) : DelegatingHandler(inner)
    {
        private int _dropped;

        public bool Dropped => _dropped == 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Query.Contains("partNumber=" + part + "&", StringComparison.Ordinal)
                && Interlocked.Exchange(ref _dropped, 1) == 0)
            {
                using var socket = new BreaksAfter(afterBytes);
                await request.Content!.CopyToAsync(socket, ct);
            }

            return await base.SendAsync(request, ct);
        }

        private sealed class BreaksAfter(int limit) : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
                Length + buffer.Length > limit
                    ? throw new IOException("Unable to write data to the transport connection: an existing connection was forcibly closed.")
                    : base.WriteAsync(buffer, ct);
        }
    }
}
