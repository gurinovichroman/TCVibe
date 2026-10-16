using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Commands;
using TCV.App.Navigation;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>Localised chrome for the file picker, resolved by the caller from the string dictionaries.</summary>
/// <param name="StatusFormat">Takes the number of indexed entries.</param>
/// <param name="ScopeFormat">Takes the relative directory the picker is narrowed to.</param>
public sealed record FileFinderStrings(
    string Title, string Placeholder, string Indexing, string StatusFormat,
    string Truncated, string ScopeFormat);

/// <summary>
/// Backs the fuzzy file picker (Ctrl+P): ranks an incrementally-filled index against the query and
/// supports narrowing to a subdirectory with Tab. Pure VM logic — the walk that fills it lives in
/// <see cref="FileIndexWalker"/> and is driven by the dialog, so ranking and drill-down are testable
/// without a window or a disk.
/// </summary>
public partial class FileFinderViewModel : ObservableObject
{
    /// <summary>Rows handed to the list. Ranking still considers every indexed entry.</summary>
    public const int MaxResults = 300;

    /// <summary>Added when the file *name* matched, not just some directory above it — typing
    /// "readme" should surface README.md itself before the twenty files in a "readme" folder.</summary>
    private const int NameMatchBonus = 25;

    private readonly List<Candidate> _all = new();
    private readonly List<(FileIndexEntry Entry, int Score)> _scored = new();
    private readonly FileUsageIndex _usage;
    private readonly FileFinderStrings _strings;

    /// <summary>
    /// An indexed entry plus everything ranking needs precomputed. Lower-casing and the habit bonus
    /// never change while the dialog is open, so they are paid once per entry instead of on every
    /// keystroke: <see cref="Refilter"/> walks the whole index (up to
    /// <see cref="FileIndexOptions.MaxEntries"/>) on the UI thread, and per-entry allocations there
    /// are what would make typing stutter (INV-06).
    /// </summary>
    private readonly record struct Candidate(
        FileIndexEntry Entry, string NameLower, string PathLower, int Habit);

    public FileFinderViewModel(string rootPath, FileUsageIndex? usage = null, FileFinderStrings? strings = null)
    {
        RootPath = rootPath;
        _usage = usage ?? FileUsageIndex.Empty;
        _strings = strings ?? DefaultStrings;
        IsIndexing = true;
        UpdateStatus();
    }

    private static readonly FileFinderStrings DefaultStrings = new(
        "Find file", "Type part of a name or path…", "indexing…", "{0} entries",
        "limit reached", "in {0}");

    /// <summary>Root of the index — the project root when one was found.</summary>
    public string RootPath { get; }

    public string Title => _strings.Title;
    public string Placeholder => _strings.Placeholder;

