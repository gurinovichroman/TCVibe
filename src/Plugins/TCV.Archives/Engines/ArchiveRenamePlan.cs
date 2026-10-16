namespace TCV.Archives.Engines;

/// <summary>
/// Which stored entries a batch of renames touches and the names they are stored under afterwards,
/// worked out before anything is written. A rename names an entry by its path, in any spelling the
/// engines accept (slashes either way, a leading <c>./</c>, another case when the exact one is not
/// there), and the new last segment of that path. A directory takes everything inside it along, whether
/// the archive stores an entry for the directory or only the files below it.
/// <para>
/// The batch applies at once: every entry is renamed from the name it has now, so a swap or a chain
/// (<c>a → b</c>, <c>b → c</c>) needs no temporary name, and a file renamed inside a folder that is renamed
/// too ends up in the folder's new name. A name that would be taken afterwards (by an entry the batch
/// leaves alone, or by another rename), an entry that is not there or a name that is not one refuses the
/// whole batch: archives are rewritten all or nothing.
/// </para>
/// </summary>
public sealed class ArchiveRenamePlan
{
    private ArchiveRenamePlan(IReadOnlyList<(int Index, string StoredName)> changes) => Changes = changes;

    /// <summary>
    /// The entries that change: the index into the stored names the plan was built from, and the name to
    /// store the entry under. A directory entry keeps its trailing slash; separators become forward slashes.
    /// </summary>
    public IReadOnlyList<(int Index, string StoredName)> Changes { get; }

    /// <param name="storedNames">Every entry name as the archive stores it, in stored order (duplicates allowed).</param>
    /// <param name="renames">The entries to rename and the new name of each.</param>
    /// <exception cref="ArgumentException">A new name that is no name, the archive root, or an entry renamed twice.</exception>
    /// <exception cref="FileNotFoundException">An entry the archive does not have.</exception>
    /// <exception cref="IOException">A name that would be taken after the batch.</exception>
    public static ArchiveRenamePlan Build(
        IReadOnlyList<string> storedNames, IReadOnlyList<(string EntryPath, string NewName)> renames)
    {
        var normalized = storedNames.Select(EntryListBuilder.NormalizePath).ToArray();

        // Every path the archive has, the directories it only implies included: "a/b/c.txt" alone is
        // also the folders "a" and "a/b", and those can be renamed (and collided with) like any entry.
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in normalized.Where(p => p.Length > 0))
        {
            nodes.Add(path);
            for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
            {
                nodes.Add(path[..slash]);
            }
        }

        var newNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (entryPath, newName) in renames)
        {
            ValidateName(newName);
            var wanted = EntryListBuilder.NormalizePath(entryPath);
            if (wanted.Length == 0)
            {
                throw new ArgumentException("The archive root cannot be renamed.", nameof(renames));
            }

            // Two names that differ only by case are distinct entries in a ZIP: the exact one first, as
            // DeleteEntries matches, and another case only when the exact one is not there.
            var source = nodes.Contains(wanted)
                ? wanted
                : nodes.FirstOrDefault(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase))
                  ?? throw new FileNotFoundException($"Entry '{entryPath}' not found in the archive.", entryPath);

            if (!newNames.TryAdd(source, newName))
            {
                throw new ArgumentException($"Entry '{entryPath}' is renamed twice in one batch.", nameof(renames));
            }
        }

        // Where every path ends up: each segment whose path (as it was) is renamed takes its new name.
        string Final(string path)
        {
            var segments = path.Split('/');
            var prefix = string.Empty;
            for (var i = 0; i < segments.Length; i++)
            {
                prefix = i == 0 ? segments[0] : prefix + "/" + segments[i];
                if (newNames.TryGetValue(prefix, out var renamed))
                {
                    segments[i] = renamed;
                }
            }

            return string.Join('/', segments);
        }

        // Taken afterwards: two different paths that end up under one name. Windows cannot hold both on
        // extraction, so case does not tell them apart. Names that already collided before the batch (a
        // ZIP may hold "Readme.txt" and "readme.txt") are not this batch's doing and are left alone.
        var finals = nodes.Select(n => (Node: n, Final: Final(n))).ToList();
        foreach (var group in finals.GroupBy(f => f.Final, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            if (group.FirstOrDefault(f => !string.Equals(f.Node, f.Final, StringComparison.Ordinal)) is { Node: not null } moved)
            {
                throw new IOException($"The name '{moved.Final}' is already taken in the archive.");
            }
        }

        var changes = new List<(int, string)>();
        for (var i = 0; i < storedNames.Count; i++)
        {
            if (normalized[i].Length == 0)
            {
                continue;
            }

            var final = Final(normalized[i]);
            if (!string.Equals(final, normalized[i], StringComparison.Ordinal))
            {
                var isDirectory = storedNames[i].EndsWith('/') || storedNames[i].EndsWith('\\');
                changes.Add((i, isDirectory ? final + "/" : final));
            }
        }

        return new ArchiveRenamePlan(changes);
    }

    /// <summary>
    /// A name for one segment: not empty, no separator (a rename stays in its folder), not "." or "..".
    /// What Windows cannot hold is the caller's to refuse; an archive can store it.
    /// </summary>
    private static void ValidateName(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName is "." or ".."
            || newName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException($"'{newName}' is not a name an entry can be renamed to.", nameof(newName));
        }
    }
}
