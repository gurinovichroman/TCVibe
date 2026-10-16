using TCV.Core.Interfaces;

namespace TCV.App.Sync;

/// <summary>How one entry compares between the two sides being synchronised.</summary>
public enum ComparisonStatus
{
    /// <summary>Same name, same size, same timestamp within tolerance.</summary>
    Same,
    /// <summary>Present on the left only.</summary>
    OnlyLeft,
    /// <summary>Present on the right only.</summary>
    OnlyRight,
    /// <summary>On both sides, and the left copy is the newer one.</summary>
    LeftNewer,
    /// <summary>On both sides, and the right copy is the newer one.</summary>
    RightNewer,
    /// <summary>Same timestamp, different size or content — neither side can be called newer.</summary>
    Different,
    /// <summary>
    /// A folder that could not be listed on one of the sides. Nothing below it was compared, and it is
    /// never offered for copying: "could not read" must not look like "is empty".
    /// </summary>
    Unreadable,
}

/// <summary>Which way a row would be copied if the user synchronised.</summary>
public enum SyncDirection
{
    /// <summary>Leave it alone.</summary>
    None,
    /// <summary>Copy left → right.</summary>
    ToRight,
    /// <summary>Copy right → left.</summary>
    ToLeft,
}

/// <summary>
/// One row of a directory comparison: a relative path and what each side has for it.
/// </summary>
/// <param name="RelativePath">Slash-separated path relative to both compared roots.</param>
/// <param name="Left">The entry on the left, or null when it is missing there.</param>
/// <param name="Right">The entry on the right, or null when it is missing there.</param>
/// <param name="Status">The verdict.</param>
/// <param name="IsDirectory">True when both sides agree this is a directory.</param>
/// <param name="CaseVariant">True when a side holds several entries whose names differ only by case
/// (possible on a Linux server). They cannot all live on a Windows disk, so the row is never ticked
/// by default and the user has to look at it.</param>
public sealed record ComparisonRow(
    string RelativePath,
    IFileEntry? Left,
    IFileEntry? Right,
    ComparisonStatus Status,
    bool IsDirectory,
    bool CaseVariant = false)
{
    /// <summary>Name shown in the grid — the last segment of the relative path.</summary>
    public string Name
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? RelativePath : RelativePath[(slash + 1)..];
        }
    }

    /// <summary>Directory part of the relative path, empty at the compared root.</summary>
    public string RelativeDirectory
    {
        get
        {
            var slash = RelativePath.LastIndexOf('/');
            return slash < 0 ? string.Empty : RelativePath[..slash];
        }
    }

    /// <summary>The direction this row would be copied by default, given the status.</summary>
    public SyncDirection DefaultDirection => CaseVariant ? SyncDirection.None : Status switch
    {
        ComparisonStatus.OnlyLeft => SyncDirection.ToRight,
        ComparisonStatus.OnlyRight => SyncDirection.ToLeft,
        ComparisonStatus.LeftNewer => SyncDirection.ToRight,
        ComparisonStatus.RightNewer => SyncDirection.ToLeft,
        // "Different" means same timestamp, different size: which one wins is a judgement call
        // the tool must not make silently. "Unreadable" has nothing to copy.
        _ => SyncDirection.None,
    };

    /// <summary>Whether a copy in <paramref name="direction"/> has a source to copy from.</summary>
    public bool CanCopy(SyncDirection direction) => Status != ComparisonStatus.Unreadable && direction switch
    {
        SyncDirection.ToRight => Left is not null,
        SyncDirection.ToLeft => Right is not null,
        _ => true,
    };
}

/// <summary>Options for one comparison pass.</summary>
public sealed record ComparisonOptions
{
    /// <summary>Timestamp tolerance when either side is a remote server: FTP rounds to the minute.</summary>
    public static readonly TimeSpan RemoteTimeTolerance = TimeSpan.FromSeconds(61);

