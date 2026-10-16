namespace TCV.MultiRename.Rules;

/// <summary>Deletes a range of characters from the name by start index and count.</summary>
public sealed class DeleteRule : IRenameRule
{
    private readonly int _start;
    private readonly int _count;

    /// <param name="start">Zero-based index of the first character to delete.</param>
    /// <param name="count">Number of characters to delete.</param>
    public DeleteRule(int start, int count)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start), "Start index must be >= 0.");
        if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count), "Count must be > 0.");
        _start = start;
        _count = count;
    }

    public string Description => $"Delete {_count} chars from position {_start}";

    public string Apply(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (_start >= name.Length) return name;

        var actualCount = Math.Min(_count, name.Length - _start);
        return name.Remove(_start, actualCount);
    }
}
