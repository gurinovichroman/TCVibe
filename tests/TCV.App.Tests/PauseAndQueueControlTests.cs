using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
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
    private sealed class QuickOperation : FileOperationBase
    {
        public bool Executed { get; private set; }

        public override Task ExecuteAsync(CancellationToken ct)
        {
            Executed = true;
            State = OperationState.Completed;
            return Task.CompletedTask;
        }
    }

    /// <summary>Runs until told to finish or cancelled — stands in for a long copy.</summary>
    private sealed class BlockingOperation : FileOperationBase
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        public override async Task ExecuteAsync(CancellationToken ct)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
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
}
