using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>
/// ROADMAP 6.61: a cancel cannot be lost between the worker choosing an item and the transfer starting,
/// a paused transfer is work the window must not close over, and closing ends every unfinished run
/// with its partial file removed and a line in the log.
/// </summary>
public sealed partial class TransferQueueEndToEndTests
{
    [Fact]
    public async Task Cancel_RightAsTheWorkerPicksTheItemUp_IsNeverLost()
    {
        // Each round races a cancel against the pick. Whichever wins, what the window shows, what the
        // log hears and what is on disk must agree.
        var finished = RecordFinished();
        _server.Files["/q.bin"] = Payload(64 * 1024);

        for (var round = 0; round < 40; round++)
        {
            var target = Path.Combine(_dir, $"q{round}.bin");
            var item = Download("/q.bin", target);

            _queue.Enqueue(item);
            if (round % 2 == 1)
            {
                await Task.Yield();
            }

            _queue.Cancel(item.Id);

            (await WaitFor(() => finished.Any(e => ReferenceEquals(e.Item, item)) && item.Cts is null))
                .Should().BeTrue($"round {round} must end");
            var report = finished.Where(e => ReferenceEquals(e.Item, item)).Should().ContainSingle().Subject;
            item.Status.Should().Be(report.Status, $"round {round}: the window and the log must agree");
            if (report.Status == TransferStatus.Cancelled)
            {
                (await WaitFor(() => !File.Exists(target))).Should().BeTrue($"round {round}: a cancelled download leaves nothing");
            }
            else
            {
                report.Status.Should().Be(TransferStatus.Complete, $"round {round}");
            }
        }
    }

    [Fact]
    public async Task HasActiveWork_PausedTransfer_CountsAsWork()
    {
        _server.Files["/p.bin"] = Payload(1_000_000);
        _server.SlowAfterBytes = 50_000;
        var item = Download("/p.bin", Path.Combine(_dir, "p.bin"));
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.Pause(item.Id);
        (await WaitFor(() => item.Status == TransferStatus.Paused && item.Cts is null)).Should().BeTrue();

        _queue.HasActiveWork.Should().BeTrue("closing over a paused transfer ends it for good");
    }

    [Fact]
    public async Task CancelAllAsync_PausedDownload_RemovesThePartialFileAndIsReportedCancelled()
    {
        var finished = RecordFinished();
        _server.Files["/paused.bin"] = Payload(1_000_000);
        _server.SlowAfterBytes = 50_000;
        var target = Path.Combine(_dir, "paused.bin");
        var item = Download("/paused.bin", target);
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();
        _queue.Pause(item.Id);
        (await WaitFor(() => item.Status == TransferStatus.Paused && item.Cts is null)).Should().BeTrue();
        File.Exists(target).Should().BeTrue();

        await _queue.CancelAllAsync().WaitAsync(TimeSpan.FromSeconds(15));

        item.Status.Should().Be(TransferStatus.Cancelled);
        File.Exists(target).Should().BeFalse("nothing can resume it after the process ends");
        finished.Should().ContainSingle().Which.Status.Should().Be(TransferStatus.Cancelled);
        _queue.HasActiveWork.Should().BeFalse();
    }

    [Fact]
    public async Task CancelAllAsync_RunningAndWaiting_EndsEveryRunBeforeItCompletes()
    {
        var finished = RecordFinished();
        _server.Files["/run.bin"] = Payload(2_000_000);
        _server.SlowAfterBytes = 50_000;
        var runningTarget = Path.Combine(_dir, "run.bin");
        var running = Download("/run.bin", runningTarget);
        var waiting = Download("/run.bin", Path.Combine(_dir, "wait.bin"));
        _queue.Enqueue(running);
        _queue.Enqueue(waiting);
        (await WaitFor(() => running.BytesTransferred > 0)).Should().BeTrue();

        await _queue.CancelAllAsync().WaitAsync(TimeSpan.FromSeconds(15));

        // Completed means done: the log has both lines and the partial file is gone by now.
        finished.Select(e => e.Item).Should().BeEquivalentTo([running, waiting]);
        finished.Should().OnlyContain(e => e.Status == TransferStatus.Cancelled);
        File.Exists(runningTarget).Should().BeFalse();
        running.Cts.Should().BeNull();
    }

    [Fact]
    public async Task Retry_RightAfterCancelOfARunningTransfer_TheCancelledRunIsStillReported()
    {
        // The worker is still unwinding when Retry comes: taken as a pause, the run went unreported.
        var finished = RecordFinished();
        _server.Files["/u.bin"] = Payload(1_000_000);
        _server.SlowAfterBytes = 50_000;
        var item = Download("/u.bin", Path.Combine(_dir, "u.bin"));
        _queue.Enqueue(item);
        (await WaitFor(() => item.BytesTransferred > 0)).Should().BeTrue();

        _queue.Cancel(item.Id);
        _queue.Retry(item.Id);

        (await WaitFor(() => finished.Any(e => ReferenceEquals(e.Item, item) && e.Status == TransferStatus.Cancelled)))
            .Should().BeTrue("a cancelled run is a line in the log whatever the retry did");
    }

    [Fact]
    public async Task CancelAllAsync_NothingQueued_CompletesAtOnce()
    {
        var all = _queue.CancelAllAsync();

        all.IsCompleted.Should().BeTrue();
        await all;
    }
}
