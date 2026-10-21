namespace TCV.App.Services;

/// <summary>
/// Tells cancelled work that nobody awaited from a failure, for the UI thread's last-chance handler.
/// FluentFTP before 53 closed its streams from async void on the token of every socket read: the thread
/// that cancelled the token ran it, and its «Cancelled read from socket stream» landed there past every
/// await — after Cancel in a copy's progress window it came up as an «Unexpected error» box (TRAP-93).
/// </summary>
public static class CancelledWork
{
    /// <summary>
    /// True for a cancellation: the user asked for the stop, there is nothing to report. A timeout
    /// dressed as one (HttpClient's <see cref="TaskCanceledException"/> around a <see cref="TimeoutException"/>)
    /// is a failure and still reaches the user.
    /// </summary>
    public static bool IsCancellation(Exception ex) =>
        ex is OperationCanceledException { InnerException: not TimeoutException };
}
