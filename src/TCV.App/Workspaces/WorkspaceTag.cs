namespace TCV.App.Workspaces;

/// <summary>
/// A workspace's name, read for the narrow rail: its initials, at most three (<see cref="For"/>:
/// <c>TotalCommanderVibe</c> → TCV, <c>agent-toolkit</c> → AT, <c>payments</c> → PAY), of which the rail
/// shows the first on the plate of a workspace without a picture (<see cref="Monogram"/>), and the name
/// itself with places to break it for the caption under the tile (<see cref="WithBreaks"/>).
/// </summary>
public static class WorkspaceTag
{
    private const int MaxLength = 3;

    public static string For(string? name)
    {
        var words = Words(name ?? string.Empty);
        var tag = words.Count switch
        {
            0 => "?",
            1 => words[0].Length > MaxLength ? words[0][..MaxLength] : words[0],
            _ => string.Concat(words.Take(MaxLength).Select(w => w[0])),
        };
        return tag.ToUpperInvariant();
    }

    /// <summary>
    /// The one letter on a workspace's plate on the rail, when it has no picture and no emoji: the
    /// first of <see cref="For"/>. One, not three: the name is written under the plate, and letters
    /// repeating it read as a second, smaller caption rather than as a picture.
    /// </summary>
    public static string Monogram(string? name) => For(name)[..1];

    /// <summary>A zero-width space: a place a line may break, drawn as nothing.</summary>
    public const char BreakHere = '​';

    /// <summary>
    /// The name with places to break the line between its words, for the small caption under the
    /// rail's tile: WPF breaks only at spaces and hyphens, and «TotalCommanderVibe» would otherwise be
    /// cut mid-word (TotalComma/nderVibe). Breaks go where a lower-case letter meets a capital, before
    /// the last capital of an upper-case run that goes on in lower case, and after '_' and '.'.
    /// </summary>
    public static string WithBreaks(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0)
            {
                var last = name[i - 1];
                var camel = char.IsUpper(c) && char.IsLower(last);
                var acronymEnd = char.IsUpper(c) && char.IsUpper(last) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (camel || acronymEnd || last is '_' or '.')
                {
                    text.Append(BreakHere);
                }
            }

            text.Append(c);
        }

        return text.ToString();
    }

    /// <summary>
    /// Runs of letters and of digits, also split where a lower-case letter meets a capital (camelCase)
    /// and before the last capital of an upper-case run that goes on in lower case (HTTPServer → HTTP, Server).
    /// </summary>
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

            if (current.Length > 0)
            {
                var last = current[^1];
                if ((char.IsUpper(c) && char.IsLower(last)) || char.IsDigit(c) != char.IsDigit(last))
                {
                    Flush();
                }
                else if (char.IsLower(c) && char.IsUpper(last) && current.Length > 1 && char.IsUpper(current[^2]))
                {
                    current.Length--;
                    Flush();
                    current.Append(last);
                }
            }

            current.Append(c);
        }

        Flush();
        return words;
    }
}
