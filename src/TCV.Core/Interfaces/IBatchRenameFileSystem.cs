using TCV.Core.Domain;

namespace TCV.Core.Interfaces;

/// <summary>
/// A file system that renames a batch in one step, because one rename at a time would cost it the whole
/// work again for every entry: an archive is rewritten once per call. The renames apply at once — each
/// entry goes from the name it has to its new name in its own folder — so a swap or a chain needs no
/// temporary name. Either all of them happen or none does, and then the call throws.
/// </summary>
public interface IBatchRenameFileSystem
{
    /// <summary>
    /// False when nothing can be renamed here (an archive format that cannot be written), or for a view
    /// whose file system underneath does not rename in batches. Callers then rename one entry at a time.
    /// </summary>
    bool CanRenameAsBatch { get; }

    /// <summary>Renames every entry in <paramref name="renames"/> to its new name, all or nothing.</summary>
    Task RenameBatchAsync(IReadOnlyList<(VfsPath Path, string NewName)> renames, CancellationToken ct);
}
