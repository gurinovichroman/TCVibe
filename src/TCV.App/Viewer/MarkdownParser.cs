using System.Text;

namespace TCV.App.Viewer;

/// <summary>Inline emphasis carried by a run of markdown text.</summary>
[Flags]
public enum MarkdownStyle
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Code = 4,
    Strikethrough = 8,
    Link = 16,
}

/// <summary>A run of text inside a markdown block, with the emphasis that applies to it.</summary>
public sealed record MarkdownInline(string Text, MarkdownStyle Style = MarkdownStyle.None, string? Link = null);

/// <summary>The block kinds the viewer renders.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    /// <summary>A fenced or indented code block; <see cref="MarkdownBlock.Text"/> holds it verbatim.</summary>
    Code,
    Quote,
    ListItem,
    /// <summary>A horizontal rule; carries no text.</summary>
    Rule,
}

/// <summary>One parsed markdown block.</summary>
/// <param name="Kind">What the block is.</param>
/// <param name="Inlines">Styled runs, for everything but code blocks and rules.</param>
/// <param name="Level">Heading level 1..6, or the nesting depth of a list item.</param>
/// <param name="Text">Raw text — the body of a code block.</param>
/// <param name="Language">Info string of a fenced code block, for syntax highlighting.</param>
/// <param name="Ordered">True for a numbered list item.</param>
/// <param name="Marker">The bullet or number to print in front of a list item.</param>
public sealed record MarkdownBlock(
    MarkdownBlockKind Kind,
    IReadOnlyList<MarkdownInline> Inlines,
    int Level = 0,
    string Text = "",
    string? Language = null,
    bool Ordered = false,
    string Marker = "");

