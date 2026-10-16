namespace TCV.Core.Interfaces;

/// <summary>
/// A write stream that only collects what is written and uploads all of it once it is complete — a
/// WebDAV PUT has to state its length up front, so the body goes to a temporary file first. Every byte
/// therefore travels twice, and the second pass, over the network, is usually the slow one. A caller
/// that shows progress uploads explicitly with <see cref="CommitAsync"/> instead of leaving it to
/// dispose, so that its bar follows the upload rather than reaching 100 % before the upload starts.
/// </summary>
/// <remarks>
/// A caller that knows nothing of this interface keeps working: disposing the stream uploads too,
/// just without progress (and <see cref="IAbortableWriteStream.Abort"/> still discards the body).
/// </remarks>
public interface IDeferredUploadStream : IAbortableWriteStream
{
    /// <summary>
    /// Uploads what was written, reporting the bytes sent so far. The count can start over from zero
    /// when the server has the body sent a second time (after asking for credentials, for instance).
    /// Throws when the upload fails or <paramref name="ct"/> is cancelled; either way the temporary
    /// copy is released, and disposing the stream afterwards sends nothing.
    /// </summary>
    Task CommitAsync(IProgress<long>? progress, CancellationToken ct);
}
