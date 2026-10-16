using FluentAssertions;
using TCV.App.Viewer;

namespace TCV.App.Tests;

public class MarkdownParserTests
{
    private static string Flatten(MarkdownBlock block) =>
        string.Concat(block.Inlines.Select(i => i.Text));

    // ── Blocks ────────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_Headings_CarryTheirLevel()
    {
        var blocks = MarkdownParser.Parse("# One\n\n### Three\n");

        blocks.Should().HaveCount(2);
        blocks[0].Kind.Should().Be(MarkdownBlockKind.Heading);
        blocks[0].Level.Should().Be(1);
        Flatten(blocks[0]).Should().Be("One");
        blocks[1].Level.Should().Be(3);
    }

    [Fact]
    public void Parse_HashWithoutSpace_IsNotAHeading()
    {
        // "#hashtag" is text; only "# " opens a heading, as CommonMark specifies.
        var blocks = MarkdownParser.Parse("#hashtag here");

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(MarkdownBlockKind.Paragraph);
    }

    [Fact]
    public void Parse_SevenHashes_IsNotAHeading()
    {
        MarkdownParser.Parse("####### too deep")[0].Kind.Should().Be(MarkdownBlockKind.Paragraph);
    }

    [Fact]
    public void Parse_FencedCode_KeepsBodyVerbatimAndRecordsLanguage()
    {
        var blocks = MarkdownParser.Parse("```csharp\nvar x = 1;\n  indented\n```\n");

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(MarkdownBlockKind.Code);
        blocks[0].Language.Should().Be("csharp");
        blocks[0].Text.Should().Be("var x = 1;\r\n  indented");
    }

    [Fact]
    public void Parse_UnclosedFence_SwallowsTheRestWithoutThrowing()
    {
        var blocks = MarkdownParser.Parse("```\nline one\nline two");

        blocks.Should().ContainSingle();
        blocks[0].Kind.Should().Be(MarkdownBlockKind.Code);
    }

    [Fact]
    public void Parse_MarkersInsideCodeFence_StayLiteral()
    {
        var blocks = MarkdownParser.Parse("```\n# not a heading\n- not a list\n```");

        blocks.Should().ContainSingle();
        blocks[0].Text.Should().Contain("# not a heading");
    }

    [Fact]
    public void Parse_BulletAndOrderedLists_AreListItems()
    {
        var blocks = MarkdownParser.Parse("- first\n- second\n\n1. one\n2. two\n");

        blocks.Should().HaveCount(4);
        blocks.Should().OnlyContain(b => b.Kind == MarkdownBlockKind.ListItem);
        blocks[0].Ordered.Should().BeFalse();
        blocks[0].Marker.Should().Be("•");
        blocks[2].Ordered.Should().BeTrue();
        blocks[2].Marker.Should().Be("1.");
        Flatten(blocks[3]).Should().Be("two");
    }

    [Fact]
    public void Parse_TaskList_GetsCheckboxMarkers()
    {
        var blocks = MarkdownParser.Parse("- [ ] todo\n- [x] done\n");

        blocks[0].Marker.Should().Be("☐");
        Flatten(blocks[0]).Should().Be("todo");
        blocks[1].Marker.Should().Be("☑");
        Flatten(blocks[1]).Should().Be("done");
    }

    [Fact]
    public void Parse_NestedList_RecordsIndent()
    {
        var blocks = MarkdownParser.Parse("- top\n    - nested\n");

        blocks[0].Level.Should().Be(0);
        blocks[1].Level.Should().Be(4);
    }

    [Fact]
    public void Parse_Quote_AndRule_AreTheirOwnBlocks()
    {
        var blocks = MarkdownParser.Parse("> quoted\n\n---\n");

        blocks[0].Kind.Should().Be(MarkdownBlockKind.Quote);
        Flatten(blocks[0]).Should().Be("quoted");
        blocks[1].Kind.Should().Be(MarkdownBlockKind.Rule);
    }

    [Fact]
    public void Parse_ConsecutiveLines_JoinIntoOneParagraph()
    {
        var blocks = MarkdownParser.Parse("first line\nsecond line\n\nnew paragraph\n");

        blocks.Should().HaveCount(2);
        Flatten(blocks[0]).Should().Be("first line second line");
    }

    [Fact]
    public void Parse_Empty_YieldsNothing() => MarkdownParser.Parse("").Should().BeEmpty();

    // ── Inlines ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParseInlines_Bold_Italic_Code_Strike()
    {
        var runs = MarkdownParser.ParseInlines("a **b** c *d* e `f` g ~~h~~");

        runs.Should().Contain(r => r.Text == "b" && r.Style == MarkdownStyle.Bold);
        runs.Should().Contain(r => r.Text == "d" && r.Style == MarkdownStyle.Italic);
        runs.Should().Contain(r => r.Text == "f" && r.Style == MarkdownStyle.Code);
        runs.Should().Contain(r => r.Text == "h" && r.Style == MarkdownStyle.Strikethrough);
    }

    [Fact]
    public void ParseInlines_CodeSpanWins_OverOtherMarkers()
    {
        var runs = MarkdownParser.ParseInlines("`**not bold**`");

        runs.Should().ContainSingle();
        runs[0].Style.Should().Be(MarkdownStyle.Code);
        runs[0].Text.Should().Be("**not bold**");
    }

    [Fact]
    public void ParseInlines_Link_KeepsLabelAndTarget()
    {
        var runs = MarkdownParser.ParseInlines("see [docs](https://example.com) now");

        var link = runs.Single(r => r.Style == MarkdownStyle.Link);
        link.Text.Should().Be("docs");
        link.Link.Should().Be("https://example.com");
    }

    [Fact]
    public void ParseInlines_LinkWithTitle_DropsTheTitle()
    {
        MarkdownParser.ParseInlines("[a](http://x \"t\")")
            .Single(r => r.Style == MarkdownStyle.Link).Link.Should().Be("http://x");
    }

    [Fact]
    public void ParseInlines_UnmatchedMarker_StaysLiteral()
    {
        var runs = MarkdownParser.ParseInlines("2 * 3 * 4 is not italic when unpaired: *");

        // The last lone asterisk has no partner and must survive as text.
        string.Concat(runs.Select(r => r.Text)).Should().EndWith("*");
    }

    [Fact]
    public void ParseInlines_EscapedMarker_IsLiteral()
    {
        var runs = MarkdownParser.ParseInlines(@"a \*not italic\* b");

        runs.Should().NotContain(r => r.Style == MarkdownStyle.Italic);
        string.Concat(runs.Select(r => r.Text)).Should().Be("a *not italic* b");
    }

    [Fact]
    public void ParseInlines_PlainText_IsOneRun()
    {
        var runs = MarkdownParser.ParseInlines("nothing special here");

        runs.Should().ContainSingle();
        runs[0].Style.Should().Be(MarkdownStyle.None);
    }
}
