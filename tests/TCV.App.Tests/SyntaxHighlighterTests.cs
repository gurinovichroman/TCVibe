using FluentAssertions;
using TCV.App.Viewer;

namespace TCV.App.Tests;

public class SyntaxHighlighterTests
{
    private static IReadOnlyList<StyledSpan> Spans(string line, SyntaxLanguage language) =>
        SyntaxHighlighter.HighlightLine(line, language, SyntaxHighlighter.LineState.Initial).Spans;

    /// <summary>Text a span covers — the assertion that matters, since offsets alone say little.</summary>
    private static string Text(string line, StyledSpan span) => line.Substring(span.Start, span.Length);

    // ── Words ─────────────────────────────────────────────────────────────────

    [Fact]
    public void HighlightLine_Keyword_IsMarked()
    {
        const string line = "public void Run()";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        spans.Should().Contain(s => s.Kind == TokenKind.Keyword && Text(line, s) == "public");
        spans.Should().Contain(s => s.Kind == TokenKind.Type && Text(line, s) == "void");
    }

    [Fact]
    public void HighlightLine_KeywordInsideIdentifier_IsNotMarked()
    {
        const string line = "publicity = 1;";
        Spans(line, SyntaxLanguage.CSharp).Should().NotContain(s => s.Kind == TokenKind.Keyword);
    }

    [Fact]
    public void HighlightLine_CaseInsensitiveLanguage_MatchesAnyCasing()
    {
        const string line = "IF ($x) { }";
        Spans(line, SyntaxLanguage.PowerShell)
            .Should().Contain(s => s.Kind == TokenKind.Keyword && Text(line, s) == "IF");
    }

    // ── Strings ───────────────────────────────────────────────────────────────

    [Fact]
    public void HighlightLine_String_CoversQuotes()
    {
        const string line = "var s = \"hello\";";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        spans.Should().Contain(s => s.Kind == TokenKind.String && Text(line, s) == "\"hello\"");
    }

    [Fact]
    public void HighlightLine_KeywordInsideString_IsNotHighlighted()
    {
        const string line = "var s = \"public class\";";
        var spans = Spans(line, SyntaxLanguage.CSharp);
        var quote = line.IndexOf('"');

        // "var" outside the literal is still a keyword; the literal itself is one string span
        // covering both words inside it.
        spans.Should().Contain(s => s.Kind == TokenKind.Keyword && Text(line, s) == "var");
        spans.Where(s => s.Start >= quote).Should().ContainSingle()
            .Which.Kind.Should().Be(TokenKind.String);
    }

    [Fact]
    public void HighlightLine_EscapedQuote_DoesNotEndTheString()
    {
        const string line = "\"a\\\"b\" + c";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        Text(line, spans.First(s => s.Kind == TokenKind.String)).Should().Be("\"a\\\"b\"");
    }

    [Fact]
    public void HighlightLine_UnterminatedString_RunsToEndOfLineWithoutThrowing()
    {
        const string line = "var s = \"oops";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        Text(line, spans.First(s => s.Kind == TokenKind.String)).Should().Be("\"oops");
    }

    // ── Comments ──────────────────────────────────────────────────────────────

    [Fact]
    public void HighlightLine_LineComment_CoversTheRestOfTheLine()
    {
        const string line = "x = 1; // set to one";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        Text(line, spans.Last()).Should().Be("// set to one");
        spans.Last().Kind.Should().Be(TokenKind.Comment);
    }

    [Fact]
    public void HighlightLine_BlockCommentSpansLines_UntilItCloses()
    {
        var (spans1, state1) = SyntaxHighlighter.HighlightLine(
            "/* start", SyntaxLanguage.CSharp, SyntaxHighlighter.LineState.Initial);
        var (spans2, state2) = SyntaxHighlighter.HighlightLine("still comment", SyntaxLanguage.CSharp, state1);
        var (spans3, state3) = SyntaxHighlighter.HighlightLine("end */ code", SyntaxLanguage.CSharp, state2);

        state1.InBlockComment.Should().BeTrue();
        state2.InBlockComment.Should().BeTrue();
        state3.InBlockComment.Should().BeFalse();

        spans1.Should().ContainSingle(s => s.Kind == TokenKind.Comment);
        spans2.Should().ContainSingle(s => s.Kind == TokenKind.Comment);
        spans3.First().Kind.Should().Be(TokenKind.Comment);
        spans3.First().Length.Should().Be("end */".Length);
    }

    [Fact]
    public void HighlightLine_CommentMarkerInsideString_DoesNotStartAComment()
    {
        const string line = "var url = \"http://example.com\";";
        Spans(line, SyntaxLanguage.CSharp).Should().NotContain(s => s.Kind == TokenKind.Comment);
    }

    // ── Numbers, preprocessor, markup ─────────────────────────────────────────

