namespace TCV.Network;

/// <summary>How a run of <see cref="ReconnectLoop"/> ended.</summary>
public enum ReconnectOutcome
{
    /// <summary>A try succeeded.</summary>
    Connected,

    /// <summary>Every try failed with a dropped connection: the server stays away.</summary>
    GaveUp,

    /// <summary>A try failed with something another try cannot fix (a refused login, a changed host key).</summary>
    Refused,

    /// <summary>The caller's token was cancelled: the user stopped it, or the panel moved on.</summary>
    Cancelled,
}

/// <summary>What the loop is doing right now, for the chip and the list: waiting for try N, or making it.</summary>
/// <param name="Attempt">The try being waited for or made, 1-based.</param>
/// <param name="MaxAttempts">How many tries there are in all.</param>
/// <param name="IsWaiting">True while pausing before the try; false while it runs.</param>
/// <param name="NextAttemptAt">When the pause ends (UTC); meaningful only while waiting.</param>
/// <param name="LastError">Why the connection is down: the failure that started the loop, then each failed try's.</param>
public sealed record ReconnectProgress(int Attempt, int MaxAttempts, bool IsWaiting, DateTime NextAttemptAt, Exception? LastError);

/// <summary>
/// Reconnects by itself after a connection dropped (6.82): pauses as <see cref="ReconnectPolicy"/>
/// says, tries, and stops at the first success, at a failure a new try cannot fix, when the tries are
/// used up, or when cancelled. <see cref="RetryNow"/> cuts the current pause short. No UI and no
/// network of its own: the try is the caller's delegate, the pause an injectable delay, so tests drive
/// it with a file system that fails on demand.
/// </summary>
/// <remarks>
/// Runs on the caller's context: progress is reported, and the try is started, wherever
/// <see cref="RunAsync"/> was awaited — the UI thread in the application. The try itself must move
/// its network work off that thread (INV-06).
/// </remarks>
public sealed class ReconnectLoop
{
    private readonly ReconnectPolicy _policy;
    private readonly Func<int, CancellationToken, Task> _attempt;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<DateTime> _utcNow;
    private TaskCompletionSource _skipWait = NewSkip();

    /// <param name="policy">Pauses and which failures are worth another try.</param>
    /// <param name="attempt">One try (its 1-based number, the token); throws when it fails.</param>
    /// <param name="delay">The pause; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> unless a test says otherwise.</param>
    /// <param name="utcNow">The clock, for <see cref="ReconnectProgress.NextAttemptAt"/>.</param>
    public ReconnectLoop(
        ReconnectPolicy policy,
        Func<int, CancellationToken, Task> attempt,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(attempt);
        _policy = policy;
        _attempt = attempt;
        _delay = delay ?? Task.Delay;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Raised before each pause and before each try.</summary>
    public event Action<ReconnectProgress>? Progress;

    /// <summary>The failure of the last try (or the one that started the loop); null after a success.</summary>
    public Exception? LastError { get; private set; }

    /// <summary>How many tries were made.</summary>
    public int Attempts { get; private set; }

    /// <summary>True while the loop pauses before a try: <see cref="RetryNow"/> then has an effect.</summary>
    public bool IsWaiting { get; private set; }

    /// <summary>Ends the current pause now; the next try starts at once. Does nothing while a try runs.</summary>
    public void RetryNow() => _skipWait.TrySetResult();

    /// <summary>Tries until connected, refused, out of tries or cancelled.</summary>
    /// <param name="cause">The failure that dropped the connection, shown until the first try reports its own.</param>
    public async Task<ReconnectOutcome> RunAsync(Exception? cause, CancellationToken ct)
    {
        LastError = cause;
        for (var attempt = 1; _policy.DelayBefore(attempt) is { } pause; attempt++)
        {
            try
            {
                await PauseAsync(attempt, pause, ct);
                ct.ThrowIfCancellationRequested();

                Attempts = attempt;
                Progress?.Invoke(new ReconnectProgress(attempt, _policy.MaxAttempts, false, default, LastError));
                await _attempt(attempt, ct);
                LastError = null;
                return ReconnectOutcome.Connected;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return ReconnectOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                LastError = ex;
                if (!ReconnectPolicy.IsWorthRetrying(ex))
                {
                    return ReconnectOutcome.Refused;
                }
            }
        }

        return ReconnectOutcome.GaveUp;
    }

    private async Task PauseAsync(int attempt, TimeSpan pause, CancellationToken ct)
    {
        _skipWait = NewSkip();
        IsWaiting = true;
        try
        {
            Progress?.Invoke(new ReconnectProgress(attempt, _policy.MaxAttempts, true, _utcNow() + pause, LastError));
            if (pause > TimeSpan.Zero)
            {
                // Retry now ends the pause early; the delay itself is cancelled with the loop.
                using var stopDelay = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var delay = _delay(pause, stopDelay.Token);
                await Task.WhenAny(delay, _skipWait.Task);
                stopDelay.Cancel();
                ct.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            IsWaiting = false;
        }
    }

    private static TaskCompletionSource NewSkip() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
