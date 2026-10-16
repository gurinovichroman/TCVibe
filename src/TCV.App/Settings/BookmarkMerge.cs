namespace TCV.App.Settings;

/// <summary>
/// Pure rules for importing bookmarks into a list that already has some: the «merge» answer of the
/// import question (6.55), kept out of the dialog so it is testable without a window.
/// </summary>
public static class BookmarkMerge
{
    /// <summary>
    /// The imported bookmarks the list does not have yet, in file order. A bookmark already in
    /// <paramref name="existing"/> (or earlier in the file) is skipped; everything else is kept as it
    /// is, so two bookmarks to the same folder under different names both survive — dropping one would
    /// lose a name the user chose.
    /// </summary>
    public static IReadOnlyList<Bookmark> NewOnes(IEnumerable<Bookmark> existing, IEnumerable<Bookmark> imported)
    {
        var seen = new HashSet<(string, string)>(existing.Select(Key));
        var result = new List<Bookmark>();

        foreach (var bookmark in imported)
        {
            if (seen.Add(Key(bookmark)))
            {
                result.Add(bookmark);
            }
        }

        return result;
    }

    /// <summary>
    /// True when the two point at the same folder under the same name. Windows paths are compared as
    /// Windows reads them: case-insensitively, either slash, no trailing separator (<c>C:/src/</c> is
    /// <c>c:\SRC</c>). The group does not count — the same entry filed elsewhere is still the same entry.
    /// </summary>
    public static bool IsSame(Bookmark a, Bookmark b) => Key(a) == Key(b);

    private static (string Name, string Path) Key(Bookmark b) =>
        ((b.Name ?? string.Empty).Trim().ToUpperInvariant(),
         (b.Path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\').ToUpperInvariant());
}
