using System.Text;

namespace TCV.App.Viewer;

/// <summary>A coloured run of text: an offset into the source line, a length and what it is.</summary>
public readonly record struct StyledSpan(int Start, int Length, TokenKind Kind);

/// <summary>
/// Table-driven lexer that turns source text into coloured spans. Pure: no WPF, no file system —
/// the view walks the spans and applies brushes, which is what makes the whole thing testable.
/// </summary>
/// <remarks>
/// A viewer's highlighter has different goals from a compiler's: it must never throw, never hang
/// on malformed input, and degrade to plain text rather than to wrong colours. It therefore
/// tokenizes line by line, carrying only one bit of cross-line state (the open block comment or
/// multi-line string), which also means a caller can render just the visible window.
/// </remarks>
public static class SyntaxHighlighter
{
    /// <summary>
    /// State carried from one line to the next: index of the open block-comment rule, or -1.
    /// Passing <see cref="LineState.Initial"/> starts a fresh file.
    /// </summary>
    public readonly record struct LineState(int OpenBlockComment)
    {
        public static LineState Initial => new(-1);
        public bool InBlockComment => OpenBlockComment >= 0;
    }

    /// <summary>
    /// Tokenizes one line. Returns the spans that carry a colour — plain text is left out, so a
    /// renderer emits the gaps verbatim — plus the state the next line starts in.
    /// </summary>
    public static (IReadOnlyList<StyledSpan> Spans, LineState Next) HighlightLine(
        string line, SyntaxLanguage language, LineState state)
    {
        if (line.Length == 0 || ReferenceEquals(language, SyntaxLanguage.None))
            return (Array.Empty<StyledSpan>(), state);

        var spans = new List<StyledSpan>();
        var i = 0;

        // A block comment opened on an earlier line runs until its terminator or to the end.
        if (state.InBlockComment)
        {
            var (open, close) = language.BlockComments[state.OpenBlockComment];
            var end = line.IndexOf(close, StringComparison.Ordinal);
            if (end < 0)
            {
                spans.Add(new StyledSpan(0, line.Length, TokenKind.Comment));
                return (spans, state);
            }
            spans.Add(new StyledSpan(0, end + close.Length, TokenKind.Comment));
            i = end + close.Length;
            state = LineState.Initial;
        }

        // A preprocessor / directive line is coloured whole once the leading blanks are past.
        if (language.HasPreprocessor && language.PreprocessorPrefix is { } prefix)
        {
            var firstNonBlank = FirstNonBlank(line, i);
            if (firstNonBlank >= 0 && line.AsSpan(firstNonBlank).StartsWith(prefix, StringComparison.Ordinal))
            {
                spans.Add(new StyledSpan(firstNonBlank, line.Length - firstNonBlank, TokenKind.Meta));
                return (spans, state);
            }
        }

        while (i < line.Length)
        {
            var c = line[i];

            // ── Line comment ──────────────────────────────────────────────────
            var lineComment = MatchAny(line, i, language.LineComments);
            if (lineComment is not null)
            {
                spans.Add(new StyledSpan(i, line.Length - i, TokenKind.Comment));
                break;
            }

            // ── Block comment ─────────────────────────────────────────────────
            var blockIndex = MatchBlockOpen(line, i, language);
            if (blockIndex >= 0)
            {
                var (open, close) = language.BlockComments[blockIndex];
                var end = line.IndexOf(close, i + open.Length, StringComparison.Ordinal);
                if (end < 0)
                {
                    spans.Add(new StyledSpan(i, line.Length - i, TokenKind.Comment));
                    return (spans, new LineState(blockIndex));
                }
                spans.Add(new StyledSpan(i, end + close.Length - i, TokenKind.Comment));
                i = end + close.Length;
                continue;
            }

            // ── String ────────────────────────────────────────────────────────
            if (language.StringDelimiters.Contains(c))
            {
                var end = ScanString(line, i, c);
                spans.Add(new StyledSpan(i, end - i, TokenKind.String));
                i = end;
                continue;
            }

            // ── Markup tag / attribute ────────────────────────────────────────
            if (ReferenceEquals(language, SyntaxLanguage.Markup) && c == '<')
            {
                var end = ScanTagName(line, i);
                spans.Add(new StyledSpan(i, end - i, TokenKind.Type));
                i = end;
                continue;
            }

            // ── Number ────────────────────────────────────────────────────────
            if (char.IsAsciiDigit(c) && !IsIdentifierPart(Previous(line, i)))
            {
                var end = ScanNumber(line, i);
                spans.Add(new StyledSpan(i, end - i, TokenKind.Number));
                i = end;
                continue;
            }

            // ── Word ──────────────────────────────────────────────────────────
            if (IsIdentifierStart(c))
            {
                var end = i;
                while (end < line.Length && IsIdentifierPart(line[end])) end++;
                var word = line[i..end];

                if (language.Keywords.Contains(word)) spans.Add(new StyledSpan(i, end - i, TokenKind.Keyword));
                else if (language.Types.Contains(word)) spans.Add(new StyledSpan(i, end - i, TokenKind.Type));

                i = end;
                continue;
            }

            i++;
        }

        return (spans, state);
    }

