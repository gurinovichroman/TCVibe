using FluentAssertions;
using TCV.Ai.Rename;

namespace TCV.Ai.Tests;

/// <summary>
/// Turning a model reply into a rename table that is safe to approve. A model asked for file names
/// will return path separators, invented extensions, duplicate names and Windows device names, and
/// every one of those has to be neutralised <em>before</em> a human sees a list they might accept.
/// </summary>
public class AiRenamePlanTests
{
    private static readonly string[] Originals = ["IMG_4821.jpg", "scan001.pdf"];

    [Fact]
    public void Parse_JsonReply_MapsNames()
    {
        const string reply = """
            [ { "from": "IMG_4821.jpg", "to": "2026-03-14-lunch-receipt.jpg" },
              { "from": "scan001.pdf", "to": "2026-01-02-lease-agreement.pdf" } ]
            """;

        var items = AiRenamePlan.Parse(reply, Originals);

        items.Should().HaveCount(2);
        items[0].Proposed.Should().Be("2026-03-14-lunch-receipt.jpg");
        items[1].Proposed.Should().Be("2026-01-02-lease-agreement.pdf");
        items.Should().OnlyContain(i => i.Changed);
    }

    [Fact]
    public void Parse_JsonInsideProseAndFences_StillWorks()
    {
        const string reply = """
            Sure! Here are the names:
            ```json
            [ { "from": "IMG_4821.jpg", "to": "receipt.jpg" } ]
            ```
            """;

        var items = AiRenamePlan.Parse(reply, ["IMG_4821.jpg"]);

        items[0].Proposed.Should().Be("receipt.jpg");
    }

    [Fact]
    public void Parse_ArrowLineFormat_IsAcceptedAsAFallback()
    {
        const string reply = """
            - IMG_4821.jpg => receipt.jpg
            * scan001.pdf => lease.pdf
            """;

        var items = AiRenamePlan.Parse(reply, Originals);

        items[0].Proposed.Should().Be("receipt.jpg");
        items[1].Proposed.Should().Be("lease.pdf");
    }

