using System.Text;
using FluentAssertions;
using TCV.Search;

namespace TCV.Search.Tests;

/// <summary>Covers the ripgrep-grade behaviour added in task 6.17.</summary>
public class RipgrepSearchTests
{
    private static async Task<List<SearchResult>> RunAsync(
        MemoryVfs vfs, SearchCriteria criteria, string root = "/")
    {
        var results = new List<SearchResult>();
        var engine = new SearchEngine(vfs);
        await foreach (var r in engine.SearchAsync(root, criteria))
            results.Add(r);
        return results;
    }

    // ── .gitignore awareness ──────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_RespectGitignore_SkipsIgnoredFiles()
    {
        var vfs = new MemoryVfs()
            .Add("/.gitignore", "bin/\n*.log\n")
            .Add("/keep.cs", "needle")
            .Add("/app.log", "needle")
            .Add("/bin/generated.cs", "needle");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("keep.cs");
    }

    [Fact]
    public async Task SearchAsync_RespectGitignoreOff_FindsIgnoredFiles()
    {
        var vfs = new MemoryVfs()
            .Add("/.gitignore", "*.log\n")
            .Add("/app.log", "needle");

        var results = await RunAsync(vfs,
            new SearchCriteria { ContentText = "needle", RespectGitignore = false });

        results.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_NestedGitignore_AppliesToItsSubtreeOnly()
    {
        var vfs = new MemoryVfs()
            .Add("/src/.gitignore", "*.tmp\n")
            .Add("/src/a.tmp", "needle")
            .Add("/docs/b.tmp", "needle");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("b.tmp");
    }

    [Fact]
    public async Task SearchAsync_SkipsVcsMetadata_WithoutAnyGitignore()
    {
        var vfs = new MemoryVfs()
            .Add("/.git/config", "needle")
            .Add("/a.cs", "needle");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("a.cs");
    }

    // ── Exclusion masks ───────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_ExcludePatterns_HideMatchingPaths()
    {
        var vfs = new MemoryVfs()
            .Add("/src/app.js", "needle")
            .Add("/src/app.min.js", "needle")
            .Add("/obj/tmp.js", "needle");

        var results = await RunAsync(vfs, new SearchCriteria
        {
            ContentText = "needle",
            ExcludePatterns = "obj/;*.min.js",
        });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("app.js");
    }

    [Fact]
    public async Task SearchAsync_ExcludeNegation_ReAddsOneFile()
    {
        var vfs = new MemoryVfs()
            .Add("/a.min.js", "needle")
            .Add("/keep.min.js", "needle");

        var results = await RunAsync(vfs, new SearchCriteria
        {
            ContentText = "needle",
            ExcludePatterns = "*.min.js;!keep.min.js",
        });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("keep.min.js");
    }

    // ── Name masks ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_SeveralNameMasks_MatchAnyOfThem()
    {
        var vfs = new MemoryVfs()
            .Add("/a.cs").Add("/b.md").Add("/c.txt");

        var results = await RunAsync(vfs, new SearchCriteria { NamePattern = "*.cs;*.md" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("a.cs", "b.md");
    }

    // ── Binary handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_SkipBinary_IgnoresFilesWithNulBytes()
    {
        var vfs = new MemoryVfs()
            .Add("/text.txt", "needle")
            .AddBytes("/blob.bin", [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0x01]);

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("text.txt");
    }

    [Fact]
    public async Task SearchAsync_SkipBinaryOff_GrepsBinaryToo()
    {
        var vfs = new MemoryVfs()
            .AddBytes("/blob.bin", [0x6E, 0x65, 0x65, 0x64, 0x6C, 0x65, 0x00, 0x01]);

        var results = await RunAsync(vfs,
            new SearchCriteria { ContentText = "needle", SkipBinary = false });

        results.Should().ContainSingle();
    }

    // ── Encodings ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task SearchAsync_Utf16File_IsDecodedAutomatically()
    {
        var vfs = new MemoryVfs().Add("/utf16.txt", "привет needle", Encoding.Unicode);

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results.Should().ContainSingle();
        results[0].MatchLineNumber.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_ExplicitCodePage_DecodesLegacyText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var cp1251 = Encoding.GetEncoding(1251);
        var vfs = new MemoryVfs().Add("/legacy.txt", "да", cp1251);

        // Two letters are too little for the statistical detector to commit to a Russian code page, so
        // Auto falls back to Latin-1 and the Cyrillic word does not match — naming the code page fixes it.
        var auto = await RunAsync(vfs, new SearchCriteria { ContentText = "да" });
        var forced = await RunAsync(vfs,
            new SearchCriteria { ContentText = "да", ContentCodePage = 1251 });

        auto.Should().BeEmpty();
        forced.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_AutoEncoding_FindsRussianTextInLegacyCodePages()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "Привет, это обычный русский текст в старой кодировке, без ручного выбора.";
        var vfs = new MemoryVfs()
            .Add("/win1251.txt", text, Encoding.GetEncoding(1251))
            .Add("/dos866.txt", text, Encoding.GetEncoding(866))
            .Add("/koi8r.txt", text, Encoding.GetEncoding(20866));

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "русский" });

        results.Select(r => r.Entry.Name).Should().BeEquivalentTo("win1251.txt", "dos866.txt", "koi8r.txt");
    }

    // ── Whole word, context, per-file cap ─────────────────────────────────────

    [Fact]
    public async Task SearchAsync_WholeWord_SkipsSubstringHits()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "haystack\nstack\n");

