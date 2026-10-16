using TCV.Search;

namespace TCV.App.Navigation;

/// <summary>How well a name answers the quick filter: the higher, the closer to what was meant.</summary>
public enum QuickFilterMatch
{
    None,

    /// <summary>The text is somewhere inside the name ("dev" in "WinDevCleaner").</summary>
    Anywhere,

    /// <summary>A word of the name starts with the text: after a dot, dash, underscore or space, or at a capital
    /// that follows a small letter ("win" in "Microsoft.Windows.dll", "Main<b>Win</b>dow").</summary>
    WordStart,

    /// <summary>The name starts with the text, or matches a mask the text is.</summary>
    Start,
}

/// <summary>
/// The panel's quick filter: what was typed into the list or the filter box, as a test of file names.
/// Plain text is found anywhere in the name, ignoring case, and every match is ranked
/// (<see cref="QuickFilterMatch"/>) so the cursor can go to the name the user is most likely typing —
/// every file manager jumps to the name that <i>starts</i> with the text (Total Commander, Far, Explorer),
/// and a substring hit on "Microsoft.<b>Wind</b>ows" must not win over "<b>Wind</b>evCleaner".
/// Text with <c>*</c> or <c>?</c> is a mask of the whole name (<c>*.cs</c> is C# files, not
/// <c>.csproj</c>), several separated by <c>;</c>, as in Select by mask. A space is part of the name.
/// </summary>
public sealed class QuickFilter
{
    public static QuickFilter Empty { get; } = new(string.Empty, null);

    private readonly FileMaskSet? _masks;

    private QuickFilter(string text, FileMaskSet? masks)
    {
        Text = text;
        _masks = masks;
    }

    /// <summary>The text the filter was made from, trimmed.</summary>
    public string Text { get; }

    /// <summary>Lets every name through.</summary>
    public bool IsEmpty => Text.Length == 0;

    public static QuickFilter Parse(string? text)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return Empty;
        }

        return trimmed.IndexOfAny(['*', '?']) >= 0
            ? new QuickFilter(trimmed, FileMaskSet.Parse(trimmed))
            : new QuickFilter(trimmed, null);
    }

    /// <summary>Lets <paramref name="name"/> through. Only the rank's yes or no: the first place the text is
    /// found is enough, where <see cref="Match"/> goes on to every place in the name.</summary>
    public bool Matches(string name)
    {
        if (_masks is not null)
        {
            return _masks.Matches(name);
        }

        return IsEmpty || name.Contains(Text, StringComparison.OrdinalIgnoreCase);
    }

    public QuickFilterMatch Match(string name)
    {
        if (IsEmpty)
        {
            return QuickFilterMatch.Start;
        }

        if (_masks is not null)
        {
            return _masks.Matches(name) ? QuickFilterMatch.Start : QuickFilterMatch.None;
        }

        var best = QuickFilterMatch.None;
        for (var at = name.IndexOf(Text, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = at + 1 < name.Length ? name.IndexOf(Text, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (at == 0)
            {
                return QuickFilterMatch.Start;
            }

            if (IsWordStart(name, at))
            {
                best = QuickFilterMatch.WordStart;
            }
            else if (best == QuickFilterMatch.None)
            {
                best = QuickFilterMatch.Anywhere;
            }
        }

        return best;
    }

    /// <summary>
    /// The index of the next (or, backwards, the previous) name after <paramref name="from"/> that the
    /// filter lets through, or -1 when there is none that way. A null name (the ".." row) never matches.
    /// </summary>
    public int NextMatch(IReadOnlyList<string?> names, int from, bool forward)
    {
        if (IsEmpty)
        {
            return -1;
        }

        var step = forward ? 1 : -1;
        for (var i = from + step; i >= 0 && i < names.Count; i += step)
        {
            if (names[i] is { } name && Matches(name))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The part of <paramref name="name"/> to highlight (6.87): the occurrence that earned the rank — the
    /// start, else the first word start, else the first place the text is. Null for an empty filter, a
    /// mask (it matches the whole name, so nothing in particular stands out) and a name it does not match.
    /// </summary>
    public (int Start, int Length)? MatchSpan(string name)
    {
        if (IsEmpty || _masks is not null)
        {
            return null;
        }

        int? first = null;
        for (var at = name.IndexOf(Text, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = at + 1 < name.Length ? name.IndexOf(Text, at + 1, StringComparison.OrdinalIgnoreCase) : -1)
        {
            if (at == 0 || IsWordStart(name, at))
            {
                return (at, Text.Length);
            }

            first ??= at;
        }

        return first is { } anywhere ? (anywhere, Text.Length) : null;
    }

    /// <summary>A word begins at <paramref name="at"/>: after a character that is not a letter or digit, or
    /// at a capital right after a small letter (camelCase, PascalCase).</summary>
    private static bool IsWordStart(string name, int at)
    {
        var before = name[at - 1];
        return !char.IsLetterOrDigit(before) || (char.IsUpper(name[at]) && char.IsLower(before));
    }
}