    /// <summary>Tokenizes a whole document, one entry per line, carrying block state across lines.</summary>
    public static IReadOnlyList<IReadOnlyList<StyledSpan>> Highlight(
        IReadOnlyList<string> lines, SyntaxLanguage language)
    {
        var result = new List<IReadOnlyList<StyledSpan>>(lines.Count);
        var state = LineState.Initial;
        foreach (var line in lines)
        {
            var (spans, next) = HighlightLine(line, language, state);
            result.Add(spans);
            state = next;
        }
        return result;
    }

    /// <summary>
    /// Classifies one line of a unified diff. Colouring diffs by line beats lexing them: the
    /// content is a mix of languages, and what the reader needs is what changed.
    /// </summary>
    public static TokenKind ClassifyDiffLine(string line)
    {
        if (line.Length == 0) return TokenKind.Plain;
        if (line.StartsWith("@@", StringComparison.Ordinal)) return TokenKind.Hunk;
        // "+++"/"---" are file headers, not content changes, so they read as metadata.
        if (line.StartsWith("+++", StringComparison.Ordinal) ||
            line.StartsWith("---", StringComparison.Ordinal) ||
            line.StartsWith("diff ", StringComparison.Ordinal) ||
            line.StartsWith("index ", StringComparison.Ordinal) ||
            line.StartsWith("new file", StringComparison.Ordinal) ||
            line.StartsWith("deleted file", StringComparison.Ordinal) ||
            line.StartsWith("rename ", StringComparison.Ordinal) ||
            line.StartsWith("similarity ", StringComparison.Ordinal)) return TokenKind.Meta;
        return line[0] switch
        {
            '+' => TokenKind.Added,
            '-' => TokenKind.Removed,
            '\\' => TokenKind.Meta,   // "\ No newline at end of file"
            _ => TokenKind.Plain,
        };
    }

    /// <summary>
    /// Splits text into lines the way an editor numbers them: LF and CRLF both end a line, and a
    /// trailing newline does not produce a phantom empty last line.
    /// </summary>
    public static IReadOnlyList<string> SplitLines(string text)
    {
        if (text.Length == 0) return Array.Empty<string>();

        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            var end = i > start && text[i - 1] == '\r' ? i - 1 : i;
            lines.Add(text[start..end]);
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text[start..].TrimEnd('\r'));
        return lines;
    }

    // ── Scanners ──────────────────────────────────────────────────────────────

    /// <summary>
    /// End offset (exclusive) of the string literal starting at <paramref name="start"/>.
    /// An unterminated literal runs to the end of the line — a viewer must not lose text
    /// because the file is being edited elsewhere.
    /// </summary>
    private static int ScanString(string line, int start, char quote)
    {
        var i = start + 1;
        while (i < line.Length)
        {
            if (line[i] == '\\' && i + 1 < line.Length) { i += 2; continue; }
            if (line[i] == quote)
            {
                // "" inside a string is an escaped quote in C#, SQL and PowerShell alike.
                if (i + 1 < line.Length && line[i + 1] == quote) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return line.Length;
    }

    private static int ScanNumber(string line, int start)
    {
        var i = start;
        // Hex, binary and the usual suffixes; a lone '.' inside is part of the literal.
        if (i + 1 < line.Length && line[i] == '0' && (line[i + 1] is 'x' or 'X' or 'b' or 'B')) i += 2;
        while (i < line.Length && (char.IsAsciiLetterOrDigit(line[i]) || line[i] == '_' ||
                                   (line[i] == '.' && i + 1 < line.Length && char.IsAsciiDigit(line[i + 1]))))
            i++;
        return i;
    }

    /// <summary>End of the tag name in <c>&lt;/name</c> or <c>&lt;name</c>, including the bracket.</summary>
    private static int ScanTagName(string line, int start)
    {
        var i = start + 1;
        if (i < line.Length && (line[i] == '/' || line[i] == '!' || line[i] == '?')) i++;
        while (i < line.Length && (IsIdentifierPart(line[i]) || line[i] == '-' || line[i] == ':' || line[i] == '.'))
            i++;
        return Math.Max(i, start + 1);
    }

    private static string? MatchAny(string line, int i, IReadOnlyList<string> tokens)
    {
        foreach (var token in tokens)
            if (line.AsSpan(i).StartsWith(token, StringComparison.Ordinal)) return token;
        return null;
    }

    private static int MatchBlockOpen(string line, int i, SyntaxLanguage language)
    {
        for (var b = 0; b < language.BlockComments.Count; b++)
            if (line.AsSpan(i).StartsWith(language.BlockComments[b].Open, StringComparison.Ordinal)) return b;
        return -1;
    }

    private static int FirstNonBlank(string line, int from)
    {
        for (var i = from; i < line.Length; i++)
            if (!char.IsWhiteSpace(line[i])) return i;
        return -1;
    }

    private static char Previous(string line, int i) => i > 0 ? line[i - 1] : ' ';

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_' || c == '$' || c == '@';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';
}
