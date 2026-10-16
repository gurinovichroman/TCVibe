using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TCV.App.Commands;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// Backs the command palette and, through the same window, every other fuzzy picker in the app:
/// the workspace switcher, the gadget list and the instant name search (6.21/6.24). Filters and
/// ranks as the user types. Pure VM logic (no WPF), so the filtering/ranking is unit-testable.
/// </summary>
/// <remarks>
/// Two modes. With a fixed list it fuzzy-matches every item on each keystroke, which is right for
/// the few hundred rows a command catalog has. With a <see cref="PaletteSource"/> the query is
/// handed to the caller instead — the name index has hundreds of thousands of entries and does its
/// own matching and capping (6.21).
/// </remarks>
public partial class CommandPaletteViewModel : ObservableObject
{
    private readonly IReadOnlyList<CommandPaletteItem> _all;
    private readonly PaletteSource? _source;

    /// <summary>Produces the rows for a query. Called on the UI thread, so it must be fast.</summary>
    public delegate IReadOnlyList<CommandPaletteItem> PaletteSource(string query);

    public CommandPaletteViewModel(
        IReadOnlyList<CommandPaletteItem> allItems, string language = "en",
        string? title = null, string? placeholder = null)
    {
        _all = allItems;
        Language = language;
        Title = title ?? (language == "ru" ? "Палитра команд" : "Command Palette");
        Placeholder = placeholder ?? (language == "ru" ? "Введите команду…" : "Type a command…");
        Refilter();
    }

    /// <summary>Picker over a source too large to filter row by row.</summary>
    public CommandPaletteViewModel(
        PaletteSource source, string language = "en",
        string? title = null, string? placeholder = null)
    {
        _all = Array.Empty<CommandPaletteItem>();
        _source = source;
        Language = language;
        Title = title ?? string.Empty;
        Placeholder = placeholder ?? string.Empty;
        Refilter();
    }

    public string Language { get; }
    public string Title { get; }
    public string Placeholder { get; }

    /// <summary>Free-form text under the query box: a hint, a count, "indexing…".</summary>
    [ObservableProperty]
    private string _status = string.Empty;

    public ObservableCollection<CommandPaletteItem> Results { get; } = new();

    [ObservableProperty]
    private string _query = string.Empty;

    [ObservableProperty]
    private int _selectedIndex;

    /// <summary>The row that Enter would run (or null when nothing matches).</summary>
    public CommandPaletteItem? SelectedItem =>
        SelectedIndex >= 0 && SelectedIndex < Results.Count ? Results[SelectedIndex] : null;

    partial void OnQueryChanged(string value) => Refilter();

    partial void OnSelectedIndexChanged(int value) => OnPropertyChanged(nameof(SelectedItem));

    /// <summary>Moves the highlighted row, clamped to the result range.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    /// <summary>
    /// Re-runs the current query after the underlying source changed, keeping the highlighted row
    /// where it is. A background index rebuild refreshes the list every few thousand entries, and
    /// jumping back to the first row each time made the arrow keys useless — Enter then opened
    /// whatever happened to be first.
    /// </summary>
    public void Refresh()
    {
        var keep = SelectedItem?.Id;
        Refilter();
        if (keep is null)
        {
            return;
        }

        for (var i = 0; i < Results.Count; i++)
        {
            if (Results[i].Id == keep)
            {
                SelectedIndex = i;
                return;
            }
        }
    }

    private void Refilter()
    {
        if (_source is not null)
        {
            Results.Clear();
            foreach (var item in _source(Query)) Results.Add(item);
            SelectedIndex = Results.Count > 0 ? 0 : -1;
            OnPropertyChanged(nameof(SelectedItem));
            return;
        }

        var scored = new List<(CommandPaletteItem Item, int Score)>();
        foreach (var item in _all)
        {
            // Match the title, and also "Category Title" so "nav back" or "files copy" work.
            var m1 = FuzzyMatcher.TryMatch(item.Title, Query, out var s1);
            var m2 = FuzzyMatcher.TryMatch(item.Category + " " + item.Title, Query, out var s2);
            if (m1 || m2)
                scored.Add((item, Math.Max(s1, s2)));
        }

        scored.Sort((a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0)
            {
                return byScore;
            }

            var byRank = b.Item.Rank.CompareTo(a.Item.Rank);
            return byRank != 0
                ? byRank
                : string.Compare(a.Item.Title, b.Item.Title, StringComparison.OrdinalIgnoreCase);
        });

        Results.Clear();
        foreach (var (item, _) in scored)
            Results.Add(item);

        SelectedIndex = Results.Count > 0 ? 0 : -1;
        OnPropertyChanged(nameof(SelectedItem));
    }
}
