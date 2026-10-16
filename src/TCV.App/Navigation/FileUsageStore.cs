using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCV.App.Navigation;

/// <summary>How often and how recently one path was opened from the file picker.</summary>
public sealed class FileUsageEntry
{
    public string Path { get; set; } = string.Empty;
    public int Hits { get; set; }
    public DateTime LastUsedUtc { get; set; }
}

/// <summary>
/// In-memory snapshot of the usage history, turning it into ranking points. Pure: no disk, no clock
/// of its own — <c>nowUtc</c> is passed in, which is what makes the decay testable.
/// </summary>
public sealed class FileUsageIndex
{
    /// <summary>An index that knows nothing — every path scores 0.</summary>
    public static FileUsageIndex Empty { get; } = new(Array.Empty<FileUsageEntry>(), DateTime.UnixEpoch);

    private readonly Dictionary<string, double> _scores;

    public FileUsageIndex(IEnumerable<FileUsageEntry> entries, DateTime nowUtc)
    {
        _scores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (string.IsNullOrEmpty(e.Path)) continue;
            _scores[Normalize(e.Path)] = Frecency(e.Hits, e.LastUsedUtc, nowUtc);
        }
    }

    /// <summary>Frecency of a path, or 0 when it was never opened from the picker.</summary>
    public double Score(string fullPath) =>
        _scores.TryGetValue(Normalize(fullPath), out var score) ? score : 0;

    /// <summary>
    /// Frequency weighted by recency: the same number of hits counts for more while the memory is
    /// fresh, so yesterday's file outranks one opened twice last spring.
    /// </summary>
    public static double Frecency(int hits, DateTime lastUsedUtc, DateTime nowUtc)
    {
        if (hits <= 0) return 0;

        var age = nowUtc - lastUsedUtc;
        var recency = age switch
        {
            _ when age < TimeSpan.FromHours(1)  => 4.0,
            _ when age < TimeSpan.FromDays(1)   => 3.0,
            _ when age < TimeSpan.FromDays(7)   => 2.0,
            _ when age < TimeSpan.FromDays(30)  => 1.3,
            _                                   => 1.0,
        };

        return hits * recency;
    }

    /// <summary>
    /// Converts frecency into fuzzy-score points. Logarithmic and capped: habit should lift a file
    /// above equally-good matches without ever beating a clearly better one — the prefix bonus in
    /// <see cref="Commands.FuzzyMatcher"/> is 100, and this stays well under it.
    /// </summary>
    public static int HabitBonus(double frecency) =>
        frecency <= 0 ? 0 : Math.Min(MaxHabitBonus, (int)Math.Round(12 * Math.Log(1 + frecency)));

    private const int MaxHabitBonus = 60;

    private static string Normalize(string path) => path.Replace('\\', '/').TrimEnd('/');
}

/// <summary>
/// Portable store of picker usage history (<c>file-usage.json</c> next to the exe, INV-03), modelled
/// on <see cref="Workspaces.WorkspaceStore"/>. A corrupt file degrades to an empty history for reading
/// and is copied aside before the next save; a locked file skips the save instead of being replaced
/// from an empty list (<see cref="Services.JsonListFile"/>). Ranking data is never worth failing a
/// keystroke over.
/// </summary>
public sealed class FileUsageStore
{
    /// <summary>Kept small on purpose — this is a ranking hint, not a history log.</summary>
    public const int MaxEntries = 400;

    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public FileUsageStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("file-usage.json")) { }

    public FileUsageStore(string filePath) => _filePath = filePath;

    public IReadOnlyList<FileUsageEntry> LoadAll()
    {
        Services.JsonListFile.TryRead<FileUsageEntry>(_filePath, JsonOptions, out var list);
        return list;
    }

    /// <summary>Reads the history and turns it into ranking points as of <paramref name="nowUtc"/>.</summary>
    public FileUsageIndex LoadIndex(DateTime nowUtc) => new(LoadAll(), nowUtc);

    /// <summary>
    /// Counts one more open of <paramref name="fullPath"/>. The store keeps the
    /// <see cref="MaxEntries"/> strongest entries by frecency, so it cannot grow without bound.
    /// </summary>
    public void Record(string fullPath, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;

        List<FileUsageEntry> list;
        switch (Services.JsonListFile.TryRead(_filePath, JsonOptions, out list))
        {
            case Services.JsonFileState.Ok:
            case Services.JsonFileState.Missing:
                break;
            case Services.JsonFileState.Corrupt:
                Services.JsonListFile.BackupCorrupt(_filePath);
                break;
            default:
                return;   // locked by another window: skip this hint rather than overwrite its history
        }

        var existing = list.FirstOrDefault(e =>
            string.Equals(e.Path, fullPath, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            list.Add(new FileUsageEntry { Path = fullPath, Hits = 1, LastUsedUtc = nowUtc });
        }
        else
        {
            existing.Hits++;
            existing.LastUsedUtc = nowUtc;
        }

        if (list.Count > MaxEntries)
        {
            list = list
                .OrderByDescending(e => FileUsageIndex.Frecency(e.Hits, e.LastUsedUtc, nowUtc))
                .ThenByDescending(e => e.LastUsedUtc)
                .Take(MaxEntries)
                .ToList();
        }

        // A read-only directory must not break opening a file.
        Services.JsonListFile.WriteAtomic(_filePath, JsonSerializer.Serialize(list, JsonOptions));
    }
}
