using System.Collections.Concurrent;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// <see cref="TransferQueue.Finished"/> is what puts a transfer into the operation log (INV-10, 6.49):
/// every run that ends is reported exactly once — a pause is not an end, a retry is a new run.
/// </summary>
public sealed partial class TransferQueueEndToEndTests
{
    private ConcurrentQueue<TransferFinishedEventArgs> RecordFinished()
    {
        var finished = new ConcurrentQueue<TransferFinishedEventArgs>();
        _queue.Finished += (_, e) => finished.Enqueue(e);
        return finished;
    }

    /// <summary>Gives a second, wrong report time to arrive before the count is checked.</summary>
    private static Task Settle() => Task.Delay(300);

    [Fact]
    public async Task Finished_CompletedDownload_IsReportedOnceAsComplete()
    {
        var finished = RecordFinished();
        _server.Files["/f.bin"] = Payload(1000);
        var item = Download("/f.bin", Path.Combine(_dir, "f.bin"));

        _queue.Enqueue(item);

        (await WaitFor(() => !finished.IsEmpty)).Should().BeTrue(item.ErrorMessage);
        await Settle();
        finished.Should().ContainSingle();
        finished.Single().Item.Should().BeSameAs(item);
        finished.Single().Status.Should().Be(TransferStatus.Complete);
        finished.Single().Error.Should().BeNull();
    }

    [Fact]
    public async Task Finished_RefusedUpload_IsReportedAsFailedWithTheErrorTheWindowShows()
    {
        var finished = RecordFinished();
        _server.Files["/up.bin"] = Payload(10);
        var local = Path.Combine(_dir, "up.bin");
        File.WriteAllBytes(local, Payload(100));
        var item = new TransferItem
        {
            SourcePath = local, DestinationPath = "/up.bin", Connection = Connection, Direction = TransferDirection.Upload,
        };

        _queue.Enqueue(item);

        (await WaitFor(() => !finished.IsEmpty)).Should().BeTrue();
        finished.Single().Status.Should().Be(TransferStatus.Failed);
        finished.Single().Error.Should().Be(item.ErrorMessage).And.Contain("already exists");
    }

    [Fact]
    public async Task Finished_PauseThenResume_IsReportedOnlyAtTheEnd()
    {
        var finished = RecordFinished();
        _server.Files["/big.bin"] = Payload(1_000_000);
        _server.SlowAfterBytes = 100_000;
        var item = Download("/big.bin", Path.Combine(_dir, "big.bin"));
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.Pause(item.Id);
        (await WaitFor(() => item.Status == TransferStatus.Paused && item.Cts is null)).Should().BeTrue();
        await Settle();
        finished.Should().BeEmpty("a paused transfer has not ended");

        _server.SlowAfterBytes = -1;
        await _queue.ResumeAsync(item.Id);

        (await WaitFor(() => !finished.IsEmpty)).Should().BeTrue(item.ErrorMessage);
        await Settle();
        finished.Should().ContainSingle().Which.Status.Should().Be(TransferStatus.Complete);
    }

    [Fact]
    public async Task Finished_CancelRightAfterPause_IsReportedOnce()
    {
        // The worker sees the pause as a cancel by then, and the window reports the cancel too:
        // one run, two threads ending it.
        var finished = RecordFinished();
        _server.Files["/slow.bin"] = Payload(2_000_000);
        _server.SlowAfterBytes = 100_000;
        var item = Download("/slow.bin", Path.Combine(_dir, "slow.bin"));
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.Pause(item.Id);
        _queue.Cancel(item.Id);

        (await WaitFor(() => !finished.IsEmpty)).Should().BeTrue();
        await Settle();
        finished.Should().ContainSingle().Which.Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public async Task Finished_CancelledBeforeItStarted_IsReportedAsCancelled()
    {
        var finished = RecordFinished();
        var item = Download("/never.bin", Path.Combine(_dir, "never.bin"));
        _queue.AddWithoutStarting(item);

        _queue.Cancel(item.Id);

        (await WaitFor(() => !finished.IsEmpty)).Should().BeTrue();
        finished.Single().Status.Should().Be(TransferStatus.Cancelled);
    }

    [Fact]
    public async Task Finished_RetryAfterFailure_ReportsEachRun()
    {
        var finished = RecordFinished();
        _server.Files["/r.bin"] = Payload(1000);
        _server.FailNextRequests = 1;
        var item = Download("/r.bin", Path.Combine(_dir, "r.bin"));
        _queue.Enqueue(item);
        (await WaitFor(() => !finished.IsEmpty)).Should().BeTrue();

        _queue.Retry(item.Id);

        (await WaitFor(() => finished.Count == 2)).Should().BeTrue(item.ErrorMessage);
        finished.Select(e => e.Status).Should().Equal(TransferStatus.Failed, TransferStatus.Complete);
    }

    [Fact]
    public async Task Finished_ListenerThatThrows_DoesNotFailTheTransferOrStopTheQueue()
    {
        _queue.Finished += (_, _) => throw new InvalidOperationException("listener bug");
        _server.Files["/a.bin"] = Payload(10);
        _server.Files["/b.bin"] = Payload(20);
        var first = Download("/a.bin", Path.Combine(_dir, "a.bin"));
        var second = Download("/b.bin", Path.Combine(_dir, "b.bin"));

        _queue.Enqueue(first);
        _queue.Enqueue(second);

        (await WaitFor(() => first.Status == TransferStatus.Complete && second.Status == TransferStatus.Complete))
            .Should().BeTrue(first.ErrorMessage ?? second.ErrorMessage);
    }
}
