namespace TCV.Archives.Engines;

/// <summary>
/// The paths a one-pass read was asked for, matched against the archive's entries the way
/// <c>ExtractEntry</c> matches a single one: by the normalised path, the exact name when the archive
/// has it and otherwise a name that differs only by case. Each request is answered once, by the first
/// entry that matches it, so a duplicate entry further on does not answer it again.
/// </summary>
internal sealed class EntryRequests
{
    private readonly Dictionary<string, string> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folded = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="requested">The paths asked for, in any spelling the engine accepts.</param>
    /// <param name="fileKeys">The raw names of every file entry in the archive.</param>
    public EntryRequests(IEnumerable<string> requested, IEnumerable<string> fileKeys)
    {
        var present = fileKeys.Select(EntryListBuilder.NormalizePath).ToHashSet(StringComparer.Ordinal);
        foreach (var path in requested)
        {
            var normalized = EntryListBuilder.NormalizePath(path);
            if (present.Contains(normalized))
            {
                _exact.TryAdd(normalized, path);
            }
            else
            {
                _folded.TryAdd(normalized, path);
            }
        }
    }

    /// <summary>Requests not answered yet. A pass can stop reading once this is zero.</summary>
    public int Remaining => _exact.Count + _folded.Count;

    /// <summary>The request the entry named <paramref name="key"/> answers (and no longer open), or null.</summary>
    public string? Claim(string key)
    {
        var normalized = EntryListBuilder.NormalizePath(key);
        if (_exact.Remove(normalized, out var requested) || _folded.Remove(normalized, out requested))
        {
            return requested;
        }

        return null;
    }
}