    /// <summary>Descend into subdirectories.</summary>
    public bool Recursive { get; init; } = true;

    /// <summary>
    /// Timestamps closer together than this count as equal. FAT stores two-second resolution and
    /// FTP servers round to the minute (<see cref="RemoteTimeTolerance"/>), so an exact comparison
    /// reports every file as different.
    /// </summary>
    public TimeSpan TimeTolerance { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Include rows whose sides are identical. Off by default — noise in a sync view.</summary>
    public bool IncludeSame { get; init; }

    /// <summary>
    /// Compare the bytes of files of equal size, not just their timestamps. Two git worktrees carry
    /// their checkout times: without this every file looks "newer" on one side. Reads both copies (with
    /// an early exit at the first difference), so it is off by default.
    /// </summary>
    public bool CompareContent { get; init; }

    /// <summary>Directory nesting limit, counted from the compared roots.</summary>
    public int MaxDepth { get; init; } = 32;

    /// <summary>Directories never entered.</summary>
    public IReadOnlyCollection<string> SkipDirectories { get; init; } = [".git", ".hg", ".svn"];
}

/// <summary>
/// Compares two directory listings. Pure: it is given the entries, not a file system, so the
/// whole verdict table is unit-testable and the same logic serves disk, archive and FTP (6.23).
/// </summary>
public static class DirectoryComparison
{
    /// <summary>
    /// Compares one directory level. <paramref name="prefix"/> is the relative path of that level,
    /// used to build each row's <see cref="ComparisonRow.RelativePath"/>.
    /// </summary>
    /// <remarks>
    /// Names are paired exactly first, and only then by case-insensitive name, so a folder holding
    /// both <c>Readme</c> and <c>README</c> (legal on a Linux server) is two entries, not a crash.
    /// Entries that still collide on a case-insensitive target are marked
    /// <see cref="ComparisonRow.CaseVariant"/>.
    /// </remarks>
    public static IReadOnlyList<ComparisonRow> CompareLevel(
        IReadOnlyList<IFileEntry> left,
        IReadOnlyList<IFileEntry> right,
        ComparisonOptions options,
        string prefix = "")
    {
        var rows = new List<ComparisonRow>();

        var leftPartner = Pair(left, right);
        var rightHasPartner = new bool[right.Count];
        foreach (var partner in leftPartner)
        {
            if (partner >= 0)
            {
                rightHasPartner[partner] = true;
            }
        }

        var leftCollides = CollidingNames(left);
        var rightCollides = CollidingNames(right);

        for (var i = 0; i < left.Count; i++)
        {
            var leftEntry = left[i];
            var relative = prefix.Length == 0 ? leftEntry.Name : prefix + "/" + leftEntry.Name;
            var variant = leftCollides.Contains(leftEntry.Name);

            if (leftPartner[i] < 0)
            {
                rows.Add(new ComparisonRow(relative, leftEntry, null,
                    ComparisonStatus.OnlyLeft, leftEntry.IsDirectory, variant));
                continue;
            }

            var rightEntry = right[leftPartner[i]];
            variant |= rightCollides.Contains(rightEntry.Name);

            var status = Compare(leftEntry, rightEntry, options);
            if (status == ComparisonStatus.Same && !options.IncludeSame &&
                !(leftEntry.IsDirectory && rightEntry.IsDirectory))
            {
                continue;
            }

            rows.Add(new ComparisonRow(relative, leftEntry, rightEntry, status,
                leftEntry.IsDirectory && rightEntry.IsDirectory, variant));
        }

        for (var j = 0; j < right.Count; j++)
        {
            if (rightHasPartner[j])
            {
                continue;
            }

            var rightEntry = right[j];
            var relative = prefix.Length == 0 ? rightEntry.Name : prefix + "/" + rightEntry.Name;
            rows.Add(new ComparisonRow(relative, null, rightEntry,
                ComparisonStatus.OnlyRight, rightEntry.IsDirectory, rightCollides.Contains(rightEntry.Name)));
        }

        rows.Sort(static (a, b) =>
        {
            // Directories first, then by name — the order every file manager uses.
            var byKind = (a.IsDirectory ? 0 : 1).CompareTo(b.IsDirectory ? 0 : 1);
            if (byKind != 0)
            {
                return byKind;
            }

            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.CompareOrdinal(a.Name, b.Name);
        });
        return rows;
    }

