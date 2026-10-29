namespace TCV.App.Controls;

/// <summary>
/// Text for a menu header that comes from the user (a bookmark, a workspace, a tool). The menu
/// templates recognise access keys, so the first "_" of "my_project" would vanish and turn the next
/// letter into a mnemonic; doubling every underscore shows the name as typed.
/// </summary>
public static class MenuHeaderText
{
    public static string Literal(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.Replace("_", "__", StringComparison.Ordinal);

    /// <summary>Longest user text <see cref="WithDetail"/> keeps; a menu as wide as a command line
    /// would push the gestures off the screen.</summary>
    public const int MaxDetailLength = 40;

    /// <summary>
    /// A localized header followed by user text that says what the item will do: "Workspace
    /// _terminal — npm run dev". The detail is shortened and taken literally, the header keeps its
    /// access key; blank detail leaves the header as it is. A line break (a project file's command may
    /// hold one) becomes a space, so the item stays one line, and the cut never splits an emoji.
    /// </summary>
    public static string WithDetail(string header, string? detail)
    {
        var text = string.Concat((detail ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        if (text.Length == 0)
        {
            return header;
        }

        if (text.Length > MaxDetailLength)
        {
            var cut = MaxDetailLength - 1;
            if (char.IsHighSurrogate(text[cut - 1]))
            {
                cut--;   // the pair's second half would be cut off
            }

            text = text[..cut].TrimEnd() + "…";
        }

        return $"{header} — {Literal(text)}";
    }

    /// <summary>The reverse, for quoting a menu path in prose: "_Files" → "Files", "my__project" →
    /// "my_project".</summary>
    public static string Plain(object? header)
    {
        if (header is not string text)
        {
            return header?.ToString() ?? string.Empty;
        }

        var result = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '_')
            {
                if (i + 1 < text.Length && text[i + 1] == '_')
                {
                    result.Append('_');
                    i++;
                }

                continue;
            }

            result.Append(text[i]);
        }

        return result.ToString();
    }
}
