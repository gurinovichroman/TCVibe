using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TCV.App.Services;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// Runs every file operation of the window and of the agents, and writes each one to the log (INV-10).
/// Two lanes, each one operation at a time, run side by side (<see cref="OperationLane"/>, ADR-0038):
/// a download from a server does not hold the copies on the disk behind it. Pause, cancel and the
/// lists of what runs and waits cover both.
/// </summary>
public sealed class OperationQueue : IOperationQueue, IDisposable
{
    private readonly Dictionary<OperationLane, Channel<(IFileOperation Op, TaskCompletionSource Tcs)>> _lanes;
    private readonly ILogger<OperationQueue> _logger;
    private readonly OperationLog? _operationLog;

    // An operation is in exactly one of these from EnqueueAsync until it ends; both under _sync.
    private readonly object _sync = new();
    private readonly List<IFileOperation> _running = [];
    private readonly List<IFileOperation> _pending = [];

    private readonly object _pauseLock = new();
    private TaskCompletionSource? _resumeGate;
    private readonly CancellationTokenSource _shutdownCts = new();

    // OperationLog is optional so tests can construct a bare queue; DI always supplies it.
    public OperationQueue(ILogger<OperationQueue> logger, OperationLog? operationLog = null)
    {
        _logger = logger;
        _operationLog = operationLog;
        _lanes = Enum.GetValues<OperationLane>().ToDictionary(
            lane => lane,
            _ => Channel.CreateUnbounded<(IFileOperation, TaskCompletionSource)>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            }));

        // On the thread pool, not on whatever thread built the queue. DI builds it on the dispatcher,
        // and a worker started there would run every operation's synchronous parts — opening files,
        // stamping times, the shell's recycle-bin delete — on the UI thread (INV-06). Operations
        // reach the UI only through the dispatcher (dialog service, progress view model).
        foreach (var lane in _lanes.Values)
        {
            _ = Task.Run(() => RunWorkerAsync(lane.Reader, _shutdownCts.Token));
        }
    }

    public IReadOnlyList<IFileOperation> Running
    {
        get
        {
            lock (_sync)
            {
                return _running.ToList();
            }
        }
    }

    public IReadOnlyList<IFileOperation> Pending
    {
        get
        {
            lock (_sync)
            {
                return _pending.ToList();
            }
        }
    }

    public async Task EnqueueAsync(IFileOperation op, CancellationToken ct = default)
    {
        var lane = op is FileOperationBase described ? described.Lane : OperationLane.Local;
        lock (_sync)
        {
            _pending.Add(op);
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _lanes[lane].Writer.WriteAsync((op, tcs), ct);
        }
        catch
        {
            lock (_sync)
            {
                _pending.Remove(op);
            }

            throw;
        }

        await tcs.Task;
    }

    /// <summary>True while the queue holds back operations that have not started yet.</summary>
    public bool IsPaused
    {
        get
        {
            lock (_pauseLock)
            {
                return _resumeGate is not null;
            }
        }
    }

    /// <summary>Stops both lanes from starting their next operation. One already running is not touched.</summary>
    public void PauseAll()
    {
        lock (_pauseLock)
        {
            _resumeGate ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
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
    /// Cancels the running operations and every one still waiting. Waiting operations stay in their
    /// lane so their awaiters complete (as Cancelled) instead of hanging forever.
    /// </summary>
    public void CancelAll()
    {
        // The waiting ones first: cancelling a running operation can run its worker on, inline, up to
        // the next operation in the lane — which would start if it were not cancelled yet (TRAP-91).
        List<IFileOperation> all;
        lock (_sync)
        {
            all = [.. _pending, .. _running];
        }

        foreach (var op in all.OfType<FileOperationBase>())
        {
            op.Cts.Cancel();
        }
    }

    private async Task RunWorkerAsync(ChannelReader<(IFileOperation Op, TaskCompletionSource Tcs)> lane, CancellationToken shutdownToken)
    {
        await foreach (var (op, tcs) in lane.ReadAllAsync(shutdownToken))
        {
            // Wait while paused; an operation cancelled in the meantime has nothing to wait for.
            while (true)
            {
                Task? gate;
                lock (_pauseLock)
                {
                    gate = _resumeGate?.Task;
                }

                if (gate is null || gate.IsCompleted || op is FileOperationBase { Cts.IsCancellationRequested: true })
                {
                    break;
                }

                try
                {
                    await gate.WaitAsync(TimeSpan.FromMilliseconds(200), shutdownToken);
                }
                catch (TimeoutException)
                {
                    // Re-check the cancellation flag.
                }
            }

            // Cancelled while it was still waiting, or the queue is shutting down — ReadAllAsync still hands
            // out what was buffered (TRAP-45): record it and move on without executing.
            if (shutdownToken.IsCancellationRequested || op is FileOperationBase { Cts.IsCancellationRequested: true })
            {
                lock (_sync)
                {
                    _pending.Remove(op);
                }

                MarkOutcome(op, OperationState.Cancelled, null);
                RecordToLog(op);
                tcs.TrySetResult();
                continue;
            }

            lock (_sync)
            {
                _pending.Remove(op);
                _running.Add(op);
            }

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
                lock (_sync)
                {
                    _running.Remove(op);
                }

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
        if (_operationLog is null || op is not FileOperationBase b || b.LogDescriptor is not { } d)
        {
            return;
        }

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
        if (op is not FileOperationBase b)
        {
            return;
        }

        b.State = state;
        b.FailureException ??= failure;
    }

    public void Dispose()
    {
        _shutdownCts.Cancel();
        foreach (var lane in _lanes.Values)
        {
            lane.Writer.TryComplete();
        }

        ResumeAll();
        _shutdownCts.Dispose();
    }
}
