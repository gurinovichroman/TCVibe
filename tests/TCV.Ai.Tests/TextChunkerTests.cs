using FluentAssertions;
using TCV.Ai.Semantic;

namespace TCV.Ai.Tests;

/// <summary>
/// Chunking for embeddings. Line numbers matter as much as the split itself: a semantic hit that
/// cannot be reported as "file:line" is not useful in a file manager.
/// </summary>
public class TextChunkerTests
{
    private static string Lines(int count, int width = 60) =>
        string.Join("\n", Enumerable.Range(1, count).Select(i => $"line {i} " + new string('x', width)));

    [Fact]
    public void Split_ShortText_IsOneChunkCoveringEveryLine()
    {
        var chunks = TextChunker.Split(Lines(5));

        chunks.Should().HaveCount(1);
        chunks[0].StartLine.Should().Be(1);
        chunks[0].EndLine.Should().Be(5);
        chunks[0].Index.Should().Be(0);
    }

    [Fact]
    public void Split_LongText_ProducesSeveralChunksWithRisingIndices()
    {
        var chunks = TextChunker.Split(Lines(400));

        chunks.Should().HaveCountGreaterThan(1);
        chunks.Select(c => c.Index).Should().BeInAscendingOrder();
        chunks.Select(c => c.Index).Should().Equal(Enumerable.Range(0, chunks.Count));
    }

    [Fact]
    public void Split_ChunksCoverTheWholeFileAndStayInOrder()
    {
        var chunks = TextChunker.Split(Lines(300));

        chunks[0].StartLine.Should().Be(1);
        chunks[^1].EndLine.Should().Be(300);

        for (int i = 1; i < chunks.Count; i++)
            chunks[i].StartLine.Should().BeGreaterThan(chunks[i - 1].StartLine);
    }

    [Fact]
    public void Split_ConsecutiveChunksOverlap()
    {
        // The thing being searched for is often exactly what a naive split would cut in half.
        var chunks = TextChunker.Split(Lines(300));

        chunks.Should().HaveCountGreaterThan(1);
        chunks[1].StartLine.Should().BeLessThanOrEqualTo(chunks[0].EndLine);
    }

    [Fact]
    public void Split_BreaksOnLineBoundaries()
    {
        var chunks = TextChunker.Split(Lines(200));

        foreach (var chunk in chunks)
        {
            chunk.Text.Should().NotStartWith(" ");
            chunk.Text.Split('\n').Should().OnlyContain(line => line.StartsWith("line ") || line.Length == 0);
        }
    }

    [Fact]
    public void Split_WithoutOverlap_StillCoversEveryLine()
    {
        var chunks = TextChunker.Split(Lines(200), overlapChars: 0);

        chunks[0].StartLine.Should().Be(1);
        chunks[^1].EndLine.Should().Be(200);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n \n ")]
    public void Split_NothingToChunk_IsEmpty(string? text) =>
        TextChunker.Split(text).Should().BeEmpty();

    [Fact]
    public void Split_TinyContent_IsDroppedAsMeaningless()
    {
        // A brace or a blank tail carries no meaning worth a vector.
        TextChunker.Split("}").Should().BeEmpty();
    }

    [Fact]
    public void Split_HandlesCrLfTheSameAsLf()
    {
        var lf = TextChunker.Split(Lines(120));
        var crlf = TextChunker.Split(Lines(120).Replace("\n", "\r\n"));

        crlf.Count.Should().Be(lf.Count);
        crlf[0].EndLine.Should().Be(lf[0].EndLine);
    }

    // ── Cosine ────────────────────────────────────────────────────────────────

    [Fact]
    public void Cosine_IdenticalVectors_IsOne() =>
        TextChunker.Cosine([1f, 2f, 3f], [1f, 2f, 3f]).Should().BeApproximately(1.0, 1e-6);

    [Fact]
    public void Cosine_OrthogonalVectors_IsZero() =>
        TextChunker.Cosine([1f, 0f], [0f, 1f]).Should().BeApproximately(0.0, 1e-6);

    [Fact]
    public void Cosine_OppositeVectors_IsMinusOne() =>
        TextChunker.Cosine([1f, 0f], [-1f, 0f]).Should().BeApproximately(-1.0, 1e-6);

    [Fact]
    public void Cosine_ScaleInvariant() =>
        TextChunker.Cosine([1f, 2f], [10f, 20f]).Should().BeApproximately(1.0, 1e-6);

    [Fact]
    public void Cosine_MismatchedOrEmpty_IsZero()
    {
        TextChunker.Cosine([1f, 2f], [1f]).Should().Be(0);
        TextChunker.Cosine([], []).Should().Be(0);
        TextChunker.Cosine([0f, 0f], [1f, 1f]).Should().Be(0);
    }
}
