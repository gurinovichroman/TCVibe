using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

public class PauseHandleTests
{
    [Fact]
    public async Task WaitAsync_WhenNotPaused_CompletesAtOnce()
    {
        using var handle = new PauseHandle();

        await handle.WaitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        handle.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_WhilePaused_BlocksUntilResumed()
    {
        using var handle = new PauseHandle();
        handle.Pause();

        var waiting = handle.WaitAsync(CancellationToken.None);
        await Task.Delay(150);
        waiting.IsCompleted.Should().BeFalse("a paused operation must not move on");

        handle.Resume();
        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
        handle.IsPaused.Should().BeFalse();
    }

    [Fact]
    public async Task WaitAsync_WhilePaused_CancellationEndsTheWait()
    {
        using var handle = new PauseHandle();
        handle.Pause();
        using var cts = new CancellationTokenSource();

        var waiting = handle.WaitAsync(cts.Token);
        cts.Cancel();

        await FluentActions.Awaiting(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Pause_CalledTwice_StillResumesWithOneResume()
    {
        using var handle = new PauseHandle();
        handle.Pause();
        handle.Pause();

        handle.Resume();

        handle.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void Resume_WhenNotPaused_IsHarmless()
    {
        using var handle = new PauseHandle();

        handle.Invoking(h => h.Resume()).Should().NotThrow();
    }

    [Fact]
    public async Task Dispose_ReleasesAWaitingOperation()
    {
        var handle = new PauseHandle();
        handle.Pause();
        var waiting = handle.WaitAsync(CancellationToken.None);

        handle.Dispose();

        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }
}

public class OperationQueueControlTests
{
    private sealed class QuickOperation(OperationLane lane = OperationLane.Local) : FileOperationBase
    {
        public bool Executed { get; private set; }

        public override OperationLane Lane => lane;

        public override Task ExecuteAsync(CancellationToken ct)
        {
            Executed = true;
            State = OperationState.Completed;
            return Task.CompletedTask;
        }
    }

    /// <summary>Runs until told to finish or cancelled — stands in for a long copy.</summary>
    private sealed class BlockingOperation(OperationLane lane = OperationLane.Local) : FileOperationBase
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public override OperationLane Lane => lane;

        public void Finish() => _finish.TrySetResult();

        public override async Task ExecuteAsync(CancellationToken ct)
        {
            _started.TrySetResult();
            await _finish.Task.WaitAsync(ct);
            State = OperationState.Completed;
        }
    }

    private static OperationQueue NewQueue() => new(NullLogger<OperationQueue>.Instance);

    [Fact]
    public async Task PauseAll_HoldsBackTheNextOperationUntilResumeAll()
    {
        using var queue = NewQueue();
        queue.PauseAll();
        var op = new QuickOperation();

        var enqueued = queue.EnqueueAsync(op);
        await Task.Delay(300);
        enqueued.IsCompleted.Should().BeFalse();
        op.Executed.Should().BeFalse();
        queue.IsPaused.Should().BeTrue();

        queue.ResumeAll();
        await enqueued.WaitAsync(TimeSpan.FromSeconds(3));

        op.Executed.Should().BeTrue();
        queue.IsPaused.Should().BeFalse();
    }

    [Fact]
    public void ResumeAll_WhenNotPaused_DoesNothing()
    {
        using var queue = NewQueue();

        queue.Invoking(q => q.ResumeAll()).Should().NotThrow();
        queue.Invoking(q => q.ResumeAll()).Should().NotThrow();
    }

    [Fact]
    public async Task CancelAll_CancelsTheRunningOperationAndEveryWaitingOne()
    {
        using var queue = NewQueue();
        var running = new BlockingOperation();
        var waiting = new QuickOperation();

        var first = queue.EnqueueAsync(running);
        await running.Started.WaitAsync(TimeSpan.FromSeconds(3));
        var second = queue.EnqueueAsync(waiting);

        queue.CancelAll();

        // Both awaiters must come back: an EnqueueAsync that never completes hangs its command for good.
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
        running.State.Should().Be(OperationState.Cancelled);
        waiting.State.Should().Be(OperationState.Cancelled);
        waiting.Executed.Should().BeFalse("a cancelled operation that never started must not start");
    }

    [Fact]
    public async Task CancelAll_WhilePaused_ReleasesTheWaitingOperationAsCancelled()
    {
        using var queue = NewQueue();
        queue.PauseAll();
        var op = new QuickOperation();
        var enqueued = queue.EnqueueAsync(op);
        await Task.Delay(100);

        queue.CancelAll();

        await enqueued.WaitAsync(TimeSpan.FromSeconds(3));
        op.State.Should().Be(OperationState.Cancelled);
        op.Executed.Should().BeFalse();
    }

    [Fact]
    public async Task Queue_AfterCancelAll_RunsTheNextOperationNormally()
    {
        using var queue = NewQueue();
        var running = new BlockingOperation();
        var first = queue.EnqueueAsync(running);
        await running.Started.WaitAsync(TimeSpan.FromSeconds(3));
        queue.CancelAll();
        await first.WaitAsync(TimeSpan.FromSeconds(3));

        var next = new QuickOperation();
        await queue.EnqueueAsync(next).WaitAsync(TimeSpan.FromSeconds(3));

        next.Executed.Should().BeTrue();
        next.State.Should().Be(OperationState.Completed);
    }

    // ── Lanes (ADR-0038) ──────────────────────────────────────────────────────

    [Fact]
    public async Task EnqueueAsync_LongDownloadRunning_LocalOperationDoesNotWaitForIt()
    {
        using var queue = NewQueue();
        var download = new BlockingOperation(OperationLane.Network);
        var downloading = queue.EnqueueAsync(download);
        await download.Started.WaitAsync(TimeSpan.FromSeconds(3));

        var local = new QuickOperation();
        await queue.EnqueueAsync(local).WaitAsync(TimeSpan.FromSeconds(3));

        local.Executed.Should().BeTrue();
        downloading.IsCompleted.Should().BeFalse();
        download.Finish();
        await downloading.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task EnqueueAsync_LongLocalCopyRunning_ServerOperationDoesNotWaitForIt()
    {
        using var queue = NewQueue();
        var copy = new BlockingOperation();
        var copying = queue.EnqueueAsync(copy);
        await copy.Started.WaitAsync(TimeSpan.FromSeconds(3));

        var upload = new QuickOperation(OperationLane.Network);
        await queue.EnqueueAsync(upload).WaitAsync(TimeSpan.FromSeconds(3));

        upload.Executed.Should().BeTrue();
        copy.Finish();
        await copying.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(OperationLane.Local)]
    [InlineData(OperationLane.Network)]
    public async Task EnqueueAsync_SameLane_StillOneAtATime(OperationLane lane)
    {
        using var queue = NewQueue();
        var first = new BlockingOperation(lane);
        var firstDone = queue.EnqueueAsync(first);
        await first.Started.WaitAsync(TimeSpan.FromSeconds(3));

        var second = new QuickOperation(lane);
        var secondDone = queue.EnqueueAsync(second);
        await Task.Delay(200);

        second.Executed.Should().BeFalse("an operation waits for the one ahead of it in its lane");
        queue.Pending.Should().ContainSingle().Which.Should().BeSameAs(second);
        queue.Running.Should().ContainSingle().Which.Should().BeSameAs(first);

        first.Finish();
        await Task.WhenAll(firstDone, secondDone).WaitAsync(TimeSpan.FromSeconds(3));
        second.Executed.Should().BeTrue();
        queue.Running.Should().BeEmpty();
        queue.Pending.Should().BeEmpty();
    }

    [Fact]
    public async Task PauseAll_HoldsBackBothLanes()
    {
        using var queue = NewQueue();
        queue.PauseAll();
        var local = new QuickOperation();
        var network = new QuickOperation(OperationLane.Network);

        var both = Task.WhenAll(queue.EnqueueAsync(local), queue.EnqueueAsync(network));
        await Task.Delay(300);
        local.Executed.Should().BeFalse();
        network.Executed.Should().BeFalse();
        queue.Pending.Should().HaveCount(2, "an operation held back by the pause is still waiting");

        queue.ResumeAll();
        await both.WaitAsync(TimeSpan.FromSeconds(3));
        local.Executed.Should().BeTrue();
        network.Executed.Should().BeTrue();
    }

    [Fact]
    public async Task CancelAll_CancelsWhatRunsInEitherLane()
    {
        using var queue = NewQueue();
        var local = new BlockingOperation();
        var network = new BlockingOperation(OperationLane.Network);
        var both = Task.WhenAll(queue.EnqueueAsync(local), queue.EnqueueAsync(network));
        await Task.WhenAll(local.Started, network.Started).WaitAsync(TimeSpan.FromSeconds(3));
        queue.Running.Should().HaveCount(2);

        queue.CancelAll();

        await both.WaitAsync(TimeSpan.FromSeconds(3));
        local.State.Should().Be(OperationState.Cancelled);
        network.State.Should().Be(OperationState.Cancelled);
    }

    [Fact]
    public async Task Dispose_OperationStillWaiting_IsCancelledWithoutStarting()
    {
        // TRAP-45: the lane's reader still hands out what was buffered once the queue shuts down.
        var queue = NewQueue();
        var running = new BlockingOperation();
        var runningDone = queue.EnqueueAsync(running);
        await running.Started.WaitAsync(TimeSpan.FromSeconds(3));
        var waiting = new QuickOperation();
        var waitingDone = queue.EnqueueAsync(waiting);

        queue.Dispose();

        await Task.WhenAll(runningDone, waitingDone).WaitAsync(TimeSpan.FromSeconds(3));
        waiting.Executed.Should().BeFalse();
        waiting.State.Should().Be(OperationState.Cancelled);
    }
}

/// <summary>Which lane a transfer takes: the network one as soon as a server or a cloud store is on one side.</summary>
public class OperationLaneTests
{
    private static readonly TCV.App.Operations.Conflict.IConflictStrategy Overwrite = new TCV.App.Operations.Conflict.OverwriteStrategy();

    [Theory]
    [InlineData("file", "file", OperationLane.Local)]
    [InlineData("zip", "file", OperationLane.Local)]
    [InlineData("ftp", "file", OperationLane.Network)]
    [InlineData("file", "sftp", OperationLane.Network)]
    [InlineData("file", "davs", OperationLane.Network)]
    [InlineData("s3", "file", OperationLane.Network)]
    [InlineData("file", "gdrive", OperationLane.Network)]
    public void Copy_And_Move_GoToTheNetworkLaneWhenAServerIsOnOneSide(string from, string to, OperationLane expected)
    {
        var source = new InMemoryFileSystem(from);
        var destination = new InMemoryFileSystem(to);

        new CopyOperation(source, destination, [], new VfsPath("/"), Overwrite, NullLogger<CopyOperation>.Instance)
            .Lane.Should().Be(expected);
        new MoveOperation(source, destination, [], new VfsPath("/"), Overwrite, NullLogger<MoveOperation>.Instance)
            .Lane.Should().Be(expected);
    }

    [Fact]
    public void Delete_OnAServer_GoesToTheNetworkLane()
    {
        new DeleteOperation(new InMemoryFileSystem("ftp"), [], moveToRecycleBin: false, NullLogger<DeleteOperation>.Instance)
            .Lane.Should().Be(OperationLane.Network);
        new DeleteOperation(new InMemoryFileSystem("file"), [], moveToRecycleBin: true, NullLogger<DeleteOperation>.Instance)
            .Lane.Should().Be(OperationLane.Local);
    }

    [Fact]
    public void SearchResultsOverAServer_AreJudgedByTheServerUnderThem()
    {
        var hits = new TCV.App.FileSystem.SearchResultsFileSystem(new InMemoryFileSystem("ftp"), []);

        new DeleteOperation(hits, [], moveToRecycleBin: false, NullLogger<DeleteOperation>.Instance)
            .Lane.Should().Be(OperationLane.Network);
    }
}