    [Fact]
    public void HighlightLine_Numbers_AreMarked()
    {
        const string line = "var a = 42; var b = 0xFF; var c = 3.14;";
        var spans = Spans(line, SyntaxLanguage.CSharp).Where(s => s.Kind == TokenKind.Number).ToList();

        spans.Select(s => Text(line, s)).Should().BeEquivalentTo("42", "0xFF", "3.14");
    }

    [Fact]
    public void HighlightLine_DigitsInsideIdentifier_AreNotANumber()
    {
        const string line = "var utf8 = 1;";
        Spans(line, SyntaxLanguage.CSharp)
            .Where(s => s.Kind == TokenKind.Number)
            .Select(s => Text(line, s))
            .Should().BeEquivalentTo("1");
    }

    [Fact]
    public void HighlightLine_PreprocessorLine_IsMetadata()
    {
        const string line = "    #if DEBUG";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        spans.Should().ContainSingle();
        spans[0].Kind.Should().Be(TokenKind.Meta);
        Text(line, spans[0]).Should().Be("#if DEBUG");
    }

    [Fact]
    public void HighlightLine_MarkupTag_IsMarkedAsType()
    {
        const string line = "<Button Content=\"OK\" />";
        var spans = Spans(line, SyntaxLanguage.Markup);

        Text(line, spans[0]).Should().Be("<Button");
        spans[0].Kind.Should().Be(TokenKind.Type);
        spans.Should().Contain(s => s.Kind == TokenKind.String && Text(line, s) == "\"OK\"");
    }

    [Fact]
    public void HighlightLine_UnknownLanguage_ProducesNoSpans()
    {
        Spans("public class X {}", SyntaxLanguage.None).Should().BeEmpty();
    }

    [Fact]
    public void HighlightLine_SpansAreOrderedAndInsideTheLine()
    {
        const string line = "public string s = \"x\"; // note";
        var spans = Spans(line, SyntaxLanguage.CSharp);

        var at = 0;
        foreach (var span in spans)
        {
            span.Start.Should().BeGreaterThanOrEqualTo(at);
            (span.Start + span.Length).Should().BeLessThanOrEqualTo(line.Length);
            at = span.Start + span.Length;
        }
    }

    // ── Diff ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("+added line", TokenKind.Added)]
    [InlineData("-removed line", TokenKind.Removed)]
    [InlineData("@@ -1,4 +1,6 @@", TokenKind.Hunk)]
    [InlineData("--- a/file.cs", TokenKind.Meta)]
    [InlineData("+++ b/file.cs", TokenKind.Meta)]
    [InlineData("diff --git a/x b/x", TokenKind.Meta)]
    [InlineData(" context", TokenKind.Plain)]
    [InlineData("", TokenKind.Plain)]
    public void ClassifyDiffLine_MapsPrefixes(string line, TokenKind expected) =>
        SyntaxHighlighter.ClassifyDiffLine(line).Should().Be(expected);

    // ── Line splitting ────────────────────────────────────────────────────────

    [Fact]
    public void SplitLines_HandlesCrlfLfAndTrailingNewline()
    {
        SyntaxHighlighter.SplitLines("a\r\nb\nc").Should().BeEquivalentTo(["a", "b", "c"], o => o.WithStrictOrdering());
        SyntaxHighlighter.SplitLines("a\nb\n").Should().BeEquivalentTo(["a", "b"], o => o.WithStrictOrdering());
        SyntaxHighlighter.SplitLines("").Should().BeEmpty();
    }

    // ── Language resolution ───────────────────────────────────────────────────

    [Theory]
    [InlineData("Program.cs", "C#")]
    [InlineData("app.ts", "JavaScript/TypeScript")]
    [InlineData("main.py", "Python")]
    [InlineData("build.ps1", "PowerShell")]
    [InlineData("settings.json", "JSON")]
    [InlineData("ci.yml", "YAML")]
    [InlineData("MainWindow.xaml", "XML/HTML")]
    [InlineData("TCV.App.csproj", "XML/HTML")]
    [InlineData(".gitignore", "INI")]
    [InlineData("Dockerfile", "Shell")]
    [InlineData("photo.png", "Text")]
    public void ForFile_ResolvesByExtension(string fileName, string expected) =>
        SyntaxLanguage.ForFile(fileName).Name.Should().Be(expected);

    [Fact]
    public void ForFile_PathWithDirectories_UsesTheFileNameOnly() =>
        SyntaxLanguage.ForFile(@"D:\repo.cs\readme.md").Name.Should().Be("Text");

    [Theory]
    [InlineData("README.md", true)]
    [InlineData("notes.MARKDOWN", true)]
    [InlineData("readme.txt", false)]
    public void IsMarkdown_DetectsMarkdownFiles(string name, bool expected) =>
        SyntaxLanguage.IsMarkdown(name).Should().Be(expected);

    [Theory]
    [InlineData("fix.patch", true)]
    [InlineData("change.diff", true)]
    [InlineData("a.cs", false)]
    public void IsDiff_DetectsDiffFiles(string name, bool expected) =>
        SyntaxLanguage.IsDiff(name).Should().Be(expected);
}
