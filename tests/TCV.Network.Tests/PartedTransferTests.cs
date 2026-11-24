using System.Net;
using System.Net.Http;
using FluentAssertions;

namespace TCV.Network.Tests;

/// <summary>The pauses a test can live with.</summary>
internal static class TestRetries
{
    public static ReconnectPolicy NoPauses { get; } = new([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
}

/// <summary><see cref="PartedTransfer"/>: how many parts at once, what is tried again, how a failure comes out.</summary>
public sealed class PartedTransferTests
{
    private static readonly HttpRequestException Dropped = new("The connection was reset.");

    // ── Side by side ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ManyParts_RunsSideBySideWithinTheLimitAndAnswersInPartOrder()
    {
        var inFlight = 0;
        var peak = 0;
        var twoIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var results = await PartedTransfer.RunAsync(12, 3, TestRetries.NoPauses, async (index, ct) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            if (now >= 2)
            {
                twoIn.TrySetResult();
            }

            // A part alone waits for a second one here: sent one at a time, this would time out.
            await twoIn.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            // The later the part, the sooner it finishes.
            await Task.Delay((12 - index) * 3, ct);
            Interlocked.Decrement(ref inFlight);
            return index * 10;
        }, CancellationToken.None);

        results.Should().Equal(0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100, 110);
        peak.Should().BeInRange(2, 3);
    }

    [Fact]
    public async Task RunAsync_OnePartIsRefused_ReportsThatFailureAndWaitsForTheOthers()
    {
        var refused = new S3Exception("Access denied", HttpStatusCode.Forbidden, "AccessDenied");
        var finished = 0;

        var act = () => PartedTransfer.RunAsync(4, 4, TestRetries.NoPauses, async (index, ct) =>
        {
            try
            {
                if (index == 2)
                {
                    await Task.Delay(10, CancellationToken.None);
                    throw refused;
                }

                await Task.Delay(Timeout.Infinite, ct);
                return index;
            }
            finally
            {
                Interlocked.Increment(ref finished);
            }
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<S3Exception>()).Which.Should().BeSameAs(refused, "not the cancellation of the parts it stopped");
        finished.Should().Be(4, "nothing may still be in flight when the caller aborts the upload");
    }

    // ── What is tried again ──────────────────────────────────────────────────

    [Fact]
    public async Task WithRetriesAsync_ADroppedConnection_IsTriedAgainAndThenSucceeds()
    {
        var tries = 0;

        var result = await PartedTransfer.WithRetriesAsync(TestRetries.NoPauses, _ =>
            ++tries < 3 ? throw Dropped : Task.FromResult("done"), CancellationToken.None);

        result.Should().Be("done");
        tries.Should().Be(3);
    }

    [Fact]
    public async Task WithRetriesAsync_TriesUsedUp_ThrowsTheLastFailure()
    {
        var tries = 0;

        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, _ =>
        {
            tries++;
            throw Dropped;
        }, CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
        tries.Should().Be(4, "one try and the three new ones the policy has pauses for");
    }

    public static TheoryData<Exception> Refusals => new()
    {
        new S3Exception("Access denied", HttpStatusCode.Forbidden, "AccessDenied"),
        new S3Exception("Not implemented", HttpStatusCode.NotImplemented, "NotImplemented"),
        new AzureStorageException("No such container", HttpStatusCode.NotFound, "ContainerNotFound"),
        new InvalidOperationException("Something nobody knows about"),
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task WithRetriesAsync_ARefusal_IsNotTriedAgain(Exception refusal)
    {
        var tries = 0;

        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, _ =>
        {
            tries++;
            throw refusal;
        }, CancellationToken.None);

        (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(refusal);
        tries.Should().Be(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task WithRetriesAsync_AServerSayingLater_IsTriedAgain(HttpStatusCode status)
    {
        var tries = 0;

        await PartedTransfer.WithRetriesAsync(TestRetries.NoPauses, _ =>
            ++tries == 1 ? throw new S3Exception("Later", status, "SlowDown") : Task.FromResult(true), CancellationToken.None);

        tries.Should().Be(2);
    }

    [Fact]
    public async Task WithRetriesAsync_S3SayingTheBodyStoppedArriving_IsTriedAgain()
    {
        // 400, a client error by its status: "your socket connection was not read from or written to".
        var tries = 0;

        await PartedTransfer.WithRetriesAsync(TestRetries.NoPauses, _ =>
            ++tries == 1
                ? throw new S3Exception("Timed out", HttpStatusCode.BadRequest, "RequestTimeout")
                : Task.FromResult(true), CancellationToken.None);

        tries.Should().Be(2);
    }

    [Fact]
    public async Task WithRetriesAsync_NameTaken_IsNotTriedAgain()
    {
        var tries = 0;

        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, _ =>
        {
            tries++;
            throw new ObjectAlreadyExistsException("\"a\" already exists.");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        tries.Should().Be(1);
    }

    // ── The stall limit ──────────────────────────────────────────────────────

    [Fact]
    public async Task WithStallLimitAsync_ATryThatNeverAnswers_IsTriedAgain()
    {
        var tries = 0;

        var result = await PartedTransfer.WithRetriesAsync(TestRetries.NoPauses, attempt =>
            PartedTransfer.WithStallLimitAsync(TimeSpan.FromMilliseconds(50), async watch =>
            {
                if (++tries == 1)
                {
                    await Task.Delay(Timeout.Infinite, watch.Token);
                }

                return tries;
            }, attempt), CancellationToken.None);

        result.Should().Be(2);
    }

    [Fact]
    public async Task WithStallLimitAsync_TriesUsedUp_SaysItWasATimeoutAndNotACancel()
    {
        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, attempt =>
            PartedTransfer.WithStallLimitAsync<int>(TimeSpan.FromMilliseconds(10), async watch =>
            {
                await Task.Delay(Timeout.Infinite, watch.Token);
                return 0;
            }, attempt), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task Alive_StartsTheLimitOver()
    {
        // No timer until it is said: the only thing that can cancel this token is what Alive arms.
        using var timeout = new CancellationTokenSource();
        var watch = new PartWatch(timeout, TimeSpan.FromMilliseconds(1));

        watch.Alive();

        await Task.Delay(Timeout.Infinite, watch.Token).ContinueWith(_ => { }).WaitAsync(TimeSpan.FromSeconds(30));
        watch.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task FileSegmentStream_EveryRead_IsASignOfLife()
    {
        var path = Path.Combine(Path.GetTempPath(), "tcv-segment-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllBytesAsync(path, new byte[100]);
        try
        {
            using var file = File.OpenHandle(path, options: FileOptions.Asynchronous);
            var signs = 0;
            await using var segment = new FileSegmentStream(file, 10, 80, () => signs++);

            await segment.ReadExactlyAsync(new byte[30]);
            signs.Should().Be(1);
            segment.ReadExactly(new byte[30]);
            signs.Should().Be(2);

            var buffer = new byte[100];
            (await segment.ReadAsync(buffer)).Should().Be(20);
            (await segment.ReadAsync(buffer)).Should().Be(0, "the end of the part");
            signs.Should().Be(3, "nothing was read at the end");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task WithStallLimitAsync_CancelledFromOutside_IsACancel()
    {
        using var cts = new CancellationTokenSource();

        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, attempt =>
            PartedTransfer.WithStallLimitAsync<int>(TimeSpan.FromMinutes(1), async watch =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, watch.Token);
                return 0;
            }, attempt), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Alive_AfterTheTryEnded_IsNothing()
    {
        var timeout = new CancellationTokenSource();
        var watch = new PartWatch(timeout, TimeSpan.FromSeconds(1));
        timeout.Dispose();

        var act = watch.Alive;

        act.Should().NotThrow();
    }

    // ── Cancellation ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WithRetriesAsync_CancelledDuringATry_StopsAtOnce()
    {
        using var cts = new CancellationTokenSource();
        var tries = 0;

        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, ct =>
        {
            tries++;
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(1);
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        tries.Should().Be(1);
    }

    [Fact]
    public async Task WithRetriesAsync_CancelledAndTheLineDroppedToo_IsNotTriedAgain()
    {
        using var cts = new CancellationTokenSource();
        var tries = 0;

        // What cancelling an open request can look like: a broken socket, not a cancellation.
        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, _ =>
        {
            tries++;
            cts.Cancel();
            throw Dropped;
        }, cts.Token);

        await act.Should().ThrowAsync<HttpRequestException>();
        tries.Should().Be(1);
    }

    [Fact]
    public async Task WithRetriesAsync_AlreadyCancelled_DoesNotTry()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var tries = 0;

        var act = () => PartedTransfer.WithRetriesAsync(TestRetries.NoPauses, _ =>
        {
            tries++;
            return Task.FromResult(1);
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        tries.Should().Be(0);
    }

    [Fact]
    public async Task WithRetriesAsync_CancelledDuringThePause_DoesNotWaitItOut()
    {
        using var cts = new CancellationTokenSource();
        var tries = 0;
        var longPause = new ReconnectPolicy([TimeSpan.FromMinutes(5)]);

        var act = () => PartedTransfer.WithRetriesAsync<int>(longPause, _ =>
        {
            tries++;
            cts.CancelAfter(10);
            throw Dropped;
        }, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        tries.Should().Be(1);
    }

    [Fact]
    public async Task WithRetriesAsync_ATimeoutWrappedInCancellation_IsAFailureAndNotACancellation()
    {
        // HttpClient's own timeout: TaskCanceledException around a TimeoutException, with nobody having cancelled (TRAP-93).
        var tries = 0;

        var act = () => PartedTransfer.WithRetriesAsync<int>(TestRetries.NoPauses, _ =>
        {
            tries++;
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.", new TimeoutException());
        }, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
        tries.Should().Be(4, "a timeout is a failure of the line like any other");
    }

    // ── Progress ─────────────────────────────────────────────────────────────

    [Fact]
    public void PartProgress_PartsTogether_ReportTheirSum()
    {
        var reported = new List<long>();
        var progress = new PartProgress(new InlineProgress<long>(reported.Add), 2);

        progress.For(0)!.Report(3);
        progress.For(1)!.Report(5);
        progress.For(0)!.Report(4);

        reported.Should().Equal(3, 8, 9);
    }

    [Fact]
    public void PartProgress_APartSentAgain_DoesNotPutTheTotalBack()
    {
        var reported = new List<long>();
        var progress = new PartProgress(new InlineProgress<long>(reported.Add), 2);

        progress.For(0)!.Report(4);
        progress.For(1)!.Report(4);
        progress.For(0)!.Report(1); // the first part starts over
        progress.For(0)!.Report(2);
        progress.For(0)!.Report(4);
        progress.For(0)!.Report(6);

        reported.Should().Equal(4, 8, 10);
    }

    [Fact]
    public void PartProgress_NobodyListening_HandsOutNothing()
    {
        new PartProgress(null, 1).For(0).Should().BeNull();
    }

    [Fact]
    public void PartProgress_ManyThreads_NeverGoesDownAndEndsAtTheWhole()
    {
        var reported = new List<long>();
        var progress = new PartProgress(new InlineProgress<long>(reported.Add), 8);

        Parallel.For(0, 8, part =>
        {
            for (long bytes = 1; bytes <= 1000; bytes++)
            {
                progress.For(part)!.Report(bytes);
            }
        });

        reported.Should().BeInAscendingOrder().And.EndWith(8000);
    }

    // ── Reading a part ───────────────────────────────────────────────────────

    [Fact]
    public async Task FileSegmentStream_WindowsReadSideBySide_EachGetsItsOwnBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tcv-segment-test-{Guid.NewGuid():N}.tmp");
        var data = Enumerable.Range(0, 64 * 1024).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(path, data);
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous);
            const int Size = 16 * 1024;

            var windows = await Task.WhenAll(Enumerable.Range(0, 4).Select(index => Task.Run(async () =>
            {
                await using var window = new FileSegmentStream(file.SafeFileHandle, index * Size, Size);
                using var copy = new MemoryStream();
                await window.CopyToAsync(copy, 1000);
                return copy.ToArray();
            })));

            windows.SelectMany(w => w).Should().Equal(data);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FileSegmentStream_ReadTwice_GivesTheSameBytesFromTheStart()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tcv-segment-test-{Guid.NewGuid():N}.tmp");
        await File.WriteAllBytesAsync(path, "0123456789"u8.ToArray());
        try
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 4096, FileOptions.Asynchronous);
            await using var window = new FileSegmentStream(file.SafeFileHandle, 2, 4);

            var first = new byte[4];
            await window.ReadExactlyAsync(first);
            window.Position = 0;
            var second = new byte[4];
            window.ReadExactly(second);

            first.Should().Equal("2345"u8.ToArray());
            second.Should().Equal(first);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}

/// <summary>The one table of media types.</summary>
public sealed class ContentTypesTests
{
    [Theory]
    [InlineData("index.html", "text/html")]
    [InlineData("dir/PHOTO.JPG", "image/jpeg")]
    [InlineData("a.b/report.pdf", "application/pdf")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("archive.unknownext", ContentTypes.Fallback)]
    [InlineData("no-extension", ContentTypes.Fallback)]
    [InlineData("dir.txt/no-extension", ContentTypes.Fallback)]
    public void ForName_AFileName_IsItsTypeOrTheFallback(string name, string expected)
    {
        ContentTypes.ForName(name).Should().Be(expected);
    }
}