    /// <summary>Rows currently shown, best match first.</summary>
    public ObservableCollection<FileIndexEntry> Results { get; } = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>Relative directory the search is narrowed to (empty = the whole index).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScopeLabel))]
    [NotifyPropertyChangedFor(nameof(HasScope))]
    private string _scope = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    /// <summary>True while the background walk is still adding entries.</summary>
    [ObservableProperty]
    private bool _isIndexing;

    /// <summary>Set when the walk stopped at the entry cap, so the status line can admit it.</summary>
    [ObservableProperty]
    private bool _isTruncated;

    public bool HasScope => Scope.Length > 0;

    public string ScopeLabel => HasScope ? string.Format(_strings.ScopeFormat, Scope) : string.Empty;

    /// <summary>Number of entries indexed so far.</summary>
    public int IndexedCount => _all.Count;

    /// <summary>
    /// There is something to search, but nothing matches — the list says so instead of staying blank,
    /// which looks the same as "still indexing".
    /// </summary>
    public bool HasNoMatches => Results.Count == 0 && _all.Count > 0;

    /// <summary>The row Enter would act on, or null when nothing matches.</summary>
    public FileIndexEntry? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    partial void OnQueryChanged(string value) => Refilter();

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(SelectedItem));

    partial void OnScopeChanged(string value) => Refilter();

    /// <summary>
    /// Adds a batch of indexed entries and re-ranks. Called on the UI thread. The highlighted row
    /// survives: the index keeps filling while the user is already reading the list, and yanking the
    /// selection back to the top under their fingers would make arrow keys unusable on a big tree.
    /// </summary>
    public void Append(IReadOnlyList<FileIndexEntry> batch)
    {
        if (batch.Count == 0) return;

        foreach (var entry in batch)
            _all.Add(new Candidate(
                entry,
                entry.Name.ToLowerInvariant(),
                entry.RelativePath.ToLowerInvariant(),
                FileUsageIndex.HabitBonus(_usage.Score(entry.FullPath))));

        OnPropertyChanged(nameof(IndexedCount));
        UpdateStatus();
        Refilter(preserveSelection: true);
    }

    /// <summary>Marks the index complete (the walk finished or was cancelled).</summary>
    public void FinishIndexing(bool truncated = false)
    {
        IsTruncated = truncated;
        IsIndexing = false;
        UpdateStatus();
    }

    /// <summary>Moves the highlighted row, clamped to the result range.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    /// <summary>
    /// Tab: narrows the search to the highlighted row's directory and clears the query, so the next
    /// keystrokes search inside it. Returns false when there is nothing to narrow to.
    /// </summary>
    public bool DrillDown()
    {
        if (SelectedItem is not { } item) return false;

        var target = item.IsDirectory ? item.RelativePath : item.RelativeDirectory;
        if (target.Length == 0 || string.Equals(target, Scope, StringComparison.OrdinalIgnoreCase))
            return false;

        Query = string.Empty;   // assignment order matters: clearing the query refilters once here…
        Scope = target;         // …and the scope change refilters against the new subtree
        return true;
    }

    /// <summary>
    /// Backspace on an empty query: steps the scope one directory up, ending at the whole index.
    /// Returns false when the search is already unscoped.
    /// </summary>
    public bool PopScope()
    {
        if (!HasScope) return false;

        var slash = Scope.LastIndexOf('/');
        Scope = slash < 0 ? string.Empty : Scope[..slash];
        return true;
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    private void Refilter(bool preserveSelection = false)
    {
        var previous = preserveSelection ? SelectedItem : null;
        var scope = Scope;
        var prefixLength = scope.Length == 0 ? 0 : scope.Length + 1;

        // Lower-cased once, not once per entry. Backslashes become slashes (index paths use '/', and
        // a Windows user types "src\app"); blanks separate terms that must all match, in any order,
        // as in every editor's picker: "app main" finds src/app/Main.cs.
        var terms = Query.Trim().ToLowerInvariant()
            .Replace((char)92, '/')
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0)
        {
            terms = [string.Empty];
        }

        _scored.Clear();

        foreach (var candidate in _all)
        {
            var relative = candidate.Entry.RelativePath;
            if (prefixLength > 0 && !IsInsideScope(relative, scope)) continue;

            // Inside a scope the leading directories are noise — match what the user still sees.
            // Invariant lower-casing is character-for-character, so the same offset cuts both.
            var path = relative.AsSpan(prefixLength);
            var pathLower = candidate.PathLower.AsSpan(prefixLength);

            var total = 0;
            var matchedAll = true;
            foreach (var term in terms)
            {
                var nameMatched = FuzzyMatcher.TryMatchLower(
                    candidate.Entry.Name, candidate.NameLower, term, out var nameScore);
                var pathMatched = FuzzyMatcher.TryMatchLower(path, pathLower, term, out var pathScore);
                if (!nameMatched && !pathMatched)
                {
                    matchedAll = false;
                    break;
                }

                var score = nameMatched ? nameScore + NameMatchBonus : int.MinValue;
                if (pathMatched && pathScore > score)
                {
                    score = pathScore;
                }
                total += score;
            }

            if (matchedAll)
            {
                _scored.Add((candidate.Entry, total + candidate.Habit));
            }
        }

        _scored.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;

            // Shallower paths first, then a stable alphabetical order.
            var byLength = a.Entry.RelativePath.Length.CompareTo(b.Entry.RelativePath.Length);
            return byLength != 0
                ? byLength
                : string.Compare(a.Entry.RelativePath, b.Entry.RelativePath, StringComparison.OrdinalIgnoreCase);
        });

        Results.Clear();
        for (var i = 0; i < _scored.Count && i < MaxResults; i++)
            Results.Add(_scored[i].Entry);

        var restored = previous is null ? -1 : Results.IndexOf(previous);
        SelectedIndex = restored >= 0 ? restored : Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedItem));
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private static bool IsInsideScope(string relativePath, string scope) =>
        relativePath.Length > scope.Length &&
        relativePath[scope.Length] == '/' &&
        relativePath.StartsWith(scope, StringComparison.OrdinalIgnoreCase);

    private void UpdateStatus()
    {
        var count = string.Format(_strings.StatusFormat, _all.Count);
        Status = IsIndexing
            ? $"{count} · {_strings.Indexing}"
            : IsTruncated ? $"{count} · {_strings.Truncated}" : count;
    }
}
