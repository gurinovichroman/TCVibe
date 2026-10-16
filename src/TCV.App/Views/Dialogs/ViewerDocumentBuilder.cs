using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using TCV.App.Localization;
using TCV.App.Viewer;

namespace TCV.App.Views.Dialogs;

/// <summary>
/// Turns the viewer's pure models — <see cref="StyledSpan"/> runs and <see cref="MarkdownBlock"/>
/// blocks — into a WPF <see cref="FlowDocument"/>. The only part of the code preview that knows
/// about WPF; everything it consumes is unit-tested without a window (6.18).
/// </summary>
internal static class ViewerDocumentBuilder
{
    /// <summary>
    /// How much of the text the code view builds is bounded by <see cref="RenderLimits"/>: lines,
    /// characters and the length of one line. The plain-text mode is still there for the whole window.
    /// </summary>
    public const int MaxRenderedLines = RenderLimits.MaxLines;

    private static readonly FontFamily Mono = new("Consolas, Cascadia Mono, Courier New");

    // ── Code ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders source with a line-number gutter and syntax colours, or a unified diff coloured
    /// per line when <paramref name="asDiff"/> is set. A window from the middle of a file passes
    /// <paramref name="showLineNumbers"/> false: it cannot know how many lines came before it.
    /// </summary>
    public static FlowDocument BuildCode(
        string text, SyntaxLanguage language, bool asDiff, bool wrap, ResourceDictionary theme,
        bool showLineNumbers = true)
    {
        var document = NewDocument(wrap);
        document.FontFamily = Mono;
        document.FontSize = 13;

        var lines = SyntaxHighlighter.SplitLines(text);
        var rendered = RenderLimits.LinesWithinBudget(lines);
        var gutterWidth = (rendered).ToString().Length;
        var gutterBrush = Brush(theme, "SyntaxGutterBrush", Brushes.Gray);
        var dimBrush = Brush(theme, "ForegroundDimBrush", Brushes.Gray);
        var clippedLines = 0;

        var state = SyntaxHighlighter.LineState.Initial;
        var paragraph = NewParagraph(wrap);

        for (var i = 0; i < rendered; i++)
        {
            var line = RenderLimits.ClipLine(lines[i], out var hidden);

            if (showLineNumbers)
            {
                paragraph.Inlines.Add(new Run((i + 1).ToString().PadLeft(gutterWidth) + "  ")
                {
                    Foreground = gutterBrush,
                });
            }

            if (asDiff)
            {
                var kind = SyntaxHighlighter.ClassifyDiffLine(line);
                paragraph.Inlines.Add(new Run(line) { Foreground = BrushFor(kind, theme) });
            }
            else
            {
                var (spans, next) = SyntaxHighlighter.HighlightLine(line, language, state);
                state = next;
                AppendSpans(paragraph.Inlines, line, spans, theme);
            }

            if (hidden > 0)
            {
                clippedLines++;
                paragraph.Inlines.Add(new Run(string.Format(LocalizationManager.Get("Viewer_LongLine"), hidden.ToString("N0")))
                {
                    Foreground = dimBrush,
                    FontStyle = FontStyles.Italic,
                });
            }

            if (i < rendered - 1) paragraph.Inlines.Add(new LineBreak());
        }

        document.Blocks.Add(paragraph);

        if (lines.Count > rendered)
            document.Blocks.Add(Note(string.Format(LocalizationManager.Get("Viewer_MoreLines"), lines.Count - rendered), theme));
        else if (clippedLines > 0)
        {
            document.Blocks.Add(Note(string.Format(LocalizationManager.Get("Viewer_LinesClipped"), clippedLines), theme));
        }

        return document;
    }

    /// <summary>Emits the coloured spans of one line, with the uncoloured gaps between them.</summary>
    private static void AppendSpans(
        InlineCollection target, string line, IReadOnlyList<StyledSpan> spans, ResourceDictionary theme)
    {
        var at = 0;
        foreach (var span in spans)
        {
            // A malformed span must not throw inside a viewer; clamp instead.
            var start = Math.Clamp(span.Start, 0, line.Length);
            var end = Math.Clamp(span.Start + span.Length, start, line.Length);
            if (start < at) continue;

            if (start > at) target.Add(new Run(line[at..start]));
            if (end > start) target.Add(new Run(line[start..end]) { Foreground = BrushFor(span.Kind, theme) });
            at = end;
        }
        if (at < line.Length) target.Add(new Run(line[at..]));
    }

