using TCV.App.Sync;
using TCV.Core.Interfaces;

namespace TCV.App.Operations.Conflict;

/// <summary>
/// What becomes of a file whose name is taken at the destination, by the rules of the copy dialog.
/// "Only if newer" decides on its own: a newer file replaces the one there without asking, an older
/// or equal one is left alone. Otherwise the strategy picked in the dialog answers, and its "overwrite
/// if newer" is settled here. One rule for every copy that writes files — between file systems, out
/// of an archive and into one — so F5 means the same wherever it is pressed (TRAP-58).
/// </summary>
public static class ConflictPolicy
{
    /// <summary>
    /// Never <see cref="ConflictResolution.OverwriteIfNewer"/>: that one comes back as
    /// <see cref="ConflictResolution.Overwrite"/> or <see cref="ConflictResolution.Skip"/>.
    /// </summary>
    /// <param name="ask">The strategy, or a gate in front of it; only called when "only if newer" is off.</param>
    /// <param name="tolerance">Time differences up to this count as equal (a ZIP keeps two-second times).</param>
    public static async Task<ConflictResolution> ResolveAsync(
        ConflictInfo conflict,
        bool onlyIfNewer,
        Func<ConflictInfo, CancellationToken, Task<ConflictResolution>> ask,
        CancellationToken ct,
        TimeSpan tolerance = default)
    {
        // Folders never take part: their own timestamp says nothing about the files inside.
        if (onlyIfNewer && !conflict.Source.IsDirectory)
        {
            return IsNewer(conflict.Source, conflict.Existing, tolerance)
                ? ConflictResolution.Overwrite
                : ConflictResolution.Skip;
        }

        var resolution = await ask(conflict, ct);
        if (resolution != ConflictResolution.OverwriteIfNewer)
        {
            return resolution;
        }

        return IsNewer(conflict.Source, conflict.Existing, tolerance)
            ? ConflictResolution.Overwrite
            : ConflictResolution.Skip;
    }

    /// <summary>
    /// True when <paramref name="source"/> was changed later than <paramref name="existing"/>, compared
    /// as moments in time: a server's UTC time against the disk's local one (TRAP-48).
    /// </summary>
    public static bool IsNewer(IFileEntry source, IFileEntry existing, TimeSpan tolerance = default) =>
        DirectoryComparison.TimeDifference(source.LastModified, existing.LastModified) > tolerance;
}
