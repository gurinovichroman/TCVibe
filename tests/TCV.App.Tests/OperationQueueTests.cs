using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

public class OperationQueueTests
{
    private sealed class SucceedingOperation : FileOperationBase
    {
        public override Task ExecuteAsync(CancellationToken ct)
        {
            State = OperationState.Completed;
            return Task.CompletedTask;
        }
    }

    private sealed class DescribedOperation : FileOperationBase
    {
        private readonly bool _fail;
        public DescribedOperation(bool fail = false) => _fail = fail;

        public override OperationLogDescriptor LogDescriptor =>
            new(OperationKind.Copy, "src", "dst", 2);

        public override Task ExecuteAsync(CancellationToken ct)
        {
            if (_fail) throw new InvalidOperationException("boom");
            State = OperationState.Completed;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOperation : FileOperationBase
    {
        public override Task ExecuteAsync(CancellationToken ct) =>
            throw new UnauthorizedAccessException("denied");
    }

    private sealed class CancellingOperation : FileOperationBase
    {
        public override Task ExecuteAsync(CancellationToken ct) =>
            throw new OperationCanceledException();
    }

    [Fact]
    public async Task EnqueueAsync_SuccessfulOperation_ReportsCompleted()
    {
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance);
        var op = new SucceedingOperation();

        await queue.EnqueueAsync(op);

        op.State.Should().Be(OperationState.Completed);
        op.FailureException.Should().BeNull();
    }

    [Fact]
    public async Task EnqueueAsync_FailingOperation_RecordsFailureInsteadOfLookingLikeSuccess()
    {
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance);
        var op = new ThrowingOperation();

        await queue.EnqueueAsync(op);

        op.State.Should().Be(OperationState.Failed);
        op.FailureException.Should().BeOfType<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task EnqueueAsync_CancelledOperation_ReportsCancelled()
    {
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance);
        var op = new CancellingOperation();

        await queue.EnqueueAsync(op);

        op.State.Should().Be(OperationState.Cancelled);
    }

    [Fact]
    public async Task EnqueueAsync_WithLog_RecordsCompletedEntry()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"tcv-oplog-{Guid.NewGuid():N}.json");
        try
        {
            var log = new OperationLog(logPath);
            using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);

            await queue.EnqueueAsync(new DescribedOperation());

            var entry = log.LoadAll().Should().ContainSingle().Subject;
            entry.Kind.Should().Be(OperationKind.Copy);
            entry.Source.Should().Be("src");
            entry.Destination.Should().Be("dst");
            entry.ItemCount.Should().Be(2);
            entry.Outcome.Should().Be(OperationOutcome.Completed);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_FailingOperation_RecordsFailedEntryWithError()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"tcv-oplog-{Guid.NewGuid():N}.json");
        try
        {
            var log = new OperationLog(logPath);
            using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);

            await queue.EnqueueAsync(new DescribedOperation(fail: true));

            var entry = log.LoadAll().Should().ContainSingle().Subject;
            entry.Outcome.Should().Be(OperationOutcome.Failed);
            entry.Error.Should().Be("boom");
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public async Task EnqueueAsync_WithoutDescriptor_WritesNothing()
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"tcv-oplog-{Guid.NewGuid():N}.json");
        try
        {
            var log = new OperationLog(logPath);
            using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);

            await queue.EnqueueAsync(new SucceedingOperation());

            log.LoadAll().Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }
}