/// <summary>
/// A small CommonMark subset, parsed into blocks the viewer can render: headings, fenced code,
/// block quotes, bullet and numbered lists, horizontal rules and paragraphs, with inline bold,
/// italic, code, strikethrough and links.
/// </summary>
/// <remarks>
/// Pure and WPF-free so it can be unit-tested. Deliberately not a full CommonMark implementation:
/// reference links, nested block structures, HTML blocks and tables are left to render as their
/// literal text rather than pull a markdown engine into a file manager.
/// </remarks>
public static class MarkdownParser
{
    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var lines = SyntaxHighlighter.SplitLines(text);
        var blocks = new List<MarkdownBlock>();
        var paragraph = new List<string>();

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            // ── Fenced code ───────────────────────────────────────────────────
            if (TryFence(line, out var fence, out var language))
            {
                FlushParagraph(blocks, paragraph);

                var body = new StringBuilder();
                i++;
                for (; i < lines.Count; i++)
                {
                    // Only a fence of at least the opening length closes it, per CommonMark.
                    if (TryFence(lines[i], out var closing, out _) && closing.Length >= fence.Length &&
                        closing[0] == fence[0]) break;
                    body.AppendLine(lines[i]);
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, [],
                    Text: body.ToString().TrimEnd('\r', '\n'), Language: language));
                continue;
            }

            // ── Blank line ends a paragraph ───────────────────────────────────
            if (line.Trim().Length == 0)
            {
                FlushParagraph(blocks, paragraph);
                continue;
            }

            // ── Horizontal rule ───────────────────────────────────────────────
            if (IsRule(line))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, []));
                continue;
            }

            // ── Heading ───────────────────────────────────────────────────────
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                var level = 0;
                while (level < trimmed.Length && trimmed[level] == '#') level++;
                if (level <= 6 && (level == trimmed.Length || trimmed[level] == ' '))
                {
                    FlushParagraph(blocks, paragraph);
                    var content = trimmed[level..].Trim().TrimEnd('#').TrimEnd();
                    blocks.Add(new MarkdownBlock(MarkdownBlockKind.Heading, ParseInlines(content), level));
                    continue;
                }
            }

            // ── Block quote ───────────────────────────────────────────────────
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote,
                    ParseInlines(trimmed[1..].TrimStart())));
                continue;
            }

            // ── List item ─────────────────────────────────────────────────────
            if (TryListItem(line, out var marker, out var ordered, out var indent, out var content2))
            {
                FlushParagraph(blocks, paragraph);
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.ListItem, ParseInlines(content2),
                    Level: indent, Ordered: ordered, Marker: marker));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph(blocks, paragraph);
        return blocks;
    }

    // ── Block helpers ─────────────────────────────────────────────────────────

    private static void FlushParagraph(List<MarkdownBlock> blocks, List<string> paragraph)
    {
        if (paragraph.Count == 0) return;
        blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph,
            ParseInlines(string.Join(" ", paragraph))));
        paragraph.Clear();
    }

    private static bool TryFence(string line, out string fence, out string? language)
    {
        fence = string.Empty;
        language = null;

        var trimmed = line.TrimStart();
        if (trimmed.Length < 3) return false;
        var ch = trimmed[0];
        if (ch != '`' && ch != '~') return false;

        var count = 0;
        while (count < trimmed.Length && trimmed[count] == ch) count++;
        if (count < 3) return false;

        fence = new string(ch, count);
        var info = trimmed[count..].Trim();
        language = info.Length == 0 ? null : info.Split(' ')[0];
        return true;
    }

    private static bool IsRule(string line)
    {
        var t = line.Trim();
        if (t.Length < 3) return false;
        var ch = t[0];
        if (ch != '-' && ch != '*' && ch != '_') return false;
        return t.All(c => c == ch || c == ' ');
    }

    private static bool TryListItem(
        string line, out string marker, out bool ordered, out int indent, out string content)
    {
        marker = string.Empty;
        ordered = false;
        content = string.Empty;

        indent = 0;
        while (indent < line.Length && (line[indent] == ' ' || line[indent] == '\t')) indent++;
        var rest = line[indent..];
        if (rest.Length < 2) return false;

        // Bullet: "- text" / "* text" / "+ text". A bare "-" with no space is not a list item.
        if ((rest[0] == '-' || rest[0] == '*' || rest[0] == '+') && rest[1] == ' ')
        {
            marker = "•";
            content = rest[2..].Trim();

            // Task list: "- [ ] text" / "- [x] text" reads far better with a real box.
            if (content.StartsWith("[ ] ", StringComparison.Ordinal)) { marker = "☐"; content = content[4..]; }
            else if (content.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) { marker = "☑"; content = content[4..]; }
            return true;
        }

        // Ordered: "1. text" or "1) text"
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits])) digits++;
        if (digits > 0 && digits + 1 < rest.Length && (rest[digits] == '.' || rest[digits] == ')') &&
            rest[digits + 1] == ' ')
        {
            marker = rest[..(digits + 1)];
            ordered = true;
            content = rest[(digits + 2)..].Trim();
            return true;
        }

        return false;
    }

    // ── Inline parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Splits a line into styled runs. Code spans win over every other marker (that is what
    /// backticks are for), and an unmatched marker stays literal text instead of swallowing the
    /// rest of the line.
    /// </summary>
    public static IReadOnlyList<MarkdownInline> ParseInlines(string text)
    {
        if (text.Length == 0) return Array.Empty<MarkdownInline>();

        var runs = new List<MarkdownInline>();
        var plain = new StringBuilder();
        var i = 0;

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            runs.Add(new MarkdownInline(plain.ToString()));
            plain.Clear();
        }

        while (i < text.Length)
        {
            var c = text[i];

            // Escaped marker: "\*" is a literal asterisk.
            if (c == '\\' && i + 1 < text.Length && "*_`~[]\\".Contains(text[i + 1]))
            {
                plain.Append(text[i + 1]);
                i += 2;
                continue;
            }

            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(text[(i + 1)..close], MarkdownStyle.Code));
                    i = close + 1;
                    continue;
                }
            }

            if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var close = text.IndexOf(new string(c, 2), i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(text[(i + 2)..close], MarkdownStyle.Bold));
                    i = close + 2;
                    continue;
                }
            }

            if (c == '*' || c == '_')
            {
                var close = text.IndexOf(c, i + 1);
                if (close > i + 1)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(text[(i + 1)..close], MarkdownStyle.Italic));
                    i = close + 1;
                    continue;
                }
            }

            if (c == '~' && i + 1 < text.Length && text[i + 1] == '~')
            {
                var close = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(text[(i + 2)..close], MarkdownStyle.Strikethrough));
                    i = close + 2;
                    continue;
                }
            }

            if (c == '[' && TryLink(text, i, out var label, out var url, out var next))
            {
                FlushPlain();
                runs.Add(new MarkdownInline(label, MarkdownStyle.Link, url));
                i = next;
                continue;
            }

            plain.Append(c);
            i++;
        }

        FlushPlain();
        return runs;
    }

    private static bool TryLink(string text, int start, out string label, out string url, out int next)
    {
        label = string.Empty;
        url = string.Empty;
        next = start;

        var close = text.IndexOf(']', start + 1);
        if (close < 0 || close + 1 >= text.Length || text[close + 1] != '(') return false;

        var urlEnd = text.IndexOf(')', close + 2);
        if (urlEnd < 0) return false;

        label = text[(start + 1)..close];
        // "[text](url \"title\")" — the title is not shown, so drop it.
        url = text[(close + 2)..urlEnd].Split(' ')[0];
        next = urlEnd + 1;
        return true;
    }
}
