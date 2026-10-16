using System.Text.Json.Serialization;

namespace TCV.App.Operations;

/// <summary>One rename a batch performed: the name the file had and the name it was given.</summary>
public sealed record RenamedName(string From, string To);

/// <summary>
/// What a <see cref="RenameOperation"/> did, written into its operation-log line so the batch can be
/// taken back after the window that applied it is gone (ADR-0008: undo after; 6.55).
/// </summary>
/// <param name="Directory">The folder, as a VFS path (<c>C:/dl</c>).</param>
/// <param name="Pairs">The renames that happened, in the order they were recorded.</param>
public sealed record RenameBatchRecord(string Directory, IReadOnlyList<RenamedName> Pairs)
{
    /// <summary>
    /// True when the record names a folder and at least one rename. A hand-edited or truncated log
    /// line can lack either, and such a line offers nothing to take back.
    /// </summary>
    [JsonIgnore]
    public bool IsUsable => !string.IsNullOrWhiteSpace(Directory) && Pairs is { Count: > 0 };
}

/// <summary>
/// Taking a logged rename batch back, worked out against the folder as it is now and before anything
/// is touched. Time has passed since the batch: a file may have been renamed again, moved or deleted,
/// and another file may have taken an original name. Those are left alone and counted, so the
/// question the user is asked says exactly what will happen (INV-11).
/// </summary>
public sealed class RenameUndoPlan
{
    private RenameUndoPlan(
        IReadOnlyList<(string From, string To)> pairs, IReadOnlyList<string> missing, IReadOnlyList<string> blocked)
    {
        Pairs = pairs;
        Missing = missing;
        Blocked = blocked;
    }

    /// <summary>The renames to perform: the name the batch gave a file back to the one it had.</summary>
    public IReadOnlyList<(string From, string To)> Pairs { get; }

    /// <summary>New names no longer in the folder (renamed again, moved or deleted since).</summary>
    public IReadOnlyList<string> Missing { get; }

    /// <summary>New names whose original is now taken by another file, which must not be overwritten.</summary>
    public IReadOnlyList<string> Blocked { get; }

    /// <summary>How many of the batch's renames cannot be taken back.</summary>
    public int SkippedCount => Missing.Count + Blocked.Count;

    /// <param name="performed">The batch's renames, as recorded.</param>
    /// <param name="namesInFolder">Every name in the folder now — files and folders alike.</param>
    public static RenameUndoPlan Build(IEnumerable<RenamedName?> performed, IEnumerable<string> namesInFolder)
    {
        var present = new HashSet<string>(namesInFolder, StringComparer.OrdinalIgnoreCase);
        var renames = performed
            .Where(p => p is { From.Length: > 0, To.Length: > 0 })
            .Select(p => p!)
            .ToList();

        var missing = renames.Where(p => !present.Contains(p.To)).Select(p => p.To).ToList();
        var candidates = renames.Where(p => present.Contains(p.To)).ToList();
        var blocked = new List<string>();

        // An original name that is in the folder now is free only if a file being renamed back is
        // what holds it — a chain (a→b, b→c) or a swap, which RenameOperation parks through a temporary
        // name; a case-only change holds its own original. Dropping one rename can take that cover away
        // from another, hence the loop until nothing changes.
        while (true)
        {
            var vacated = new HashSet<string>(candidates.Select(p => p.To), StringComparer.OrdinalIgnoreCase);
            var stuck = candidates.Where(p => present.Contains(p.From) && !vacated.Contains(p.From)).ToList();
            if (stuck.Count == 0)
            {
                break;
            }

            foreach (var p in stuck)
            {
                candidates.Remove(p);
                blocked.Add(p.To);
            }
        }

        return new RenameUndoPlan(
            candidates.Select(p => (From: p.To, To: p.From)).ToList(), missing, blocked);
    }
}