    // ── Markdown ──────────────────────────────────────────────────────────────

    /// <summary>Renders parsed markdown: headings, lists, quotes, rules and fenced code.</summary>
    public static FlowDocument BuildMarkdown(string text, bool wrap, ResourceDictionary theme)
    {
        var document = NewDocument(wrap);
        document.FontFamily = new FontFamily("Segoe UI, Arial");
        document.FontSize = 14;

        // A huge source is cut before parsing: every block becomes several WPF elements.
        var source = RenderLimits.CapMarkdown(text, out var sourceCapped);
        var blocks = MarkdownParser.Parse(source);
        var rendered = 0;
        foreach (var block in blocks)
        {
            if (rendered++ >= RenderLimits.MaxMarkdownBlocks)
            {
                sourceCapped = true;
                break;
            }

            switch (block.Kind)
            {
                case MarkdownBlockKind.Heading:
                    document.Blocks.Add(Heading(block, theme));
                    break;

                case MarkdownBlockKind.Code:
                    document.Blocks.Add(CodeBlock(block, theme));
                    break;

                case MarkdownBlockKind.Quote:
                    document.Blocks.Add(Quote(block, theme));
                    break;

                case MarkdownBlockKind.ListItem:
                    document.Blocks.Add(ListItem(block, theme));
                    break;

                case MarkdownBlockKind.Rule:
                    document.Blocks.Add(Rule(theme));
                    break;

                default:
                    var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    AppendInlines(paragraph.Inlines, block.Inlines, theme);
                    document.Blocks.Add(paragraph);
                    break;
            }
        }

        if (sourceCapped)
        {
            document.Blocks.Add(Note(string.Format(LocalizationManager.Get("Viewer_MarkdownCapped"), Math.Min(rendered, RenderLimits.MaxMarkdownBlocks).ToString("N0")), theme));
        }

        return document;
    }

