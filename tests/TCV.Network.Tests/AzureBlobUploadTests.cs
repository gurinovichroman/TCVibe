using System.Net;
using System.Text;
using System.Xml.Linq;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Tests;

/// <summary>
/// Blocks side by side and tried again, and the type a blob is stored with, against
/// <see cref="FakeBlobService"/>, written from the Blob service documentation. No live account has seen this.
/// </summary>
public sealed class AzureBlobUploadTests
{
    /// <summary>For a try meant to hang. Long enough that the tries that are not, on a busy machine, do not time out too.</summary>
    private static readonly TimeSpan ShortStall = TimeSpan.FromSeconds(2);

    private static AzureBlobFileSystem Create(FakeBlobService fake, int partConcurrency = 4, TimeSpan? stallLimit = null) =>
        new(new ConnectionInfo { Protocol = NetworkProtocol.AzureBlob, Username = "myaccount", Password = AzureSharedKeyTests.Key, Bucket = "c" },
            new HttpClient(fake))
        {
            SingleUploadLimit = 10,
            BlockSize = 4,
            PartConcurrency = partConcurrency,
            PartRetries = TestRetries.NoPauses,
            StallLimit = stallLimit ?? PartedTransfer.DefaultStallLimit,
        };

    private static async Task UploadAsync(IVirtualFileSystem fs, string path, string text, IProgress<long>? progress = null, bool overwrite = true, CancellationToken ct = default)
    {
        var output = await fs.OpenWriteAsync(new VfsPath(path), overwrite, ct);
        await output.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await ((IDeferredUploadStream)output).CommitAsync(progress, ct);
        await output.DisposeAsync();
    }

    private static bool IsBlock(HttpRequestMessage request, int number) =>
        request.RequestUri!.Query.Contains("comp=block&", StringComparison.Ordinal)
        && BlockNumber(request.RequestUri.Query) == number;

