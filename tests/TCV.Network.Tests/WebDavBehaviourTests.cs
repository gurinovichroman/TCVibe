using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>What <see cref="WebDavFileSystem"/> puts on the wire and how it reacts to answers.</summary>
public sealed class WebDavBehaviourTests
{
    private sealed record Recorded(HttpMethod Method, Uri Uri, HttpRequestHeaders Headers, byte[] Body);

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Recorded> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Requests.Add(new Recorded(request.Method, request.RequestUri!, request.Headers, body));
            return respond(request);
        }
    }

    private static WebDavFileSystem Create(ScriptedHandler handler, TimeSpan? timeout = null)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://dav.example") };
        return new WebDavFileSystem(client) { MetadataTimeout = timeout ?? TimeSpan.FromSeconds(30) };
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    // ── Uploads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenWriteAsync_DisposedNormally_SendsOnePutWithTheWholeBody()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.Created));
        var fs = Create(handler);

        await using (var output = await fs.OpenWriteAsync(new VfsPath("/a b.txt"), overwrite: true))
        {
            await output.WriteAsync(Encoding.UTF8.GetBytes("hello"));
        }

        var put = handler.Requests.Should().ContainSingle().Subject;
        put.Method.Should().Be(HttpMethod.Put);
        put.Uri.AbsolutePath.Should().Be("/a%20b.txt");
        Encoding.UTF8.GetString(put.Body).Should().Be("hello");
    }

    [Fact]
    public async Task OpenWriteAsync_Aborted_NeverSendsThePartialBody()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.Created));
        var fs = Create(handler);

        var output = await fs.OpenWriteAsync(new VfsPath("/half.bin"), overwrite: true);
        await output.WriteAsync(new byte[] { 1, 2, 3 });
        ((IAbortableWriteStream)output).Abort();
        await output.DisposeAsync();

        handler.Requests.Should().BeEmpty("a half-written body must not be published as the file");
    }

    [Fact]
    public async Task OpenWriteAsync_CancelledToken_NeverSendsAnything()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.Created));
        var fs = Create(handler);
        using var cts = new CancellationTokenSource();

        var output = await fs.OpenWriteAsync(new VfsPath("/x"), true, cts.Token);
        await output.WriteAsync(new byte[] { 1 });
        cts.Cancel();
        await output.DisposeAsync();

        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenWriteAsync_WithoutOverwrite_AsksTheServerToRefuseAnExistingFile()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.PreconditionFailed));
        var fs = Create(handler);

        var act = async () =>
        {
            await using var output = await fs.OpenWriteAsync(new VfsPath("/exists.txt"), overwrite: false);
            await output.WriteAsync(new byte[] { 1 });
        };

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        handler.Requests.Single().Headers.GetValues("If-None-Match").Should().Contain("*");
    }

    [Fact]
    public async Task CopyAsync_SourceFailsMidStream_DoesNotPublishTheTruncatedFile()
    {
        var handler = new ScriptedHandler(request => request.Method == HttpMethod.Get
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new FailingStream()) }
            : Status(HttpStatusCode.Created));
        var fs = Create(handler);

        var act = () => fs.CopyAsync(new VfsPath("/src.bin"), new VfsPath("/dst.bin"), null, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        handler.Requests.Should().NotContain(r => r.Method == HttpMethod.Put);
    }

    private sealed class FailingStream : Stream
    {
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_reads++ == 0)
            {
                buffer[offset] = 1;
                return 1;
            }

            throw new IOException("connection reset");
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_reads++ == 0)
            {
                buffer.Span[0] = 1;
                return ValueTask.FromResult(1);
            }

            throw new IOException("connection reset");
        }
    }

    // ── Move and rename ──────────────────────────────────────────────────────

    [Fact]
    public async Task MoveAsync_DestinationHeader_KeepsPercentEncoding()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.Created));
        var fs = Create(handler);

        await fs.MoveAsync(new VfsPath("/a.txt"), new VfsPath("/new folder/b c.txt"), CancellationToken.None);

        var destination = handler.Requests.Single().Headers.GetValues("Destination").Single();
        destination.Should().Be("http://dav.example/new%20folder/b%20c.txt", "ToString() would have un-escaped it");
    }

    [Fact]
    public async Task RenameAsync_NeverOverwritesAnExistingEntry()
    {
        var handler = new ScriptedHandler(_ => Status(HttpStatusCode.PreconditionFailed));
        var fs = Create(handler);

        var act = () => fs.RenameAsync(new VfsPath("/a.txt"), "b.txt", CancellationToken.None);

        await act.Should().ThrowAsync<IOException>().WithMessage("*already exists*");
        handler.Requests.Single().Headers.GetValues("Overwrite").Should().Contain("F");
    }

    // ── Answers ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetEntryAsync_NotFound_IsNullButALoginFailureIsNotAnAnswer()
    {
        var missing = Create(new ScriptedHandler(_ => Status(HttpStatusCode.NotFound)));
        var denied = Create(new ScriptedHandler(_ => Status(HttpStatusCode.Unauthorized)));

        (await missing.GetEntryAsync(new VfsPath("/x"))).Should().BeNull();
        var act = () => denied.GetEntryAsync(new VfsPath("/x"));
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task ListAsync_ServerThatNeverAnswers_TimesOutWithAClearMessage()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("unused"));
        var hanging = new HangingHandler();
        var fs = new WebDavFileSystem(new HttpClient(hanging) { BaseAddress = new Uri("http://dav.example") })
        {
            MetadataTimeout = TimeSpan.FromMilliseconds(150),
        };
        _ = handler;

        var act = () => fs.ListAsync(new VfsPath("/"));

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("*did not answer*");
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        }
    }

    [Fact]
    public async Task OpenReadFromAsync_ServerHonoursRange_ContinuesAtTheOffset()
    {
        var handler = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([7, 8, 9]),
            };
            return response;
        });
        var fs = Create(handler);

        var (stream, offset, total) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 100, CancellationToken.None);
        await stream.DisposeAsync();

        offset.Should().Be(100);
        total.Should().Be(103);
        handler.Requests.Single().Headers.Range!.Ranges.Single().From.Should().Be(100);
    }

    [Fact]
    public async Task OpenReadFromAsync_ServerIgnoresRange_RestartsFromTheBeginning()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[200]),
        });
        var fs = Create(handler);

        var (stream, offset, total) = await fs.OpenReadFromAsync(new VfsPath("/big.bin"), 100, CancellationToken.None);
        await stream.DisposeAsync();

        offset.Should().Be(0, "the body is the whole file, so appending it after 100 bytes would corrupt it");
        total.Should().Be(200);
    }

    // ── Listing hrefs ────────────────────────────────────────────────────────

    [Fact]
    public void ParseMultiStatus_AbsoluteHrefWithEscapedSpace_IsDecodedExactlyOnce()
    {
        const string xml = """
            <D:multistatus xmlns:D="DAV:">
              <D:response><D:href>http://h/base/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop></D:propstat></D:response>
              <D:response><D:href>http://h/base/my%20file%2520x.txt</D:href><D:propstat><D:prop><D:getcontentlength>3</D:getcontentlength></D:prop></D:propstat></D:response>
            </D:multistatus>
            """;

        var items = WebDavResponse.ParseMultiStatus(xml, "/base");

        items.Should().ContainSingle().Which.Name.Should().Be("my file%20x.txt");
    }
}