    private static Block Heading(MarkdownBlock block, ResourceDictionary theme)
    {
        // 22pt down to 13pt across the six levels, so the outline is readable at a glance.
        var size = Math.Max(13.0, 24.0 - block.Level * 2.0);
        var paragraph = new Paragraph
        {
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, block.Level == 1 ? 4 : 12, 0, 6),
            Foreground = Brush(theme, "AccentBrush", Brushes.SteelBlue),
        };
        AppendInlines(paragraph.Inlines, block.Inlines, theme);
        return paragraph;
    }

    private static Block CodeBlock(MarkdownBlock block, ResourceDictionary theme)
    {
        // A fenced block carries its language, so it gets the same highlighter as a source file.
        var language = block.Language is null
            ? SyntaxLanguage.None
            : SyntaxLanguage.ForFile("x." + block.Language.ToLowerInvariant());

        var paragraph = new Paragraph
        {
            FontFamily = Mono,
            FontSize = 12.5,
            Background = Brush(theme, "SyntaxCodeBackBrush", Brushes.Transparent),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var lines = SyntaxHighlighter.SplitLines(block.Text);
        var state = SyntaxHighlighter.LineState.Initial;
        for (var i = 0; i < lines.Count; i++)
        {
            var (spans, next) = SyntaxHighlighter.HighlightLine(lines[i], language, state);
            state = next;
            AppendSpans(paragraph.Inlines, lines[i], spans, theme);
            if (i < lines.Count - 1) paragraph.Inlines.Add(new LineBreak());
        }

        return paragraph;
    }

    private static Block Quote(MarkdownBlock block, ResourceDictionary theme)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 8),
            Padding = new Thickness(10, 2, 0, 2),
            BorderBrush = Brush(theme, "BorderBrush", Brushes.Gray),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Foreground = Brush(theme, "ForegroundDimBrush", Brushes.Gray),
        };
        AppendInlines(paragraph.Inlines, block.Inlines, theme);
        return paragraph;
    }

    private static Block ListItem(MarkdownBlock block, ResourceDictionary theme)
    {
        // Nesting is rendered as indentation rather than as real WPF lists: the parser reports a
        // flat sequence with an indent level, which keeps both sides simple.
        var indent = 14 + block.Level * 6;
        var paragraph = new Paragraph { Margin = new Thickness(indent, 0, 0, 3), TextIndent = -14 };
        paragraph.Inlines.Add(new Run(block.Marker + " ")
        {
            Foreground = Brush(theme, "AccentBrush", Brushes.SteelBlue),
        });
        AppendInlines(paragraph.Inlines, block.Inlines, theme);
        return paragraph;
    }

    private static Block Rule(ResourceDictionary theme) => new BlockUIContainer(new Border
    {
        BorderBrush = Brush(theme, "BorderBrush", Brushes.Gray),
        BorderThickness = new Thickness(0, 1, 0, 0),
        Margin = new Thickness(0, 6, 0, 10),
    });

    private static void AppendInlines(
        InlineCollection target, IReadOnlyList<MarkdownInline> inlines, ResourceDictionary theme)
    {
        foreach (var run in inlines)
        {
            var element = new Run(run.Text);

            if (run.Style.HasFlag(MarkdownStyle.Bold)) element.FontWeight = FontWeights.Bold;
            if (run.Style.HasFlag(MarkdownStyle.Italic)) element.FontStyle = FontStyles.Italic;
            if (run.Style.HasFlag(MarkdownStyle.Strikethrough)) element.TextDecorations = TextDecorations.Strikethrough;
            if (run.Style.HasFlag(MarkdownStyle.Code))
            {
                element.FontFamily = Mono;
                element.Background = Brush(theme, "SyntaxCodeBackBrush", Brushes.Transparent);
                element.Foreground = Brush(theme, "SyntaxStringBrush", Brushes.Firebrick);
            }
            if (run.Style.HasFlag(MarkdownStyle.Link))
            {
                // Rendered as coloured, underlined text with the target in a tooltip rather than
                // as a live Hyperlink: the viewer must not become a way to launch arbitrary URLs.
                element.Foreground = Brush(theme, "AccentBrush", Brushes.SteelBlue);
                element.TextDecorations = TextDecorations.Underline;
                element.ToolTip = run.Link;
            }

            target.Add(element);
        }
    }

    // ── Shared ────────────────────────────────────────────────────────────────

    private static FlowDocument NewDocument(bool wrap) => new()
    {
        PagePadding = new Thickness(12, 10, 12, 10),
        // Without a huge page width the document reflows to the window even in no-wrap mode.
        PageWidth = wrap ? double.NaN : 20_000,
        IsOptimalParagraphEnabled = false,
        IsHyphenationEnabled = false,
    };

    private static Paragraph NewParagraph(bool wrap) => new()
    {
        Margin = new Thickness(0),
        LineHeight = 17,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextAlignment = TextAlignment.Left,
        KeepTogether = false,
        TextIndent = 0,
        FontStretch = FontStretches.Normal,
        // Long lines scroll horizontally instead of reflowing when wrapping is off.
        Padding = new Thickness(0),
    };

    private static Block Note(string text, ResourceDictionary theme) => new Paragraph(new Run(text))
    {
        Foreground = Brush(theme, "ForegroundDimBrush", Brushes.Gray),
        FontStyle = FontStyles.Italic,
        Margin = new Thickness(0, 10, 0, 0),
    };

    private static Brush BrushFor(TokenKind kind, ResourceDictionary theme) => kind switch
    {
        TokenKind.Keyword => Brush(theme, "SyntaxKeywordBrush", Brushes.RoyalBlue),
        TokenKind.Type => Brush(theme, "SyntaxTypeBrush", Brushes.Teal),
        TokenKind.String => Brush(theme, "SyntaxStringBrush", Brushes.Firebrick),
        TokenKind.Number => Brush(theme, "SyntaxNumberBrush", Brushes.SeaGreen),
        TokenKind.Comment => Brush(theme, "SyntaxCommentBrush", Brushes.Green),
        TokenKind.Meta => Brush(theme, "SyntaxMetaBrush", Brushes.Purple),
        TokenKind.Added => Brush(theme, "SyntaxAddedBrush", Brushes.Green),
        TokenKind.Removed => Brush(theme, "SyntaxRemovedBrush", Brushes.IndianRed),
        TokenKind.Hunk => Brush(theme, "SyntaxHunkBrush", Brushes.RoyalBlue),
        _ => Brush(theme, "ForegroundBrush", Brushes.Black),
    };

    /// <summary>
    /// Looks a brush up in the active theme, falling back to a fixed colour. A missing key must
    /// not throw: the viewer is also opened from windows whose theme dictionary is not merged yet.
    /// </summary>
    private static Brush Brush(ResourceDictionary theme, string key, Brush fallback) =>
        theme[key] as Brush ?? fallback;
}
