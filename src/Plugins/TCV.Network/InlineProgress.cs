namespace TCV.Network;

/// <summary>
/// An <see cref="IProgress{T}"/> that runs on the reporting thread. <see cref="Progress{T}"/> posts each
/// report instead — to the thread pool when there is no synchronisation context — so the reports of one
/// transfer could arrive out of order and a bar could step backwards.
/// </summary>
internal sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
