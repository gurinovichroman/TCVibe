using FluentAssertions;
using TCV.Git;

namespace TCV.Git.Tests;

public class GitDiffLinesTests
{
    private const string Sample =
        "diff --git a/f.txt b/f.txt\n" +
        "index 111..222 100644\n" +
        "--- a/f.txt\n" +
        "+++ b/f.txt\n" +
        "@@ -1,2 +1,2 @@\n" +
        " keep\n" +
        "-old\n" +
        "+new\n";

    [Fact]
    public void Parse_UnifiedDiff_ClassifiesEveryLine()
    {
        var kinds = GitDiffLines.Parse(Sample).Select(l => l.Kind).ToArray();

        kinds.Should().Equal(
            DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header, DiffLineKind.Header,
            DiffLineKind.Hunk, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added);
    }

    [Fact]
    public void Parse_RemovedLineThatLooksLikeAFileHeader_IsStillRemoved()
    {
        // A removed "-- sql comment" is "--- sql comment" in the diff; inside a hunk it is content.
        var diff = "diff --git a/q.sql b/q.sql\n--- a/q.sql\n+++ b/q.sql\n@@ -1 +1 @@\n--- sql comment\n+++ added\n";

        var lines = GitDiffLines.Parse(diff);

        lines[^2].Kind.Should().Be(DiffLineKind.Removed);
        lines[^1].Kind.Should().Be(DiffLineKind.Added);
    }

    [Fact]
    public void Parse_CrLfEndings_AreTrimmed()
    {
        var lines = GitDiffLines.Parse("@@ -1 +1 @@\r\n+x\r\n");

        lines.Select(l => l.Text).Should().Equal("@@ -1 +1 @@", "+x");
    }

    [Fact]
    public void Parse_EmptyOrNull_GivesNoLines()
    {
        GitDiffLines.Parse(null).Should().BeEmpty();
        GitDiffLines.Parse(string.Empty).Should().BeEmpty();
    }
}
