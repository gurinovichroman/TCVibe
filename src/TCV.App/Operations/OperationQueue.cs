using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TCV.App.Services;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

public sealed class OperationQueue : IOperationQueue, IDisposable
{
    private readonly Channel<(IFileOperation Op, TaskCompletionSource Tcs)> _channel;
    private readonly ILogger<OperationQueue> _logger;
    private readonly OperationLog? _operationLog;
    private readonly ConcurrentBag<IFileOperation> _running = new();
    private readonly ConcurrentQueue<IFileOperation> _pending = new();
    private readonly object _pauseLock = new();
    private TaskCompletionSource? _resumeGate;
    private volatile IFileOperation? _current;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Task _worker;

    // OperationLog is optional so tests can construct a bare queue; DI always supplies it.
    public OperationQueue(ILogger<OperationQueue> logger, OperationLog? operationLog = null)
    {
        _logger = logger;
        _operationLog = operationLog;
        _channel = Channel.CreateUnbounded<(IFileOperation, TaskCompletionSource)>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
        // On the thread pool, not on whatever thread built the queue. DI builds it on the dispatcher,
        // and a worker started there would run every operation's synchronous parts — opening files,
        // stamping times, the shell's recycle-bin delete — on the UI thread (INV-06). Operations
        // reach the UI only through the dispatcher (dialog service, progress view model).
        _worker = Task.Run(() => RunWorkerAsync(_shutdownCts.Token));
    }

    public IReadOnlyList<IFileOperation> Running => _running.ToList();
    public IReadOnlyList<IFileOperation> Pending => _pending.ToList();

    public async Task EnqueueAsync(IFileOperation op, CancellationToken ct = default)
    {
        _pending.Enqueue(op);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync((op, tcs), ct);
        await tcs.Task;
    }

    /// <summary>True while the queue holds back operations that have not started yet.</summary>
    public bool IsPaused
    {
        get { lock (_pauseLock) return _resumeGate is not null; }
    }

    /// <summary>Stops the queue from starting the next operation. One already running is not touched.</summary>
    public void PauseAll()
    {
        lock (_pauseLock)
            _resumeGate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Lets the queue go on. Safe to call when it was not paused.</summary>
    public void ResumeAll()
    {
        TaskCompletionSource? gate;
        lock (_pauseLock)
        {
            gate = _resumeGate;
            _resumeGate = null;
        }
        gate?.TrySetResult();
    }

    /// <summary>
    /// Cancels the running operation and every one still waiting. Waiting operations stay in the
    /// channel so their awaiters complete (as Cancelled) instead of hanging forever.
    /// </summary>
    public void CancelAll()
    {
        (_current as FileOperationBase)?.Cts.Cancel();
        foreach (var op in _running.OfType<FileOperationBase>())
            op.Cts.Cancel();
        foreach (var op in _pending.OfType<FileOperationBase>())
            op.Cts.Cancel();
    }

    private async Task RunWorkerAsync(CancellationToken shutdownToken)
    {
        await foreach (var (op, tcs) in _channel.Reader.ReadAllAsync(shutdownToken))
        {
            // Remove from pending
            _pending.TryDequeue(out _);
            _current = op;

            // Wait while paused; an operation cancelled in the meantime has nothing to wait for.
            while (true)
            {
                Task? gate;
                lock (_pauseLock) gate = _resumeGate?.Task;
                if (gate is null || gate.IsCompleted) break;
                if (op is FileOperationBase { Cts.IsCancellationRequested: true }) break;
                try
                {
                    await gate.WaitAsync(TimeSpan.FromMilliseconds(200), shutdownToken);
                }
                catch (TimeoutException)
                {
                    // Re-check the cancellation flag.
                }
            }

            // Cancelled while it was still waiting: record it and move on without executing.
            if (op is FileOperationBase { Cts.IsCancellationRequested: true })
            {
                MarkOutcome(op, OperationState.Cancelled, null);
                RecordToLog(op);
                _current = null;
                tcs.TrySetResult();
                continue;
            }

            _running.Add(op);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    shutdownToken,
                    op is FileOperationBase fob ? fob.Cts.Token : CancellationToken.None);

                await op.ExecuteAsync(linked.Token);
                _logger.LogInformation("Operation {Id} completed", op.Id);
            }
            catch (OperationCanceledException)
            {
                MarkOutcome(op, OperationState.Cancelled, null);
                _logger.LogInformation("Operation {Id} cancelled", op.Id);
            }
            catch (Exception ex)
            {
                MarkOutcome(op, OperationState.Failed, ex);
                _logger.LogError(ex, "Operation {Id} failed", op.Id);
            }
            finally
            {
                _running.TryTake(out _);
                _current = null;
                RecordToLog(op);
                tcs.TrySetResult();
            }
        }
    }

    /// <summary>
    /// Writes the finished operation to the user-facing log, if it exposes a descriptor. Runs in
    /// the worker's finally so every terminal state — completed, failed, cancelled — is recorded.
    /// </summary>
    private void RecordToLog(IFileOperation op)
    {
        if (_operationLog is null) return;
        if (op is not FileOperationBase b || b.LogDescriptor is not { } d) return;

        var outcome = b.State switch
        {
            OperationState.Completed => OperationOutcome.Completed,
            OperationState.Cancelled => OperationOutcome.Cancelled,
            _ => OperationOutcome.Failed,
        };

        _operationLog.Append(new OperationLogEntry
        {
            Timestamp = DateTime.Now,
            Kind = d.Kind,
            Source = d.Source,
            Destination = d.Destination,
            ItemCount = d.ItemCount,
            Outcome = outcome,
            Error = b.FailureException?.Message,
            Origin = b.Origin,
            Renames = d.Renames,
            ArrivedIn = d.ArrivedIn,
        });
    }

    // The exception never reaches the EnqueueAsync awaiter (that would surface as an
    // unhandled exception inside a command), so the outcome is recorded on the operation
    // itself — callers must check State before acting on "the operation finished".
    private static void MarkOutcome(IFileOperation op, OperationState state, Exception? failure)
    {
        if (op is not FileOperationBase b) return;
        b.State = state;
        b.FailureException ??= failure;
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        _channel.Writer.TryComplete();
        ResumeAll();
        _shutdownCts.Dispose();
    }
}
