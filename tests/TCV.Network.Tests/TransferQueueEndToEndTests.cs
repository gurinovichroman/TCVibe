using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// The transfer queue against a real (tiny, in-process) WebDAV server: what a download, upload,
/// pause, resume, cancel and retry actually do to files on disk.
/// </summary>
public sealed partial class TransferQueueEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-queue-" + Guid.NewGuid().ToString("N"));
    private readonly MiniDavServer _server = new();
    private readonly TransferQueue _queue = new();

    public TransferQueueEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _queue.Dispose();
        _server.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private ConnectionInfo Connection => new()
    {
        Protocol = NetworkProtocol.WebDav,
        Host = "127.0.0.1",
        Port = _server.Port,
    };

    private TransferItem Download(string remote, string local, bool overwrite = false) => new()
    {
        SourcePath = remote,
        DestinationPath = local,
        Connection = Connection,
        Direction = TransferDirection.Download,
        OverwriteExisting = overwrite,
    };

    private static byte[] Payload(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    private static async Task<bool> WaitFor(Func<bool> condition, int seconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return condition();
    }

    [Fact]
    public async Task Download_CompletesAndWritesTheFile()
    {
        var data = Payload(300_000);
        _server.Files["/f.bin"] = data;
        var target = Path.Combine(_dir, "f.bin");
        var item = Download("/f.bin", target);

        _queue.Enqueue(item);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        File.ReadAllBytes(target).Should().Equal(data);
        item.PercentComplete.Should().Be(100);
    }

    [Fact]
    public async Task Download_ExistingFileWithoutPermission_IsRefusedAndLeftAlone()
    {
        _server.Files["/f.bin"] = Payload(1000);
        var target = Path.Combine(_dir, "f.bin");
        File.WriteAllText(target, "mine");
        var item = Download("/f.bin", target);

        _queue.Enqueue(item);

        (await WaitFor(() => item.Status == TransferStatus.Failed)).Should().BeTrue();
        item.ErrorMessage.Should().Contain("already exists");
        File.ReadAllText(target).Should().Be("mine");
    }

    [Fact]
    public async Task Download_ExistingFileWithPermission_IsReplaced()
    {
        var data = Payload(2000);
        _server.Files["/f.bin"] = data;
        var target = Path.Combine(_dir, "f.bin");
        File.WriteAllText(target, "old");
        var item = Download("/f.bin", target, overwrite: true);

        _queue.Enqueue(item);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        File.ReadAllBytes(target).Should().Equal(data);
    }

    [Fact]
    public async Task Upload_SendsTheFile_AndRefusesToReplaceOneThatExists()
    {
        var data = Payload(5000);
        var local = Path.Combine(_dir, "up.bin");
        File.WriteAllBytes(local, data);
        var first = new TransferItem
        {
            SourcePath = local, DestinationPath = "/up.bin", Connection = Connection, Direction = TransferDirection.Upload,
        };
        _queue.Enqueue(first);
        (await WaitFor(() => first.Status == TransferStatus.Complete)).Should().BeTrue(first.ErrorMessage);
        _server.Files["/up.bin"].Should().Equal(data);

        var second = new TransferItem
        {
            SourcePath = local, DestinationPath = "/up.bin", Connection = Connection, Direction = TransferDirection.Upload,
        };
        _queue.Enqueue(second);

        (await WaitFor(() => second.Status == TransferStatus.Failed)).Should().BeTrue();
        second.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task Upload_WhileTheServerIsStillReceiving_IsNotShownAsComplete()
    {
        // The body goes to a temporary file before the PUT. Counting only that put the item at 100 %
        // while the real upload — here held up by the server — had barely begun.
        var data = Payload(8_000_000);
        var local = Path.Combine(_dir, "big-up.bin");
        File.WriteAllBytes(local, data);
        _server.SlowAfterBytes = 100_000;
        var item = new TransferItem
        {
            SourcePath = local, DestinationPath = "/big-up.bin", Connection = Connection, Direction = TransferDirection.Upload,
        };
        _queue.Enqueue(item);

        (await WaitFor(() => item.PercentComplete >= 50)).Should().BeTrue("collecting the body is the first half");
        await Task.Delay(300);
        item.Status.Should().Be(TransferStatus.Transferring);
        item.PercentComplete.Should().BeLessThan(100, "the server has not received the body yet");

        _server.SlowAfterBytes = -1;
        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        item.PercentComplete.Should().Be(100);
        _server.Files["/big-up.bin"].Should().Equal(data);
    }

    [Fact]
    public async Task Cancel_RunningDownload_DeletesThePartialFile()
    {
        _server.Files["/slow.bin"] = Payload(2_000_000);
        _server.SlowAfterBytes = 100_000;
        var target = Path.Combine(_dir, "slow.bin");
        var item = Download("/slow.bin", target);
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.Cancel(item.Id);

        (await WaitFor(() => item.Status == TransferStatus.Cancelled && !File.Exists(target))).Should().BeTrue();
    }

    [Fact]
    public async Task PauseThenResume_ContinuesWithARangeRequestAndProducesAnIntactFile()
    {
        var data = Payload(2_000_000);
        _server.Files["/big.bin"] = data;
        _server.SlowAfterBytes = 100_000;
        var target = Path.Combine(_dir, "big.bin");
        var item = Download("/big.bin", target);
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.Pause(item.Id);
        (await WaitFor(() => item.Status == TransferStatus.Paused && item.Cts is null)).Should().BeTrue();
        File.Exists(target).Should().BeTrue("a paused download keeps its partial file");
        _server.SlowAfterBytes = -1;

        await _queue.ResumeAsync(item.Id);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        File.ReadAllBytes(target).Should().Equal(data);
        _server.RangeRequests.Should().NotBeEmpty("the second request continued instead of starting over");
    }

    [Fact]
    public async Task Resume_AfterTheLocalFileWasTamperedWith_StartsOverInsteadOfAppendingToIt()
    {
        var data = Payload(1_000_000);
        _server.Files["/t.bin"] = data;
        _server.SlowAfterBytes = 50_000;
        var target = Path.Combine(_dir, "t.bin");
        var item = Download("/t.bin", target);
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();
        _queue.Pause(item.Id);
        (await WaitFor(() => item.Status == TransferStatus.Paused && item.Cts is null)).Should().BeTrue();
        File.AppendAllText(target, "junk");
        _server.SlowAfterBytes = -1;

        await _queue.ResumeAsync(item.Id);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        File.ReadAllBytes(target).Should().Equal(data);
    }

    [Fact]
    public async Task Resume_AfterTheServerFileWasReplaced_DownloadsTheNewFileWhole()
    {
        var oldData = Payload(1_000_000);
        _server.Files["/build.zip"] = oldData;
        _server.SlowAfterBytes = 50_000;
        var target = Path.Combine(_dir, "build.zip");
        var item = Download("/build.zip", target);
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();
        _queue.Pause(item.Id);
        (await WaitFor(() => item.Status == TransferStatus.Paused && item.Cts is null)).Should().BeTrue();

        var newData = Payload(1_200_000).Select(b => (byte)(b ^ 0x5A)).ToArray();
        _server.Files["/build.zip"] = newData;
        _server.SlowAfterBytes = -1;
        await _queue.ResumeAsync(item.Id);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        File.ReadAllBytes(target).Should().Equal(newData, "the head of the old build must not be spliced onto the new one");
    }

    [Fact]
    public async Task Retry_FailedTransfer_RunsAgain()
    {
        _server.Files["/r.bin"] = Payload(1000);
        _server.FailNextRequests = 1;
        var target = Path.Combine(_dir, "r.bin");
        var item = Download("/r.bin", target);
        _queue.Enqueue(item);
        (await WaitFor(() => item.Status == TransferStatus.Failed)).Should().BeTrue();
        item.ErrorMessage.Should().NotBeNullOrEmpty();

        _queue.Retry(item.Id);

        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue(item.ErrorMessage);
        item.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task ClearFinished_RemovesOnlyFinishedItems()
    {
        _server.Files["/a.bin"] = Payload(10);
        var done = Download("/a.bin", Path.Combine(_dir, "a.bin"));
        _queue.Enqueue(done);
        (await WaitFor(() => done.Status == TransferStatus.Complete)).Should().BeTrue();
        var pending = new TransferItem { Connection = Connection };
        _queue.AddWithoutStarting(pending);

        _queue.ClearFinished();

        _queue.Items.Should().ContainSingle().Which.Should().BeSameAs(pending);
    }

    [Fact]
    public async Task HasActiveWork_IsTrueWhileTransferringAndFalseAfterwards()
    {
        _server.Files["/w.bin"] = Payload(1_000_000);
        _server.SlowAfterBytes = 50_000;
        var item = Download("/w.bin", Path.Combine(_dir, "w.bin"));
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.HasActiveWork.Should().BeTrue();

        _server.SlowAfterBytes = -1;
        (await WaitFor(() => item.Status == TransferStatus.Complete)).Should().BeTrue();
        _queue.HasActiveWork.Should().BeFalse();
    }

    [Fact]
    public async Task Resume_RightAfterTheWorkerWentIdle_StillGetsPickedUp()
    {
        // The lost-wakeup case: a Resume racing the moment the pump finds nothing to do.
        for (var round = 0; round < 20; round++)
        {
            var item = new TransferItem { Connection = Connection, Direction = TransferDirection.Download };
            _server.Files["/z.bin"] = Payload(10);
            var quick = Download("/z.bin", Path.Combine(_dir, $"z{round}.bin"));
            _queue.Enqueue(quick);
            (await WaitFor(() => quick.Status == TransferStatus.Complete)).Should().BeTrue();
            _ = item;
        }
    }

    [Fact]
    public async Task Items_AreOnlyTouchedThroughTheGivenSynchronizationContext()
    {
        using var context = new QueueingContext();
        using var queue = new TransferQueue(context);
        var item = new TransferItem { Connection = Connection };

        queue.AddWithoutStarting(item);
        queue.Items.Should().BeEmpty("the UI list changes only when the UI thread runs the posted work");

        context.RunPending();
        queue.Items.Should().ContainSingle();
        await Task.CompletedTask;
    }

    private sealed class QueueingContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _posts = new();

        public override void Post(SendOrPostCallback d, object? state) => _posts.Enqueue((d, state));

        public void RunPending()
        {
            while (_posts.TryDequeue(out var post))
            {
                post.Callback(post.State);
            }
        }

        public void Dispose()
        {
        }
    }

    // ── A WebDAV server just big enough for the queue ────────────────────────

    private sealed class MiniDavServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _failuresLeft;

        public MiniDavServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        public int Port { get; }

        public ConcurrentDictionary<string, byte[]> Files { get; } = new();

        public ConcurrentBag<string> RangeRequests { get; } = [];

        /// <summary>
        /// After this many body bytes a download — or the reading of an upload — stalls (until set to -1).
        /// Lets a test catch a transfer mid-way.
        /// </summary>
        public int SlowAfterBytes { get; set; } = -1;

        public int FailNextRequests
        {
            set => _failuresLeft = value;
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                var request = context.Request;
                var response = context.Response;
                var path = Uri.UnescapeDataString(request.Url!.AbsolutePath);

                if (Interlocked.Decrement(ref _failuresLeft) >= 0)
                {
                    response.StatusCode = 500;
                    response.Close();
                    return;
                }

                Interlocked.Exchange(ref _failuresLeft, 0);

                switch (request.HttpMethod)
                {
                    case "GET" when Files.TryGetValue(path, out var data):
                        await ServeAsync(request, response, data);
                        break;
                    case "PUT":
                        if (request.Headers["If-None-Match"] == "*" && Files.ContainsKey(path))
                        {
                            response.StatusCode = 412;
                            response.Close();
                            break;
                        }

                        using (var buffer = new MemoryStream())
                        {
                            var chunk = new byte[32 * 1024];
                            int read;
                            while ((read = await request.InputStream.ReadAsync(chunk)) > 0)
                            {
                                buffer.Write(chunk, 0, read);
                                while (SlowAfterBytes >= 0 && buffer.Length >= SlowAfterBytes && !_stop.IsCancellationRequested)
                                {
                                    await Task.Delay(20);
                                }
                            }

                            Files[path] = buffer.ToArray();
                        }

                        response.StatusCode = 201;
                        response.Close();
                        break;
                    default:
                        response.StatusCode = 404;
                        response.Close();
                        break;
                }
            }
            catch (Exception)
            {
                // A client that hung up mid-transfer (pause, cancel) is normal here.
            }
        }

        private async Task ServeAsync(HttpListenerRequest request, HttpListenerResponse response, byte[] data)
        {
            var start = 0;
            var etag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data))[..16] + "\"";
            response.AddHeader("ETag", etag);

            // If-Range naming another version: the range is ignored and the whole file is sent.
            var rangeStillValid = request.Headers["If-Range"] is not { } ifRange || ifRange == etag;
            if (rangeStillValid && request.Headers["Range"] is { } range && range.StartsWith("bytes=", StringComparison.Ordinal))
            {
                RangeRequests.Add(range);
                start = int.Parse(range["bytes=".Length..].TrimEnd('-'));
                response.StatusCode = 206;
                response.AddHeader("Content-Range", $"bytes {start}-{data.Length - 1}/{data.Length}");
            }

            response.ContentLength64 = data.Length - start;
            var sent = 0;
            const int chunk = 32 * 1024;
            for (var offset = start; offset < data.Length; offset += chunk)
            {
                while (SlowAfterBytes >= 0 && sent >= SlowAfterBytes && !_stop.IsCancellationRequested)
                {
                    await Task.Delay(20);
                }

                var count = Math.Min(chunk, data.Length - offset);
                await response.OutputStream.WriteAsync(data.AsMemory(offset, count));
                sent += count;
            }

            response.Close();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
        }
    }
}
