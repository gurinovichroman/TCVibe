using System.Runtime.ExceptionServices;

namespace TCV.Network;

/// <summary>
/// Sends the parts of one large transfer a few at a time, and each part again when the line failed:
/// the S3 parts and copied parts, the Azure blocks. Results come back in part order, whatever order
/// the parts finished in, because that is the order the store has to be told at the end.
/// </summary>
/// <remarks>
/// No bytes are held here. A part is read from the upload's temporary file as it is sent, one buffer
/// at a time (<see cref="FileSegmentStream"/>), and read from its start again by each new try, so what
/// a transfer keeps in memory is one buffer per part in flight.
/// </remarks>
internal static class PartedTransfer
{
    /// <summary>Parts in flight at once: faster than one on a fast line, and not a storm on a slow one.</summary>
    public const int DefaultConcurrency = 4;

    /// <summary>
    /// Three more tries of a part, after 1, 2 and 4 s (as the cloud drives wait when throttled). A
    /// hiccup or "slow down" passes in that time; beyond it the user decides, with the error in front of them.
    /// </summary>
    public static ReconnectPolicy DefaultRetries { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)]);

    /// <summary>
    /// How long a part may go without a byte moving, the wait for the answer included. A server that
    /// goes silent holds a socket that looks alive: without a limit the try never ends, and the next one
    /// never starts. Long, because a slow line and a store that takes its time are no faults; each buffer
    /// taken by the socket starts it over, so it says nothing about the size of the part or the speed.
    /// </summary>
    public static readonly TimeSpan DefaultStallLimit = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs one try under <paramref name="limit"/>. Nobody asked for the cancellation it ends with, so
    /// <see cref="WithRetriesAsync{T}"/> takes it for the timeout it is (TRAP-93), and tries again.
    /// </summary>
    public static async Task<T> WithStallLimitAsync<T>(TimeSpan limit, Func<PartWatch, Task<T>> attempt, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        return await attempt(new PartWatch(timeout, limit));
    }

    /// <summary>
    /// Runs <paramref name="sendPart"/> for parts 0 to <paramref name="count"/> − 1, at most
    /// <paramref name="concurrency"/> at once. Waits for every part it started before it returns or
    /// throws, so nothing is still in flight when the caller aborts the upload. The first failure is the
    /// one thrown: the parts cancelled because of it come after it.
    /// </summary>
    public static async Task<T[]> RunAsync<T>(
        int count, int concurrency, ReconnectPolicy retries, Func<int, CancellationToken, Task<T>> sendPart, CancellationToken ct)
    {
        var results = new T[count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, concurrency),
            CancellationToken = ct,
            TaskScheduler = TaskScheduler.Default,
        };
        await Parallel.ForEachAsync(Enumerable.Range(0, count), options, async (index, token) =>
        {
            results[index] = await WithRetriesAsync(retries, attempt => sendPart(index, attempt), token);
        });
        return results;
    }

    /// <summary>
    /// Runs <paramref name="attempt"/>, and again after a pause while the failure is one
    /// <see cref="ReconnectPolicy.IsWorthRetrying"/> calls worth it and tries are left. Cancellation
    /// stops it at once and is never tried again.
    /// </summary>
    /// <exception cref="TimeoutException">
    /// A cancellation nobody asked for: a timeout in disguise (TRAP-93). Passed on as what it is, it
    /// would read as "the user cancelled" and a failed upload would go unreported.
    /// </exception>
    public static async Task<T> WithRetriesAsync<T>(
        ReconnectPolicy retries, Func<CancellationToken, Task<T>> attempt, CancellationToken ct)
    {
        for (var retry = 1; ; retry++)
        {
            // TRAP-45: a token already cancelled does not stop a call by itself.
            ct.ThrowIfCancellationRequested();

            Exception failure;
            try
            {
                return await attempt(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                failure = ex is OperationCanceledException
                    ? new TimeoutException("The server stopped answering.", ex)
                    : ex;
            }

            var pause = retries.DelayBefore(retry);
            if (pause is null || !ReconnectPolicy.IsWorthRetrying(failure))
            {
                ExceptionDispatchInfo.Throw(failure);
            }

            await Task.Delay(pause.GetValueOrDefault(), ct);
        }
    }
}

/// <summary>One try under a stall limit: what it runs under, and how it says it is still moving.</summary>
internal sealed class PartWatch(CancellationTokenSource timeout, TimeSpan limit)
{
    public CancellationToken Token { get; } = timeout.Token;

    /// <summary>Starts the limit over. May come from a body still being read after the try has ended.</summary>
    public void Alive()
    {
        try
        {
            timeout.CancelAfter(limit);
        }
        catch (ObjectDisposedException)
        {
            // The try is over: nothing left to watch.
        }
    }
}

/// <summary>
/// One running total for a transfer whose parts go side by side. Reports are in step (a bar must not
/// flicker between threads) and never go down: a part sent again starts its own count over, and the
/// total stands still until the new try has caught up with the old one.
/// </summary>
internal sealed class PartProgress(IProgress<long>? total, int parts)
{
    private readonly long[] _sent = new long[parts];
    private long _sum;
    private long _reported;

    /// <summary>What a part reports its own bytes to; null when nobody listens.</summary>
    public IProgress<long>? For(int part) =>
        total is null ? null : new InlineProgress<long>(bytes => Report(part, bytes));

    private void Report(int part, long bytes)
    {
        lock (_sent)
        {
            _sum += bytes - _sent[part];
            _sent[part] = bytes;
            if (_sum > _reported)
            {
                _reported = _sum;
                total!.Report(_sum);
            }
        }
    }
}
