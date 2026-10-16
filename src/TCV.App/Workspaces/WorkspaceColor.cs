using System.Globalization;

namespace TCV.App.Workspaces;

/// <summary>
/// Parsing and normalising of the workspace accent colour. Deliberately WPF-free: the value is
/// stored as text in <c>workspaces.json</c> and hand-editable, so the forgiving forms people
/// actually type (<c>4f9cf9</c>, <c>#4F9CF9</c>, <c>#f00</c>) all have to resolve to one canonical
/// spelling before anything tries to paint with it.
/// </summary>
public static class WorkspaceColor
{
    /// <summary>
    /// The ready-made swatches of the properties dialog. Mid-tone hues, so a strip and a dot read on
    /// both the dark and the light theme, and far enough apart that two windows are told apart at a
    /// glance — which is what the colour is for. Any other colour is still one click away.
    /// </summary>
    public static IReadOnlyList<string> Presets { get; } =
    [
        "#E5484D",   // red
        "#F76B15",   // orange
        "#F5B000",   // amber
        "#46A758",   // green
        "#12A594",   // teal
        "#0090FF",   // blue
        "#3E63DD",   // indigo
        "#8E4EC6",   // violet
        "#D6409F",   // pink
        "#A18072",   // brown
        "#8B8D98",   // grey
    ];

    /// <summary>
    /// The colour a new workspace starts with: the first preset no workspace wears yet, or, once all
    /// are taken, the one worn least. A new workspace without a colour sits on the rail as two grey
    /// letters, and two windows of different projects look the same.
    /// </summary>
    public static string SuggestFor(IEnumerable<string?> used)
    {
        var counts = Presets.ToDictionary(p => p, _ => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var color in used)
        {
            if (TryNormalize(color, out var normalized) && counts.ContainsKey(normalized))
            {
                counts[normalized]++;
            }
        }

        // MinBy keeps the first of equals, so the presets' own order breaks ties.
        return Presets.MinBy(p => counts[p])!;
    }

    /// <summary>
    /// Normalises a user-entered colour to <c>#RRGGBB</c>.
    /// Blank input is not an error — it means "no accent", and yields an empty string.
    /// </summary>
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return true;   // cleared

        var text = input.Trim().TrimStart('#');

        // #abc is the CSS shorthand for #aabbcc — expand it rather than rejecting it.
        if (text.Length == 3)
            text = string.Concat(text[0], text[0], text[1], text[1], text[2], text[2]);

        if (text.Length != 6) return false;

        foreach (var ch in text)
            if (!Uri.IsHexDigit(ch)) return false;

        normalized = "#" + text.ToUpperInvariant();
        return true;
    }

    /// <summary>Splits a normalised colour into its channels. False when the value is blank or
    /// malformed — callers use that to mean "draw nothing", never to throw mid-render.</summary>
    public static bool TryParseRgb(string? normalized, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (!TryNormalize(normalized, out var text) || text.Length != 7) return false;

        r = byte.Parse(text.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        g = byte.Parse(text.AsSpan(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        b = byte.Parse(text.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return true;
    }
}
