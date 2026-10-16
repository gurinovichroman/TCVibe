namespace TCV.App.Collections;

/// <summary>
/// Explorer-style name order: runs of digits compare by value, so "file2" sorts before "file10",
/// and letters compare case-insensitively in the current culture. Pure managed code (no
/// <c>StrCmpLogicalW</c> round trip per comparison) because a 10 000-entry listing sorts on every load.
/// </summary>
public sealed class NaturalStringComparer : IComparer<string>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            var xDigit = char.IsAsciiDigit(x[i]);
            var yDigit = char.IsAsciiDigit(y[j]);

            // A number sorts before text at the same position ("a1" < "ab"), as in Explorer.
            if (xDigit != yDigit) return xDigit ? -1 : 1;

            var xEnd = RunEnd(x, i, xDigit);
            var yEnd = RunEnd(y, j, yDigit);

            var result = xDigit
                ? CompareNumbers(x.AsSpan(i, xEnd - i), y.AsSpan(j, yEnd - j))
                : x.AsSpan(i, xEnd - i).CompareTo(y.AsSpan(j, yEnd - j), StringComparison.CurrentCultureIgnoreCase);
            if (result != 0) return result;

            i = xEnd;
            j = yEnd;
        }

        // One name is a prefix of the other: the shorter comes first. Names equal but for case or
        // leading zeros ("a01" / "a1") still need a stable order.
        var remaining = (x.Length - i).CompareTo(y.Length - j);
        return remaining != 0 ? remaining : string.CompareOrdinal(x, y);
    }

    private static int RunEnd(string s, int start, bool digits)
    {
        var end = start;
        while (end < s.Length && char.IsAsciiDigit(s[end]) == digits) end++;
        return end;
    }

    /// <summary>Compares two digit runs by value without parsing, so any length fits.</summary>
    private static int CompareNumbers(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        x = x.TrimStart('0');
        y = y.TrimStart('0');
        if (x.Length != y.Length) return x.Length.CompareTo(y.Length);
        return x.SequenceCompareTo(y);
    }
}
