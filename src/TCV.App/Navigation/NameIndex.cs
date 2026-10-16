using System.IO;
using System.Text;
using System.Text.Json;

namespace TCV.App.Navigation;

/// <summary>One indexed name: enough to show a row and navigate to it, and nothing more.</summary>
/// <param name="Path">Full path on disk.</param>
/// <param name="Name">File or directory name.</param>
/// <param name="IsDirectory">True for a directory.</param>
public readonly record struct NameIndexEntry(string Path, string Name, bool IsDirectory)
{
    /// <summary>Directory part, for the secondary line of a result row.</summary>
    public string Directory
    {
        get
        {
            var slash = Path.LastIndexOfAny(['\\', '/']);
            return slash <= 0 ? string.Empty : Path[..slash];
        }
    }
}

/// <summary>
/// An in-memory name index over one or more roots, answering substring queries instantly — the
/// "Everything" half of task 6.21.
/// </summary>
/// <remarks>
/// Deliberately not an NTFS/USN-journal index: reading the master file table needs administrator
/// rights, which a portable tool must not require. Instead the roots are walked once in the
/// background and the result is persisted, so a restart is instant and a rescan is explicit.
/// <para>
/// The query path is a linear scan over a pre-lowercased name array. At the scale this index is
/// meant for (a few hundred thousand entries) that is a fraction of a millisecond per keystroke
/// and needs no trigram structure to maintain — measure before adding one.
/// </para>
/// </remarks>
public sealed class NameIndex
{
    private readonly List<NameIndexEntry> _entries = new();

    /// <summary>Lowercased names, parallel to <see cref="_entries"/>, so matching allocates nothing.</summary>
    private readonly List<string> _lowerNames = new();

    private readonly Lock _gate = new();

    /// <summary>Roots the current content was built from.</summary>
    public IReadOnlyList<string> Roots { get; private set; } = Array.Empty<string>();

    /// <summary>When the index was last rebuilt, in UTC; default when it never was.</summary>
    public DateTime BuiltUtc { get; private set; }

    /// <summary>True when the walk stopped at the entry cap, so deep folders are missing.</summary>
    public bool IsTruncated { get; private set; }

