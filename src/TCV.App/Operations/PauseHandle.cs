namespace TCV.App.Operations;

/// <summary>
/// Allows an ongoing file operation to be paused and resumed. Never blocks a thread: the UI thread
/// flips it, the worker awaits it.
/// </summary>
public sealed class PauseHandle : IDisposable
{
    private readonly object _lock = new();
    private TaskCompletionSource? _resumed;

    public bool IsPaused
    {
        get { lock (_lock) return _resumed is not null; }
    }

    public void Pause()
    {
        lock (_lock)
            _resumed ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        TaskCompletionSource? gate;
        lock (_lock)
        {
            gate = _resumed;
            _resumed = null;
        }
        gate?.TrySetResult();
    }

    /// <summary>Completes at once when running; while paused, when resumed (or cancelled).</summary>
    public Task WaitAsync(CancellationToken ct)
    {
        Task? gate;
        lock (_lock) gate = _resumed?.Task;
        return gate is null || gate.IsCompleted ? Task.CompletedTask : gate.WaitAsync(ct);
    }

    /// <summary>Releases anyone still waiting: a disposed handle must not strand an operation.</summary>
    public void Dispose() => Resume();
}
