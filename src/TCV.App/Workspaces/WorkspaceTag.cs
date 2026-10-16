namespace TCV.App.Workspaces;

/// <summary>
/// The two letters a workspace shows on the narrow rail: the initials of its first two words
/// (<c>agent-toolkit</c> → AT, <c>TotalCommanderVibe</c> → TC), or the first two letters of a
/// one-word name. The whole name is the button's tooltip.
/// </summary>
public static class WorkspaceTag
{
    public static string For(string? name)
    {
        var words = Words(name ?? string.Empty);
        var tag = words.Count switch
        {
            0 => "?",
            1 => words[0].Length >= 2 ? words[0][..2] : words[0],
            _ => string.Concat(words[0][0], words[1][0]),
        };
        return tag.ToUpperInvariant();
    }

    /// <summary>Runs of letters and digits, also split where a lower-case letter meets a capital (camelCase).</summary>
    private static List<string> Words(string name)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();

        void Flush()
        {
            if (current.Length > 0)
            {
                words.Add(current.ToString());
            }

            current.Clear();
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0 && char.IsLower(current[^1]))
            {
                Flush();
            }

            current.Append(c);
        }

        Flush();
        return words;
    }
}