    [Fact]
    public void Parse_EveryOriginalAppearsExactlyOnce()
    {
        // The table is a complete account of the selection, so a skipped file still gets a row.
        var items = AiRenamePlan.Parse("""[ { "from": "IMG_4821.jpg", "to": "receipt.jpg" } ]""", Originals);

        items.Should().HaveCount(2);
        items.Select(i => i.Original).Should().Equal(Originals);
        items[1].Changed.Should().BeFalse();
        items[1].Note.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_GarbageReply_LeavesEverythingUnchanged()
    {
        var items = AiRenamePlan.Parse("I could not determine the contents.", Originals);

        items.Should().OnlyContain(i => !i.Changed);
    }

    [Fact]
    public void Parse_EmptyReply_LeavesEverythingUnchanged() =>
        AiRenamePlan.Parse(string.Empty, Originals).Should().OnlyContain(i => !i.Changed);

    // ── Sanitisation ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ProposedFolderPath_KeepsOnlyTheLeaf()
    {
        var items = AiRenamePlan.Parse(
            """[ { "from": "scan001.pdf", "to": "2026/invoices/lease.pdf" } ]""", ["scan001.pdf"]);

        items[0].Proposed.Should().Be("lease.pdf");
        items[0].Note.Should().Contain("folder path");
    }

    [Fact]
    public void Parse_InvalidCharacters_AreReplaced()
    {
        var items = AiRenamePlan.Parse(
            """[ { "from": "scan001.pdf", "to": "in<voice>:2026?.pdf" } ]""", ["scan001.pdf"]);

        items[0].Proposed.Should().NotContainAny("<", ">", ":", "?");
        items[0].Proposed.Should().EndWith(".pdf");
    }

    [Fact]
    public void Parse_MissingExtension_RestoresTheOriginalOne()
    {
        var items = AiRenamePlan.Parse(
            """[ { "from": "IMG_4821.jpg", "to": "lunch-receipt" } ]""", ["IMG_4821.jpg"]);

        items[0].Proposed.Should().Be("lunch-receipt.jpg");
        items[0].Note.Should().Contain(".jpg");
    }

    [Fact]
    public void Parse_ChangedExtension_IsPutBack()
    {
        // Changing the extension changes which program opens the file — that is not a rename.
        var items = AiRenamePlan.Parse(
            """[ { "from": "IMG_4821.jpg", "to": "receipt.png" } ]""", ["IMG_4821.jpg"]);

        items[0].Proposed.Should().Be("receipt.jpg");
    }

    [Fact]
    public void Parse_ReservedDeviceName_IsPrefixed()
    {
        var items = AiRenamePlan.Parse(
            """[ { "from": "scan001.pdf", "to": "CON.pdf" } ]""", ["scan001.pdf"]);

        items[0].Proposed.Should().Be("_CON.pdf");
        items[0].Note.Should().Contain("reserved");
    }

    [Fact]
    public void Parse_TrailingDotsAndSpaces_AreTrimmed()
    {
        // Windows silently strips these, which would make the preview lie about the result.
        var items = AiRenamePlan.Parse(
            """[ { "from": "scan001.pdf", "to": "lease.pdf  " } ]""", ["scan001.pdf"]);

        items[0].Proposed.Should().Be("lease.pdf");
    }

    [Fact]
    public void Parse_EmptyProposedName_FallsBackToTheOriginal()
    {
        var items = AiRenamePlan.Parse(
            """[ { "from": "scan001.pdf", "to": "   " } ]""", ["scan001.pdf"]);

        items[0].Changed.Should().BeFalse();
    }

    [Fact]
    public void Parse_VeryLongName_IsShortenedKeepingTheExtension()
    {
        var long_ = new string('a', 400) + ".pdf";
        var items = AiRenamePlan.Parse(
            $$"""[ { "from": "scan001.pdf", "to": "{{long_}}" } ]""", ["scan001.pdf"]);

        items[0].Proposed.Length.Should().BeLessThan(200);
        items[0].Proposed.Should().EndWith(".pdf");
        items[0].Note.Should().Contain("shortened");
    }

    [Fact]
    public void Parse_VeryLongDotPrefixedName_IsShortenedRatherThanThrowing()
    {
        // ".aaa…" is all extension and no stem, and the original has no extension to restore — the
        // shortening used to index a negative length out of an empty stem and take the window with it.
        var original = "notes";
        var proposed = "." + new string('a', 400);

        var items = AiRenamePlan.Parse(
            $$"""[ { "from": "{{original}}", "to": "{{proposed}}" } ]""", [original]);

        items[0].Proposed.Length.Should().BeLessThan(200);
        items[0].Proposed.Should().NotBeEmpty();
    }

    [Fact]
    public void Parse_ExtensionAloneExceedsTheLimit_StillProducesAName()
    {
        var original = "scan001.pdf";
        var proposed = "receipt." + new string('x', 300) + ".pdf";

        var items = AiRenamePlan.Parse(
            $$"""[ { "from": "{{original}}", "to": "{{proposed}}" } ]""", [original]);

        items[0].Proposed.Length.Should().BeLessThan(200);
        items[0].Proposed.Should().NotBeEmpty();
    }

    // ── Collisions ────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_TwoFilesProposedTheSameName_GetsANumericSuffix()
    {
        const string reply = """
            [ { "from": "a.jpg", "to": "receipt.jpg" }, { "from": "b.jpg", "to": "receipt.jpg" } ]
            """;

        var items = AiRenamePlan.Parse(reply, ["a.jpg", "b.jpg"]);

        items[0].Proposed.Should().Be("receipt.jpg");
        items[1].Proposed.Should().Be("receipt (2).jpg");
        items[1].Note.Should().Contain("collision");
    }

    [Fact]
    public void Parse_ProposedNameClashesWithAnUntouchedFile_GetsASuffix()
    {
        // Renaming a.jpg to b.jpg is broken if b.jpg is sitting right there, unchanged.
        var items = AiRenamePlan.Parse(
            """[ { "from": "a.jpg", "to": "b.jpg" } ]""", ["a.jpg", "b.jpg"]);

        items[0].Proposed.Should().Be("b (2).jpg");
    }

    [Fact]
    public void Parse_CaseOnlyChange_CountsAsUnchanged()
    {
        // The prompt asks for lowercase names, so a proposal that differs only in case is the model
        // restyling a name it had nothing to say about. Treating it as a rename would queue a
        // pointless file operation, so it comes back unchanged instead — and, importantly, does not
        // get flagged as colliding with itself.
        var items = AiRenamePlan.Parse(
            """[ { "from": "Receipt.jpg", "to": "receipt.jpg" } ]""", ["Receipt.jpg"]);

        items[0].Changed.Should().BeFalse();
        items[0].Proposed.Should().Be("Receipt.jpg");
        items[0].Note.Should().NotContain("collision");
    }

    [Fact]
    public void Parse_RenameFreesItsOwnNameSoItNeverCollidesWithItself()
    {
        // a.jpg → a-receipt.jpg while a.jpg is in the selection: the original name is being vacated,
        // so nothing should acquire a "(2)" suffix.
        var items = AiRenamePlan.Parse(
            """[ { "from": "a.jpg", "to": "a-receipt.jpg" } ]""", ["a.jpg", "a-receipt-old.jpg"]);

        items[0].Proposed.Should().Be("a-receipt.jpg");
        items[0].Note.Should().NotContain("collision");
    }

    // ── Prompt ────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildPrompt_IncludesNamesAndExcerpts()
    {
        var request = AiRenamePlan.BuildPrompt(
            [("scan001.pdf", "LEASE AGREEMENT dated 2 January 2026")])[1].Content;

        request.Should().Contain("scan001.pdf");
        request.Should().Contain("LEASE AGREEMENT");
    }

    [Fact]
    public void BuildPrompt_SaysWhenAFileHasNoReadableText()
    {
        // Guessing a name for a JPEG from its bytes is not something to pretend at.
        var request = AiRenamePlan.BuildPrompt([("IMG_4821.jpg", string.Empty)])[1].Content;

        request.Should().Contain("no readable text");
    }

    [Fact]
    public void BuildPrompt_CapsTheBatch()
    {
        var many = Enumerable.Range(0, AiRenamePlan.MaxBatch + 20)
            .Select(i => ($"f{i}.pdf", "text")).ToList();

        var request = AiRenamePlan.BuildPrompt(many)[1].Content;

        request.Should().NotContain($"f{AiRenamePlan.MaxBatch + 5}.pdf");
    }
}