    /// <summary>The block's place in the file, from its id: base64 of <c>upload-000002</c>.</summary>
    private static int BlockNumber(string query)
    {
        var id = Uri.UnescapeDataString(query[(query.IndexOf("blockid=", StringComparison.Ordinal) + "blockid=".Length)..]);
        var text = Encoding.ASCII.GetString(Convert.FromBase64String(id));
        return int.Parse(text[(text.IndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
    }

    // ── Side by side ─────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_LargeFile_StagesBlocksSideBySideWithinTheLimit()
    {
        var fake = new FakeBlobService().WithContainer("c");
        var inFlight = 0;
        var peak = 0;
        var twoIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BeforeAnswer = async (request, ct) =>
        {
            if (!request.RequestUri!.Query.Contains("comp=block&", StringComparison.Ordinal))
            {
                return;
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

            // A block alone waits for a second one here: sent one at a time, this would time out.
            await twoIn.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(2, ct);
            Interlocked.Decrement(ref inFlight);
        };
        using var fs = Create(fake, partConcurrency: 2);

        await UploadAsync(fs, "/big.bin", "0123456789abcdefghijklmn");

        peak.Should().Be(2);
        fake.Text("c", "big.bin").Should().Be("0123456789abcdefghijklmn");
    }

    [Fact]
    public async Task OpenWriteAsync_BlocksFinishOutOfOrder_CommitsThemInFileOrder()
    {
        var fake = new FakeBlobService().WithContainer("c");
        fake.BeforeAnswer = async (request, ct) =>
        {
            // The first block is the last to finish.
            if (IsBlock(request, 0))
            {
                await Task.Delay(50, ct);
            }
        };
        using var fs = Create(fake);

        await UploadAsync(fs, "/big.bin", "hello world");

        var list = XElement.Parse(Encoding.UTF8.GetString(fake.Calls.Single(c => c.Q("comp") == "blocklist").Body));
        list.Elements("Latest").Select(e => BlockNumber("blockid=" + e.Value)).Should().Equal(0, 1, 2);
        fake.Text("c", "big.bin").Should().Be("hello world");
    }

    [Fact]
    public async Task OpenWriteAsync_LargeFile_ReportsARisingTotalEndingAtTheWhole()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);
        var reported = new List<long>();

        await UploadAsync(fs, "/big.bin", "0123456789abcdefghijklmn", new InlineProgress<long>(reported.Add));

        reported.Should().BeInAscendingOrder().And.EndWith(24);
    }

    // ── Tried again ──────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_SingleBlobServerBusyOnce_IsSentAgain()
    {
        var fake = new FakeBlobService().WithContainer("c");
        var refusals = 0;
        fake.Intercept = request => request.Method == HttpMethod.Put && Interlocked.Increment(ref refusals) == 1
            ? FakeBlobService.Error(HttpStatusCode.ServiceUnavailable, "ServerBusy")
            : null;
        using var fs = Create(fake);
        var reported = new List<long>();

        await UploadAsync(fs, "/small.bin", "hello", new InlineProgress<long>(reported.Add));

        fake.Text("c", "small.bin").Should().Be("hello");
        refusals.Should().Be(2);
        reported.Should().BeInAscendingOrder().And.EndWith(5);
    }

    [Fact]
    public async Task OpenWriteAsync_SingleBlobNobodyAnswers_IsSentAgain()
    {
        var fake = new FakeBlobService().WithContainer("c");
        var tries = 0;
        fake.BeforeAnswer = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Put && Interlocked.Increment(ref tries) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var fs = Create(fake, stallLimit: ShortStall);

        await UploadAsync(fs, "/small.bin", "hello");

        fake.Text("c", "small.bin").Should().Be("hello");
    }

    [Fact]
    public async Task OpenWriteAsync_ABlockNobodyAnswers_IsSentAgain()
    {
        var fake = new FakeBlobService().WithContainer("c");
        var tries = 0;
        fake.BeforeAnswer = async (request, ct) =>
        {
            if (IsBlock(request, 1) && Interlocked.Increment(ref tries) == 1)
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var fs = Create(fake, stallLimit: ShortStall);

        await UploadAsync(fs, "/big.bin", "hello world");

        fake.Text("c", "big.bin").Should().Be("hello world");
    }

    [Fact]
    public void Handler_DoesNotFollowRedirects()
    {
        // A Shared Key signature covers one URL; sent on by the handler, it would go wherever an answer points.
        using var handler = AzureBlobFileSystem.CreateHandler();

        handler.AllowAutoRedirect.Should().BeFalse();
    }

    [Fact]
    public async Task OpenWriteAsync_ServerBusyOnce_StagesTheBlockAgainWithTheSameBytesAndId()
    {
        var fake = new FakeBlobService().WithContainer("c");
        var refusals = 0;
        fake.Intercept = request => IsBlock(request, 1) && Interlocked.Increment(ref refusals) == 1
            ? FakeBlobService.Error(HttpStatusCode.ServiceUnavailable, "ServerBusy")
            : null;
        using var fs = Create(fake);
        var reported = new List<long>();

        await UploadAsync(fs, "/big.bin", "hello world", new InlineProgress<long>(reported.Add));

        var tries = fake.Calls.Where(c => c.Q("comp") == "block" && BlockNumber("blockid=" + c.Q("blockid")) == 1).ToList();
        tries.Should().HaveCount(2);
        tries[1].Body.Should().Equal(tries[0].Body).And.Equal("o wo"u8.ToArray());
        tries[1].Q("blockid").Should().Be(tries[0].Q("blockid"));
        fake.Text("c", "big.bin").Should().Be("hello world");
        reported.Should().BeInAscendingOrder().And.EndWith(11);
    }

    [Fact]
    public async Task OpenWriteAsync_LineDropsInTheMiddleOfABlock_SendsAllOfItsBytesAgain()
    {
        var fake = new FakeBlobService().WithContainer("c");
        var dropping = new DropsBlockOnce(fake, 1, afterBytes: 2);
        using var fs = new AzureBlobFileSystem(
            new ConnectionInfo { Protocol = NetworkProtocol.AzureBlob, Username = "myaccount", Password = AzureSharedKeyTests.Key, Bucket = "c" },
            new HttpClient(dropping))
        {
            SingleUploadLimit = 10,
            BlockSize = 4,
            PartRetries = TestRetries.NoPauses,
        };

        await UploadAsync(fs, "/big.bin", "hello world");

        dropping.Dropped.Should().BeTrue();
        fake.Text("c", "big.bin").Should().Be("hello world", "the new try starts at the start of the block, not where the broken one stopped");
    }

    [Fact]
    public async Task OpenWriteAsync_BlockKeepsFailing_GivesUpAfterTheLimitAndCommitsNothing()
    {
        var fake = new FakeBlobService().With("c", "big.bin", "old");
        fake.Intercept = request => IsBlock(request, 1) ? FakeBlobService.Error(HttpStatusCode.InternalServerError, "InternalError") : null;
        using var fs = Create(fake);

        var act = () => UploadAsync(fs, "/big.bin", "hello world");

        await act.Should().ThrowAsync<AzureStorageException>();
        fake.Calls.Count(c => c.Q("comp") == "block" && BlockNumber("blockid=" + c.Q("blockid")) == 1).Should().Be(4);
        fake.Calls.Should().NotContain(c => c.Q("comp") == "blocklist");
        fake.Text("c", "big.bin").Should().Be("old", "the blob changes only at the commit");
    }

    [Fact]
    public async Task OpenWriteAsync_BlockRefused_IsNotSentAgain()
    {
        var fake = new FakeBlobService().WithContainer("c");
        fake.Intercept = request => IsBlock(request, 1) ? FakeBlobService.Error(HttpStatusCode.Forbidden, "AuthorizationFailure") : null;
        using var fs = Create(fake);

        var act = () => UploadAsync(fs, "/big.bin", "hello world");

        await act.Should().ThrowAsync<AzureStorageException>();
        fake.Calls.Count(c => c.Q("comp") == "block" && BlockNumber("blockid=" + c.Q("blockid")) == 1).Should().Be(1);
        fake.Calls.Should().NotContain(c => c.Q("comp") == "blocklist");
    }

    [Fact]
    public async Task OpenWriteAsync_Cancelled_StopsWithoutTryingAgain()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var cts = new CancellationTokenSource();
        fake.BeforeAnswer = async (request, ct) =>
        {
            if (request.RequestUri!.Query.Contains("comp=block&", StringComparison.Ordinal))
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, ct);
            }
        };
        using var fs = Create(fake, partConcurrency: 1);

        var output = await fs.OpenWriteAsync(new VfsPath("/big.bin"), overwrite: true);
        await output.WriteAsync(new byte[12]);
        var act = () => ((IDeferredUploadStream)output).CommitAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        fake.Calls.Count(c => c.Q("comp") == "block").Should().Be(1);
        fake.Calls.Should().NotContain(c => c.Q("comp") == "blocklist");
    }

