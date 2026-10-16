namespace TCV.Ai.Semantic;

/// <summary>A piece of a file, with the lines it came from so a hit can be shown in place.</summary>
/// <param name="StartLine">1-based first line, inclusive.</param>
/// <param name="EndLine">1-based last line, inclusive.</param>
public sealed record TextChunk(int Index, int StartLine, int EndLine, string Text);

/// <summary>
/// Splits a file into embeddable chunks on line boundaries, with a small overlap.
/// <para>
/// Line boundaries rather than a fixed byte window: an embedding of a fragment cut mid-statement is
/// noticeably worse, and a hit has to be reportable as "file:line" to be worth anything in a file
/// manager. The overlap exists because the thing being searched for is often exactly what a naive
/// split would cut in half — a function signature and the comment above it.
/// </para>
/// </summary>
public static class TextChunker
{
    /// <summary>Target chunk size. Around 300–400 tokens for code, which suits every small embedding model.</summary>
    public const int DefaultChunkChars = 1_400;

    /// <summary>Characters repeated from the previous chunk.</summary>
    public const int DefaultOverlapChars = 200;

    /// <summary>Chunks shorter than this carry no meaning worth a vector (a brace, a blank tail).</summary>
    public const int MinChunkChars = 40;

    public static IReadOnlyList<TextChunk> Split(
        string? text, int chunkChars = DefaultChunkChars, int overlapChars = DefaultOverlapChars)
    {
        var chunks = new List<TextChunk>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;

        chunkChars = Math.Max(MinChunkChars * 2, chunkChars);
        overlapChars = Math.Clamp(overlapChars, 0, chunkChars / 2);

        var lines = text.Replace("\r\n", "\n").Split('\n');

        var current = new List<string>();
        var currentChars = 0;
        var startLine = 1;
        var index = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            current.Add(lines[i]);
            currentChars += lines[i].Length + 1;

            if (currentChars < chunkChars) continue;

            var endLine = i + 1;
            Emit(chunks, ref index, startLine, endLine, current);

            // Carry back whole lines worth roughly `overlapChars`, so the next chunk starts with the
            // tail of this one instead of a hard cut.
            var carried = TakeTail(current, overlapChars);
            current = carried.Lines;
            currentChars = carried.Chars;
            startLine = endLine - carried.Lines.Count + 1;
        }

        if (current.Count > 0)
            Emit(chunks, ref index, startLine, lines.Length, current);

        return chunks;
    }

    private static void Emit(
        List<TextChunk> chunks, ref int index, int startLine, int endLine, List<string> lines)
    {
        var body = string.Join("\n", lines).Trim();
        if (body.Length < MinChunkChars) return;

        chunks.Add(new TextChunk(index++, startLine, endLine, body));
    }

    private static (List<string> Lines, int Chars) TakeTail(List<string> lines, int budget)
    {
        var tail = new List<string>();
        var chars = 0;

        for (int i = lines.Count - 1; i >= 0 && chars < budget; i--)
        {
            tail.Insert(0, lines[i]);
            chars += lines[i].Length + 1;
        }

        return (tail, chars);
    }

    /// <summary>
    /// Cosine similarity, for callers that hold vectors in memory (the tests, and any future
    /// fallback path). The stored index does this inside sqlite-vec, which is the whole reason for
    /// taking that dependency.
    /// </summary>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length == 0 || a.Length != b.Length) return 0;

        double dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        if (normA == 0 || normB == 0) return 0;
        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}
