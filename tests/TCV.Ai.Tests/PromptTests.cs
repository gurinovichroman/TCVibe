using FluentAssertions;
using TCV.Ai.Llm;

namespace TCV.Ai.Tests;

/// <summary>
/// Prompt building and — the half that matters — cleaning up what the model sends back. A commit
/// message goes straight into an editable field, so the cleanup is what stands between the user and
/// a code fence in their git history.
/// </summary>
public class PromptTests
{
    private static readonly (string Path, string Status)[] Files =
    [
        ("src/Parser.cs", "M"),
        ("tests/ParserTests.cs", "A"),
    ];

    [Fact]
    public void CommitPrompt_IncludesBranchFilesAndDiff()
    {
        var messages = CommitMessagePrompt.Build("feature/parser", Files, "@@ -1 +1 @@\n-old\n+new");

        messages.Should().HaveCount(2);
        messages[0].Role.Should().Be("system");

        var request = messages[1].Content;
        request.Should().Contain("feature/parser");
        request.Should().Contain("src/Parser.cs");
        request.Should().Contain("+new");
    }

    [Fact]
    public void CommitPrompt_MarksATruncatedDiff()
    {
        var huge = new string('x', CommitMessagePrompt.MaxDiffChars + 500);

        var request = CommitMessagePrompt.Build("main", Files, huge)[1].Content;

        // The model has to know it is not seeing everything, or it will describe the whole change.
        request.Should().Contain("[diff truncated]");
    }

    [Fact]
    public void CommitPrompt_WithoutADiff_StillListsTheFiles()
    {
        var request = CommitMessagePrompt.Build("main", Files, string.Empty)[1].Content;

        request.Should().Contain("src/Parser.cs");
        request.Should().NotContain("Diff:");
    }

    [Fact]
    public void CommitPrompt_SummarisesAVeryLongFileList()
    {
        var many = Enumerable.Range(0, 200).Select(i => ($"file{i}.cs", "M")).ToArray();

        var request = CommitMessagePrompt.Build("main", many, string.Empty)[1].Content;

        request.Should().Contain("and 120 more");
    }

    // ── Clean ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Clean_StripsACodeFence()
    {
        var raw = "```\nfix the parser\n\nIt was dropping trailing commas.\n```";

        var clean = CommitMessagePrompt.Clean(raw);

        clean.Should().StartWith("fix the parser");
        clean.Should().NotContain("```");
    }

    [Fact]
    public void Clean_StripsAFenceWithALanguageTag()
    {
        CommitMessagePrompt.Clean("```text\nfix the parser\n```")
            .Should().Be("fix the parser");
    }

    [Theory]
    [InlineData("Commit message:\nfix the parser")]
    [InlineData("Here is the commit message:\nfix the parser")]
    [InlineData("message: fix the parser")]
    public void Clean_DropsAnAnnouncingLabel(string raw) =>
        CommitMessagePrompt.Clean(raw).Should().Be("fix the parser");

    [Fact]
    public void Clean_StripsQuotesAroundTheSubject() =>
        CommitMessagePrompt.Clean("\"fix the parser\"").Should().Be("fix the parser");

    [Fact]
    public void Clean_ShortensAnOverlongSubjectAtAWordBoundary()
    {
        var subject = "fix the parser so that it stops dropping trailing commas in nested object literals";

        var clean = CommitMessagePrompt.Clean(subject);
        var first = clean.Split('\n')[0];

        first.Length.Should().BeLessThanOrEqualTo(CommitMessagePrompt.MaxSubjectLength);
        first.Should().NotEndWith(" ");
        // Cut at a word boundary, not mid-word.
        subject.Should().StartWith(first);
        first.Should().NotContain("…");
    }

    [Fact]
    public void Clean_KeepsTheBodyIntact()
    {
        var raw = "fix the parser\n\n- trailing commas were dropped\n- added a regression test";

        var clean = CommitMessagePrompt.Clean(raw);

        clean.Should().Contain("- trailing commas were dropped");
        clean.Should().Contain("- added a regression test");
    }

    [Fact]
    public void Clean_TrimsLeadingAndTrailingBlankLines() =>
        CommitMessagePrompt.Clean("\n\nfix the parser\n\n\n").Should().Be("fix the parser");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    public void Clean_EmptyInput_IsEmpty(string raw) =>
        CommitMessagePrompt.Clean(raw).Should().BeEmpty();

    // ── Summary ───────────────────────────────────────────────────────────────

    [Fact]
    public void SummaryPrompt_NamesTheFileAndCarriesTheContent()
    {
        var messages = SummaryPrompt.Build("Parser.cs", "public class Parser { }");

        messages[1].Content.Should().Contain("Parser.cs");
        messages[1].Content.Should().Contain("public class Parser");
    }

    [Fact]
    public void SummaryPrompt_SaysWhenTheContentWasCutShort()
    {
        var huge = new string('x', SummaryPrompt.MaxContentChars + 100);

        var request = SummaryPrompt.Build("big.log", huge)[1].Content;

        request.Should().Contain("showing the first");
    }

    [Fact]
    public void SummaryPrompt_HandlesEmptyContentWithoutThrowing()
    {
        var act = () => SummaryPrompt.Build("empty.txt", string.Empty);

        act.Should().NotThrow();
    }
}