    [Fact]
    public async Task OpenWriteAsync_WithoutOverwrite_CommitIsConditionalAsBefore()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);

        await UploadAsync(fs, "/big.bin", "hello world", overwrite: false);

        fake.Calls.Single(c => c.Q("comp") == "blocklist").Header("If-None-Match").Should().Be("*");
    }

    // ── The type ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/page.html", "text/html")]
    [InlineData("/dir/PHOTO.JPG", "image/jpeg")]
    [InlineData("/data.unknownext", "application/octet-stream")]
    [InlineData("/no-extension", "application/octet-stream")]
    public async Task OpenWriteAsync_SmallFile_StoresTheTypeOfItsName(string path, string expected)
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);

        await UploadAsync(fs, path, "hello");

        fake.Calls.Single(c => c.Method == "PUT").Header("x-ms-blob-content-type").Should().Be(expected);
    }

    [Fact]
    public async Task OpenWriteAsync_LargeFile_StatesTheTypeAtTheCommit()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);

        await UploadAsync(fs, "/movie.mp4", "hello world");

        fake.Calls.Single(c => c.Q("comp") == "blocklist").Header("x-ms-blob-content-type").Should().Be("video/mp4");
        fake.Calls.Where(c => c.Q("comp") == "block").Should().OnlyContain(c => c.Header("x-ms-blob-content-type") == null, "a block has no type of its own");
    }

    [Fact]
    public async Task OpenWriteAsync_TypeHeader_IsCoveredByTheSignature()
    {
        var fake = new FakeBlobService().WithContainer("c");
        using var fs = Create(fake);
        string? signed = null;
        fake.BeforeAnswer = (request, _) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                signed = AzureSharedKey.StringToSign(request, "myaccount");
            }

            return Task.CompletedTask;
        };

        await UploadAsync(fs, "/page.html", "hello");

        signed.Should().Contain("x-ms-blob-content-type:text/html\n");
    }

    /// <summary>Loses the line once while a block is on its way: the bytes read so far are gone with it.</summary>
    private sealed class DropsBlockOnce(HttpMessageHandler inner, int block, int afterBytes) : DelegatingHandler(inner)
    {
        private int _dropped;

        public bool Dropped => _dropped == 1;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (IsBlock(request, block) && Interlocked.Exchange(ref _dropped, 1) == 0)
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
