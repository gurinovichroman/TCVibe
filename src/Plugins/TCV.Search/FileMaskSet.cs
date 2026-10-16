using System.Text.RegularExpressions;

namespace TCV.Search;

/// <summary>
/// A Total-Commander style list of file-name masks (<c>*.cs;*.md</c>) compiled once. An entry
/// matches when <b>any</b> mask matches; an empty set matches everything, so callers can pass the
/// user's raw text through without a null check.
/// </summary>
/// <remarks>
/// Separators are <c>;</c>, <c>,</c> and line breaks — deliberately <b>not</b> the space: this set
/// filters file <i>names</i>, and names with spaces in them are ordinary, so <c>my report*</c> has
/// to stay one mask. A space-separated list is written <c>*.cs;*.md</c> instead.
/// <c>*.*</c> is the DOS convention for "every file" and therefore also matches
/// extensionless names (<c>README</c>, <c>LICENSE</c>) — a literal <c>.*</c> would skip those.
/// Path-shaped exclusions (<c>build/</c>, <c>**/obj/**</c>) are not this class's job: they go
/// through <see cref="TCV.Core.Domain.GitignoreRules"/>, which already implements that grammar.
/// </remarks>
public sealed class FileMaskSet
{
    /// <summary>A set with no masks — matches everything.</summary>
    public static FileMaskSet Empty { get; } = new(Array.Empty<Regex>());

    private static readonly char[] Separators = [';', ',', (char)13, (char)10];
    private static readonly char[] WildcardChars = ['*', '?'];

    private readonly IReadOnlyList<Regex> _masks;

    private FileMaskSet(IReadOnlyList<Regex> masks) => _masks = masks;

    public bool IsEmpty => _masks.Count == 0;

    /// <summary>Parses a mask list; blank input yields <see cref="Empty"/>.</summary>
    public static FileMaskSet Parse(string? text, bool caseSensitive = false, bool bareNameIsSubstring = false)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        var masks = new List<Regex>();
        foreach (var raw in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            masks.Add(new Regex(ToPattern(raw, bareNameIsSubstring), options));

        return masks.Count == 0 ? Empty : new FileMaskSet(masks);
    }

    /// <summary>True when <paramref name="name"/> matches any mask, or the set is empty.</summary>
    public bool Matches(string name) =>
        _masks.Count == 0 || _masks.Any(m => m.IsMatch(name));

    private static string ToPattern(string mask, bool bareNameIsSubstring)
    {
        if (mask == "*.*") mask = "*";

        // A name with no wildcard at all is a fragment the user remembers, not a whole file name:
        // "config" finds appsettings.config.json, as it does in Total Commander and every editor.
        if (bareNameIsSubstring && mask.IndexOfAny(WildcardChars) < 0)
        {
            return ".*" + Regex.Escape(mask) + ".*";
        }

        return "^" + Regex.Escape(mask).Replace("\\*", ".*").Replace("\\?", ".") + "$";
    }
}
