namespace TCV.App.Viewer;

/// <summary>
/// How much of a window the coloured views (code, markdown) are willing to build. A
/// <c>FlowDocument</c> materialises an element per coloured run, so one minified 3 MB line or
/// a 4 MB markdown file freezes the window for many seconds. The plain-text view has no such limit and
/// always holds the whole window; the coloured views say when they stopped and where to look instead.
/// </summary>
public static class RenderLimits
{
    /// <summary>Lines rendered at most.</summary>
    public const int MaxLines = 20_000;

    /// <summary>Characters rendered at most, all lines together.</summary>
    public const int MaxChars = 1_500_000;

    /// <summary>A longer line is cut here (minified bundles, data URIs): lexing it costs more than it shows.</summary>
    public const int MaxLineChars = 2_000;

    /// <summary>Markdown source rendered at most: every block becomes several elements.</summary>
    public const int MaxMarkdownChars = 400_000;

    /// <summary>Markdown blocks rendered at most.</summary>
    public const int MaxMarkdownBlocks = 6_000;

    /// <summary>How many leading lines fit in both the line budget and the character budget.</summary>
    public static int LinesWithinBudget(IReadOnlyList<string> lines)
    {
        var total = 0;
        var count = 0;
        foreach (var line in lines)
        {
            if (count >= MaxLines)
            {
                break;
            }

            total += Math.Min(line.Length, MaxLineChars);
            if (total > MaxChars && count > 0)
            {
                break;
            }

            count++;
        }
        return count;
    }

    /// <summary>The line to render (cut at <see cref="MaxLineChars"/>) and how many characters were left out.</summary>
    public static string ClipLine(string line, out int hidden)
    {
        if (line.Length <= MaxLineChars)
        {
            hidden = 0;
            return line;
        }

        hidden = line.Length - MaxLineChars;
        return line[..MaxLineChars];
    }

    /// <summary>
    /// Markdown source cut at a line boundary within <see cref="MaxMarkdownChars"/>; the cut sits after a
    /// blank line when there is one nearby so it does not split a paragraph or a fenced block mid-way.
    /// </summary>
    public static string CapMarkdown(string text, out bool capped)
    {
        if (text.Length <= MaxMarkdownChars)
        {
            capped = false;
            return text;
        }

        capped = true;
        var cut = text.LastIndexOf('\n', MaxMarkdownChars);
        if (cut < MaxMarkdownChars / 2)
        {
            cut = MaxMarkdownChars;
        }

        // Prefer the end of a paragraph shortly before the limit over the middle of one.
        var paragraph = text.LastIndexOf("\n\n", cut, StringComparison.Ordinal);
        if (paragraph > cut - 4_000)
        {
            cut = paragraph;
        }

        return text[..cut];
    }
}
