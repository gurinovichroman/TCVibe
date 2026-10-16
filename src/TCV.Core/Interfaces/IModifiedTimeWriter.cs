using TCV.Core.Domain;

namespace TCV.Core.Interfaces;

/// <summary>
/// Optional capability of a file system: stamp a file with a modification time after it was written.
/// A synchronisation needs it — a copy that gets "now" as its time looks newer than its source, so
/// the next comparison flips the arrow and the two sides never settle.
/// </summary>
/// <remarks>
/// Best effort by contract: a server that cannot set times (FTP without MFMT) reports
/// <c>false</c> instead of failing the transfer that just succeeded.
/// </remarks>
public interface IModifiedTimeWriter
{
    /// <summary>Sets the modification time of <paramref name="path"/>. False when the file system cannot.</summary>
    Task<bool> TrySetLastModifiedAsync(VfsPath path, DateTime lastModified, CancellationToken ct = default);
}
