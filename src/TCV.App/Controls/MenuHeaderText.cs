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
