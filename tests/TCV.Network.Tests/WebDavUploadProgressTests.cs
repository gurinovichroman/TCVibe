using System.Net;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// WebDAV uploads report the bytes really sent: the body is collected in a temporary file first, and
/// the bar used to reach 100 % on that file before the PUT — the slow part — had even started.
/// </summary>
public sealed class WebDavUploadProgressTests
{
    private const int BodySize = 300_000; // several 80 KB chunks

    private static byte[] Payload(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>A server that reads PUT bodies chunk by chunk, so a test can see how much has arrived.</summary>
    private sealed class BodyReadingHandler(byte[]? download = null) : HttpMessageHandler
    {
        private readonly MemoryStream _receiving = new();

        public int PutsStarted { get; private set; }

        public int PutsCompleted { get; private set; }

        public byte[] LastBody { get; private set; } = [];

        /// <summary>Bytes of the PUT body the server holds right now.</summary>
        public long Received => _receiving.Length;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get && download is not null)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(download) };
            }

            PutsStarted++;
            _receiving.SetLength(0);
            await request.Content!.CopyToAsync(_receiving, ct);
            LastBody = _receiving.ToArray();
            PutsCompleted++;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }
    }

    private static WebDavFileSystem Create(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://dav.example") });

    // ── The body ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ProgressStreamContent_ReadByTheServer_ReachesTheTotalOnlyOnceTheWholeBodyArrived()
    {
        var body = Payload(BodySize);
        using var source = new MemoryStream(body);
        var handler = new BodyReadingHandler();
        var reports = new List<(long Sent, long ReceivedThen)>();
        using var content = new ProgressStreamContent(source, new InlineProgress<long>(sent => reports.Add((sent, handler.Received))));
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://dav.example") };

        using var response = await client.PutAsync("/f.bin", content);

        handler.LastBody.Should().Equal(body);
        reports.Should().HaveCountGreaterThan(1, "a large body is reported chunk by chunk, not once at the end");
        reports.Select(r => r.Sent).Should().BeInAscendingOrder();
        reports.Should().OnlyContain(r => r.Sent <= r.ReceivedThen, "progress never runs ahead of what the server has");
        reports[^1].Sent.Should().Be(body.Length);
        reports.Where(r => r.Sent == body.Length).Should().OnlyContain(r => r.ReceivedThen == body.Length);
    }

    [Fact]
    public async Task ProgressStreamContent_SentTwice_StartsAgainFromWhereTheStreamWas()
    {
        // The handler sends the body a second time after an authentication challenge.
        var body = Payload(BodySize);
        using var source = new MemoryStream(body) { Position = 1000 };
        var reports = new List<long>();
        using var content = new ProgressStreamContent(source, new InlineProgress<long>(reports.Add));
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        await content.CopyToAsync(first);
        await content.CopyToAsync(second);

        content.Headers.ContentLength.Should().Be(body.Length - 1000);
        second.ToArray().Should().Equal(body[1000..]);
        reports.Count(r => r == body.Length - 1000).Should().Be(2);
    }

    // ── The write stream ─────────────────────────────────────────────────────

    [Fact]
    public async Task CommitAsync_WithProgress_FollowsTheBytesTheServerReceivedAndSendsOnce()
    {
        var body = Payload(BodySize);
        var handler = new BodyReadingHandler();
        var fs = Create(handler);
        var reports = new List<(long Sent, long ReceivedThen)>();

        var output = await fs.OpenWriteAsync(new VfsPath("/up.bin"), overwrite: true);
        await output.WriteAsync(body);
        handler.PutsStarted.Should().Be(0, "writing only fills the temporary file");

        await ((IDeferredUploadStream)output).CommitAsync(
            new InlineProgress<long>(sent => reports.Add((sent, handler.Received))), CancellationToken.None);
        await output.DisposeAsync();

        handler.PutsStarted.Should().Be(1, "disposing a committed stream must not send it again");
        handler.LastBody.Should().Equal(body);
        reports.Should().HaveCountGreaterThan(1);
        reports.Should().OnlyContain(r => r.Sent <= r.ReceivedThen);
        reports[^1].Sent.Should().Be(body.Length);
    }

    [Fact]
    public async Task CommitAsync_CancelledMidUpload_ThrowsAndRemovesTheTemporaryFile()
    {
        var handler = new BodyReadingHandler();
        var fs = Create(handler);
        using var cts = new CancellationTokenSource();

        var output = (WebDavFileSystem.WebDavUploadStream)await fs.OpenWriteAsync(new VfsPath("/up.bin"), overwrite: true);
        await output.WriteAsync(Payload(BodySize));
        var temporary = output.BufferPath;
        File.Exists(temporary).Should().BeTrue();

        var act = () => output.CommitAsync(new InlineProgress<long>(_ => cts.Cancel()), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(temporary).Should().BeFalse("a cancelled upload must not leave its body in %TEMP%");
        await output.DisposeAsync();
        handler.PutsStarted.Should().Be(1, "disposing after a failed commit must not try again");
        handler.PutsCompleted.Should().Be(0);
    }

    [Fact]
    public async Task CommitAsync_AfterCancellation_RefusesInsteadOfReportingSuccess()
    {
        // Disposing a cancelled stream quietly sends nothing; an explicit commit must not look like it worked.
        var handler = new BodyReadingHandler();
        var fs = Create(handler);
        using var cts = new CancellationTokenSource();

        var output = await fs.OpenWriteAsync(new VfsPath("/up.bin"), true, cts.Token);
        await output.WriteAsync(new byte[] { 1, 2, 3 });
        cts.Cancel();

        var act = () => ((IDeferredUploadStream)output).CommitAsync(null, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.PutsStarted.Should().Be(0);
    }

    // ── Copy inside one server ───────────────────────────────────────────────

    [Fact]
    public async Task CopyAsync_CountsTheDownloadAsHalfAndTheUploadAsTheOtherHalf()
    {
        var body = Payload(BodySize);
        var handler = new BodyReadingHandler(download: body);
        var fs = Create(handler);
        var reports = new List<(long Shown, long ReceivedThen, int PutsStarted)>();

        await fs.CopyAsync(new VfsPath("/a.bin"), new VfsPath("/b.bin"),
            new InlineProgress<FileProgress>(p => reports.Add((p.BytesCopied, handler.Received, handler.PutsStarted))),
            CancellationToken.None);

        handler.LastBody.Should().Equal(body);
        reports.Where(r => r.PutsStarted == 0).Should().OnlyContain(r => r.Shown <= body.Length / 2,
            "collecting the body into the temporary file is only the first half");
        reports.Should().Contain(r => r.Shown > body.Length / 2 && r.Shown < body.Length,
            "the bar keeps moving while the PUT is under way");
        reports.Where(r => r.Shown == body.Length).Should().OnlyContain(r => r.ReceivedThen == body.Length);
        reports[^1].Shown.Should().Be(body.Length);
    }
}
