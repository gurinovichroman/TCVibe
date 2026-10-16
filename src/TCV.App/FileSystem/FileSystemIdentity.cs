using TCV.Core.Interfaces;

namespace TCV.App.FileSystem;

/// <summary>
/// Which file system an entry really lives on. A view over another file system — the search-results
/// list (<see cref="SearchResultsFileSystem"/>) — is that file system for every question an operation
/// asks: whether source and destination are one (INV-08: a system copy or a rename instead of
/// streaming, the "folder into itself" check) and whether its paths are Windows paths. Asked by
/// object identity alone, F6 from the results to a disk folder streamed every byte and then deleted
/// the source, and a folder could be copied into itself (ADR-0028).
/// </summary>
public static class FileSystemIdentity
{
    /// <summary>The file system whose files <paramref name="fs"/> shows: itself, or the one under a view.</summary>
    public static IVirtualFileSystem Underlying(IVirtualFileSystem fs) =>
        fs is SearchResultsFileSystem results ? Underlying(results.Inner) : fs;

    /// <summary>True when both show the files of one file system, so a rename or a system copy can join them.</summary>
    public static bool Same(IVirtualFileSystem a, IVirtualFileSystem b) =>
        ReferenceEquals(Underlying(a), Underlying(b));

    /// <summary>
    /// True when the entries are files of the Windows disk, so their paths survive <c>ToWindowsPath</c>
    /// and BCL calls (timestamps, read-only flags, volumes) mean something.
    /// </summary>
    public static bool IsLocalDisk(IVirtualFileSystem fs) =>
        string.Equals(Underlying(fs).Scheme, "file", StringComparison.OrdinalIgnoreCase);
}
