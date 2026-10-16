namespace TCV.App.Commands;

/// <summary>
/// Pure, case-insensitive fuzzy subsequence matcher shared by the command palette and the
/// workspace switcher. No WPF dependencies, so it is trivially unit-testable.
/// </summary>
/// <remarks>
/// Every character of the pattern must appear in the text in order. The score rewards
/// prefix/substring hits, matches at word boundaries (after a separator or a CamelCase hump)
/// and consecutive runs, and lightly penalises skipped characters and long targets — so the
/// tightest, most "obvious" match ranks first.
/// </remarks>
public static class FuzzyMatcher
{
    private const int PrefixBonus = 100;
    private const int SubstringBonus = 40;
    private const int BoundaryBonus = 15;
    private const int CamelBonus = 12;
    private const int ConsecutiveBonus = 10;
    private const int GapPenalty = 1;

    /// <summary>
    /// Returns true when every character of <paramref name="pattern"/> appears in
    /// <paramref name="text"/> in order (case-insensitive). An empty/whitespace pattern matches
    /// everything with score 0. <paramref name="score"/> ranks matches (higher = better);
    /// <paramref name="positions"/> lists the matched indices in <paramref name="text"/> for
    /// optional highlighting.
    /// </summary>
    public static bool TryMatch(string text, string pattern, out int score, out List<int> positions)
    {
        positions = new List<int>();
        score = 0;

        if (string.IsNullOrWhiteSpace(pattern)) return true;
        if (string.IsNullOrEmpty(text)) return false;

        return Walk(text, text.ToLowerInvariant(), pattern.Trim().ToLowerInvariant(), positions, out score);
    }

    /// <summary>Convenience overload for callers that only need the score.</summary>
    public static bool TryMatch(string text, string pattern, out int score) =>
        TryMatch(text, pattern, out score, out _);

    /// <summary>
    /// Score-only variant for hot ranking loops: the caller supplies the lower-cased text and an
    /// already trimmed, lower-cased pattern, so an index can lower-case each candidate once instead
    /// of once per keystroke, and nothing is allocated per candidate. Both spans may be slices, which
    /// is how a scoped search matches a sub-path without cutting a substring out of it. Scoring is
    /// identical to <see cref="TryMatch(string,string,out int)"/>.
    /// </summary>
    public static bool TryMatchLower(
        ReadOnlySpan<char> text, ReadOnlySpan<char> textLower, ReadOnlySpan<char> patternLower,
        out int score)
    {
        score = 0;

        if (patternLower.IsWhiteSpace()) return true;   // empty span included
        if (text.IsEmpty) return false;

        return Walk(text, textLower, patternLower, null, out score);
    }

    private static bool Walk(
        ReadOnlySpan<char> text, ReadOnlySpan<char> textLower, ReadOnlySpan<char> patternLower,
        List<int>? positions, out int score)
    {
        score = 0;

        // Whole-token boosts on top of the per-character walk below.
        if (textLower.StartsWith(patternLower, StringComparison.Ordinal))
            score += PrefixBonus;
        else if (textLower.Contains(patternLower, StringComparison.Ordinal))
            score += SubstringBonus;

        int cursor = 0;
        int prev = -2;
        foreach (var pc in patternLower)
        {
            int found = textLower[cursor..].IndexOf(pc);
            if (found < 0)
            {
                score = 0;
                positions?.Clear();
                return false;
            }

            int idx = cursor + found;

            if (idx == prev + 1) score += ConsecutiveBonus;
            if (idx == 0 || IsBoundary(text[idx - 1])) score += BoundaryBonus;
            else if (char.IsUpper(text[idx]) && char.IsLower(text[idx - 1])) score += CamelBonus;

            score -= (idx - (prev + 1)) * GapPenalty;   // punish skipped runs

            positions?.Add(idx);
            prev = idx;
            cursor = idx + 1;
        }

        // Break ties toward shorter, tighter targets.
        score -= text.Length / 10;
        return true;
    }

    private static bool IsBoundary(char c) =>
        c is ' ' or '/' or '\\' or '_' or '-' or '.' or ':' or '(';
}