    /// <summary>For each left entry, the index of its right partner, or -1. Exact names win over case-only matches.</summary>
    private static int[] Pair(IReadOnlyList<IFileEntry> left, IReadOnlyList<IFileEntry> right)
    {
        var partner = new int[left.Count];
        Array.Fill(partner, -1);
        var taken = new bool[right.Count];

        var exact = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var folded = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (var j = 0; j < right.Count; j++)
        {
            Add(exact, right[j].Name, j);
            Add(folded, right[j].Name, j);
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!exact.TryGetValue(left[i].Name, out var candidates))
            {
                continue;
            }

            foreach (var j in candidates)
            {
                if (taken[j])
                {
                    continue;
                }

                partner[i] = j;
                taken[j] = true;
                break;
            }
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (partner[i] >= 0 || !folded.TryGetValue(left[i].Name, out var candidates))
            {
                continue;
            }

            foreach (var j in candidates)
            {
                if (taken[j])
                {
                    continue;
                }

                partner[i] = j;
                taken[j] = true;
                break;
            }
        }

        return partner;

        static void Add(Dictionary<string, List<int>> map, string key, int index)
        {
            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = new List<int>(1);
            }

            list.Add(index);
        }
    }

    /// <summary>Names that another entry of the same listing shares, ignoring case.</summary>
    private static HashSet<string> CollidingNames(IReadOnlyList<IFileEntry> entries)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in entries.GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2)
            {
                continue;
            }

            foreach (var entry in group)
            {
                result.Add(entry.Name);
            }
        }
        return result;
    }

    /// <summary>The verdict for a pair of entries that exist on both sides.</summary>
    public static ComparisonStatus Compare(IFileEntry left, IFileEntry right, ComparisonOptions options)
    {
        // A directory present on both sides is "same" at this level; its contents are compared
        // by descending into it, not by looking at its size or timestamp.
        if (left.IsDirectory || right.IsDirectory)
        {
            return left.IsDirectory && right.IsDirectory
                ? ComparisonStatus.Same
                : ComparisonStatus.Different;   // a file on one side, a directory on the other
        }

        var delta = TimeDifference(left.LastModified, right.LastModified);
        var sameTime = delta.Duration() <= options.TimeTolerance;
        var sameSize = left.Size == right.Size;

        if (sameTime)
        {
            return sameSize ? ComparisonStatus.Same : ComparisonStatus.Different;
        }

        return delta > TimeSpan.Zero ? ComparisonStatus.LeftNewer : ComparisonStatus.RightNewer;
    }

    /// <summary>
    /// <paramref name="left"/> minus <paramref name="right"/> as moments in time. Subtracting two
    /// <see cref="DateTime"/>s ignores their <see cref="DateTime.Kind"/>, so a UTC time from a server
    /// against a local time from the disk came out hours off — and the older file won the sync.
    /// </summary>
    public static TimeSpan TimeDifference(DateTime left, DateTime right) =>
        left.ToUniversalTime() - right.ToUniversalTime();

    /// <summary>Counts by status, for the summary line above the comparison grid.</summary>
    public static IReadOnlyDictionary<ComparisonStatus, int> Summarize(IEnumerable<ComparisonRow> rows)
    {
        var counts = new Dictionary<ComparisonStatus, int>();
        foreach (var row in rows)
        {
            counts[row.Status] = counts.GetValueOrDefault(row.Status) + 1;
        }

        return counts;
    }
}