    public int Count
    {
        get { lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Replaces the whole content. Called once a rebuild finishes.</summary>
    public void Replace(
        IReadOnlyList<NameIndexEntry> entries, IReadOnlyList<string> roots, DateTime builtUtc,
        bool truncated = false)
    {
        lock (_gate)
        {
            _entries.Clear();
            _lowerNames.Clear();
            _entries.AddRange(entries);
            foreach (var entry in entries)
            {
                _lowerNames.Add(entry.Name.ToLowerInvariant());
            }

            Roots = roots;
            BuiltUtc = builtUtc;
            IsTruncated = truncated;
        }
    }

    /// <summary>
    /// Swaps everything below <paramref name="directory"/> (the directory's own entry included when
    /// it is in <paramref name="replacement"/>) for a fresh listing. This is how a copy or a new
    /// folder made in the manager shows up in the name search without a whole-disk rescan.
    /// </summary>
    public void ReplaceSubtree(string directory, IReadOnlyList<NameIndexEntry> replacement)
    {
        var self = directory.TrimEnd((char)92, '/');
        var prefix = self + (char)92;

        Swap(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(path, self, StringComparison.OrdinalIgnoreCase),
             replacement);
    }

    /// <summary>
    /// Swaps only the direct children of <paramref name="directory"/> for a fresh listing, keeping
    /// everything deeper. Used when the folder is too big to rescan whole.
    /// </summary>
    public void ReplaceChildren(string directory, IReadOnlyList<NameIndexEntry> children)
    {
        var self = directory.TrimEnd((char)92, '/');
        Swap(path =>
        {
            var slash = path.LastIndexOfAny([(char)92, '/']);
            return slash > 0 && string.Equals(path[..slash], self, StringComparison.OrdinalIgnoreCase);
        }, children);
    }

    private void Swap(Func<string, bool> remove, IReadOnlyList<NameIndexEntry> replacement)
    {
        lock (_gate)
        {
            var keptEntries = new List<NameIndexEntry>(_entries.Count);
            var keptNames = new List<string>(_entries.Count);
            for (var i = 0; i < _entries.Count; i++)
            {
                if (remove(_entries[i].Path))
                {
                    continue;
                }

                keptEntries.Add(_entries[i]);
                keptNames.Add(_lowerNames[i]);
            }

            foreach (var entry in replacement)
            {
                keptEntries.Add(entry);
                keptNames.Add(entry.Name.ToLowerInvariant());
            }

            _entries.Clear();
            _lowerNames.Clear();
            _entries.AddRange(keptEntries);
            _lowerNames.AddRange(keptNames);
        }
    }

    /// <summary>
    /// Names containing <paramref name="query"/>, best first: a prefix match beats a match in the
    /// middle, a shorter name beats a longer one, and the rest is alphabetical so the order is
    /// stable between keystrokes.
    /// </summary>
    /// <remarks>
    /// Runs on the UI thread on every keystroke, so it keeps only the best <paramref name="limit"/>
    /// hits in a bounded heap (O(n log k)) instead of sorting every match: one letter over four
    /// hundred thousand names used to sort a couple of hundred thousand tuples per keypress.
    /// </remarks>
    public IReadOnlyList<NameIndexEntry> Query(string? query, int limit = 200)
    {
        if (limit <= 0)
        {
            return Array.Empty<NameIndexEntry>();
        }

        var needle = query?.Trim().ToLowerInvariant() ?? string.Empty;

        lock (_gate)
        {
            if (needle.Length == 0)
            {
                return _entries.Take(limit).ToList();
            }

            // Max-heap on "worse": the root is the worst hit kept so far, and is evicted first.
            var heap = new PriorityQueue<Hit, Hit>(Comparer<Hit>.Create(static (a, b) => Compare(b, a)));
            for (var i = 0; i < _entries.Count; i++)
            {
                var name = _lowerNames[i];
                var at = name.IndexOf(needle, StringComparison.Ordinal);
                if (at < 0)
                {
                    continue;
                }

                var hit = new Hit(at, name.Length, _entries[i]);
                if (heap.Count < limit)
                {
                    heap.Enqueue(hit, hit);
                }
                else if (Compare(hit, heap.Peek()) < 0)
                {
                    heap.Dequeue();
                    heap.Enqueue(hit, hit);
                }
            }

            var result = new NameIndexEntry[heap.Count];
            for (var i = result.Length - 1; i >= 0; i--)
            {
                result[i] = heap.Dequeue().Entry;
            }

            return result;
        }
    }

    private readonly record struct Hit(int Rank, int Length, NameIndexEntry Entry);

    private static int Compare(Hit a, Hit b)
    {
        var byRank = a.Rank.CompareTo(b.Rank);
        if (byRank != 0)
        {
            return byRank;
        }

        var byLength = a.Length.CompareTo(b.Length);
        return byLength != 0
            ? byLength
            : string.Compare(a.Entry.Name, b.Entry.Name, StringComparison.OrdinalIgnoreCase);
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>
    /// Serialised form. A compact record rather than the runtime one: the lowercased names are
    /// derived and re-deriving them on load is cheaper than storing a second copy of every name.
    /// </summary>
    private sealed class Snapshot
    {
        public List<string> Roots { get; set; } = new();
        public DateTime BuiltUtc { get; set; }
        public bool Truncated { get; set; }
        /// <summary>One line per entry: <c>D</c> or <c>F</c>, then the full path.</summary>
        public List<string> Entries { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Writes the index next to the executable (INV-03). False when it could not be written.</summary>
    public bool Save(string filePath)
    {
        try
        {
            Snapshot snapshot;
            lock (_gate)
            {
                snapshot = new Snapshot
                {
                    Roots = Roots.ToList(),
                    BuiltUtc = BuiltUtc,
                    Truncated = IsTruncated,
                    Entries = _entries.Select(e => (e.IsDirectory ? "D" : "F") + e.Path).ToList(),
                };
            }

            // Temp + move: a crash mid-write must not leave a half-written cache that the next start
            // would reject and rebuild from scratch.
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions), Encoding.UTF8);
            File.Move(temp, filePath, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;   // a cache that cannot be written is a slower start, not a failure
        }
    }

    /// <summary>Loads a previously saved index; false when there is nothing usable to load.</summary>
    public bool Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return false;
            }

            var snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(filePath), JsonOptions);
            if (snapshot is null || snapshot.Entries.Count == 0)
            {
                return false;
            }

            var entries = new List<NameIndexEntry>(snapshot.Entries.Count);
            foreach (var line in snapshot.Entries)
            {
                if (line.Length < 2)
                {
                    continue;
                }

                var path = line[1..];
                var slash = path.LastIndexOfAny([(char)92, '/']);
                var name = slash < 0 ? path : path[(slash + 1)..];
                entries.Add(new NameIndexEntry(path, name, line[0] == 'D'));
            }

            Replace(entries, snapshot.Roots, snapshot.BuiltUtc, snapshot.Truncated);
            return true;
        }
        catch (Exception)
        {
            return false;   // a corrupt cache is a rebuild, not a crash
        }
    }
}