        var loose = await RunAsync(vfs, new SearchCriteria { ContentText = "stack" });
        var strict = await RunAsync(vfs, new SearchCriteria { ContentText = "stack", WholeWord = true });

        loose.Should().HaveCount(2);
        strict.Should().ContainSingle();
        strict[0].MatchLineNumber.Should().Be(2);
    }

    [Fact]
    public async Task SearchAsync_ContextLines_AreCapturedOnBothSides()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "one\ntwo\nneedle\nfour\nfive\n");

        var results = await RunAsync(vfs,
            new SearchCriteria { ContentText = "needle", ContextLines = 2 });

        results.Should().ContainSingle();
        results[0].ContextBefore.Should().BeEquivalentTo(["one", "two"], o => o.WithStrictOrdering());
        results[0].ContextAfter.Should().BeEquivalentTo(["four", "five"], o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task SearchAsync_ContextAtFileStart_IsClipped()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "needle\nsecond\n");

        var results = await RunAsync(vfs,
            new SearchCriteria { ContentText = "needle", ContextLines = 3 });

        results[0].ContextBefore.Should().BeEmpty();
        results[0].ContextAfter.Should().BeEquivalentTo(["second"]);
    }

    [Fact]
    public async Task SearchAsync_MaxMatchesPerFile_CapsHitsInOneFile()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "x\nx\nx\nx\n");

        var results = await RunAsync(vfs,
            new SearchCriteria { ContentText = "x", MaxMatchesPerFile = 2 });

        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_MaxResults_StopsTheWalkAndFlagsTruncation()
    {
        var vfs = new MemoryVfs();
        for (var i = 0; i < 20; i++) vfs.Add($"/f{i}.txt", "needle");

        var engine = new SearchEngine(vfs);
        var count = 0;
        await foreach (var _ in engine.SearchAsync("/",
            new SearchCriteria { ContentText = "needle", MaxResults = 5 })) count++;

        count.Should().Be(5);
        engine.WasTruncated.Should().BeTrue();
    }

    [Fact]
    public async Task SearchAsync_ReportsColumnAndLength_ForHighlighting()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "let x = needle;");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results[0].MatchColumn.Should().Be(8);
        results[0].MatchLength.Should().Be(6);
        results[0].MatchLineText.Should().Be("let x = needle;");
    }

    [Fact]
    public async Task SearchAsync_CrlfFile_NumbersLinesLikeAnEditor()
    {
        var vfs = new MemoryVfs().Add("/a.txt", "one\r\ntwo\r\nneedle\r\n");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results[0].MatchLineNumber.Should().Be(3);
        results[0].MatchLineText.Should().Be("needle");
    }

    [Fact]
    public async Task SearchAsync_FileOverContentCap_IsNotOpened()
    {
        var vfs = new MemoryVfs().Add("/big.txt", new string('a', 4096) + "needle");

        var results = await RunAsync(vfs,
            new SearchCriteria { ContentText = "needle", MaxContentBytes = 1024 });

        results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_ContentSearch_DoesNotReportDirectories()
    {
        var vfs = new MemoryVfs().Add("/needle/a.txt", "nothing here");

        var results = await RunAsync(vfs, new SearchCriteria { ContentText = "needle" });

        results.Should().BeEmpty();
    }
}
