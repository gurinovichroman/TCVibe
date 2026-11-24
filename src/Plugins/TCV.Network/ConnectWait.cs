namespace TCV.Network;

/// <summary>
/// Waits for a step of opening a connection for no longer than the user is willing to. Cancel has to
/// work at once, and a connect may not hear the token: SSH.NET and Windows (<c>WNetAddConnection2</c>)
/// sit in a blocking call for their whole timeout, and the Cancel beside the chip seemed dead until
/// then (TRAP-96). The step is left to end by itself: what it still brings back is closed, and its
/// failure is nobody's news. A panel's listing waits the same way: a file system may be just as deaf.
/// </summary>
public static class ConnectWait
{
    /// <param name="step">The step, already started. It closes on its own whatever it opened and failed with.</param>
    /// <param name="ct">The user's Cancel.</param>
    /// <param name="discard">Closes what an abandoned step brought back; runs in the pool, and handles its own failures.</param>
    /// <returns>What the step returned.</returns>
    /// <exception cref="OperationCanceledException">Cancelled, whether or not the step has ended.</exception>
    public static async Task<T> UntilCancelledAsync<T>(Task<T> step, CancellationToken ct, Action<T>? discard = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        try
        {
            return await step.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            _ = step.ContinueWith(
                ended =>
                {
                    if (ended.IsCompletedSuccessfully)
                    {
                        discard?.Invoke(ended.Result);
                    }
                    else
                    {
                        _ = ended.Exception;   // read, so it does not come back as an unobserved one
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
            throw;
        }
    }
}
