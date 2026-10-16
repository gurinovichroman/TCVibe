namespace TCV.MultiRename.Rules;

/// <summary>Inserts a text snippet at a given character position in the name.</summary>
public sealed class InsertRule : IRenameRule
{
    private readonly string _text;
    private readonly int _position;

    /// <param name="text">Text to insert.</param>
    /// <param name="position">
    ///   Zero-based character index at which to insert.
    ///   Negative values are interpreted from the end of the string (like Python slices).
    ///   A position beyond the string length appends the text.
    /// </param>
    public InsertRule(string text, int position)
    {
        _text = text ?? throw new ArgumentNullException(nameof(text));
        _position = position;
    }

    public string Description => $"Insert \"{_text}\" at position {_position}";

    public string Apply(string name)
    {
        if (string.IsNullOrEmpty(_text)) return name;
        if (string.IsNullOrEmpty(name)) return _text;

        var pos = _position;
        if (pos < 0)
            pos = Math.Max(0, name.Length + pos);
        pos = Math.Min(pos, name.Length);

        return name.Insert(pos, _text);
    }
}
