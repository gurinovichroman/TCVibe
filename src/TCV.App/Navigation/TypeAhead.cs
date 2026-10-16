namespace TCV.App.Navigation;

/// <summary>
/// Typing in the panel's Tree view (6.87): the quick filter narrows a list the tree does not show, so
/// there letters do what they do in Explorer's folder tree — the cursor goes to the next node in sight
/// whose name starts with them. Letters typed within <see cref="Pause"/> of each other are one prefix;
/// the same letter typed again and again steps through the names that start with it.
/// </summary>
public sealed class TypeAhead
{
    /// <summary>How long a pause starts a new prefix.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    private string _prefix = string.Empty;
    private DateTime _last = DateTime.MinValue;

    /// <summary>Adds what was typed at <paramref name="now"/> and returns the prefix to look for.</summary>
    public string Type(string text, DateTime now)
    {
        if (now - _last > Pause)
        {
            _prefix = string.Empty;
        }

        _last = now;
        _prefix += text;
        return _prefix;
    }

    /// <summary>
    /// The index of the name to go to from <paramref name="current"/> (-1 for none selected), or -1 when no
    /// name starts with <paramref name="prefix"/>. A longer prefix keeps the current name while it still
    /// fits; one letter, or one letter repeated, moves on to the next name with it, round to the first.
    /// </summary>
    public static int Find(IReadOnlyList<string> names, int current, string prefix)
    {
        if (names.Count == 0 || prefix.Length == 0)
        {
            return -1;
        }

        var repeated = prefix.All(c => char.ToUpperInvariant(c) == char.ToUpperInvariant(prefix[0]));
        var look = repeated ? prefix[..1] : prefix;
        var start = repeated ? current + 1 : Math.Max(current, 0);

        for (var n = 0; n < names.Count; n++)
        {
            var i = (start + n) % names.Count;
            if (names[i].StartsWith(look, StringComparison.CurrentCultureIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}
