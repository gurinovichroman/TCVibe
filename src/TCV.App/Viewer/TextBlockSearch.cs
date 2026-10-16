using System.Text;

namespace TCV.App.Viewer;

/// <summary>
/// A stretch of a file a block search settled on: a window read from <see cref="Start"/> shows all of
/// it, and the match lies before <see cref="End"/>.
/// </summary>
public readonly record struct TextBlock(long Start, long End);

/// <summary>
/// Where find next settled: a window read from <see cref="Start"/> shows the match, which ends past
/// <see cref="Seen"/>. The text before <see cref="Seen"/> was searched already, so the search in the window
/// goes on from there rather than from its top.
/// </summary>
public readonly record struct WindowMatch(long Start, long Seen);

/// <summary>
/// Find next and find previous over a file bigger than the viewer's window (6.41): reads the file a block
/// at a time, the nearest block first, until one holds the text. Pure (an <see cref="IViewerSource"/>, no
/// window), so the viewer runs it off the UI thread (INV-06) and the block edges are tested on their own.
/// </summary>
/// <remarks>
/// <para>
/// Blocks are cut the way the viewer pages. Forward, each block is the window paging would show next: it
/// starts on the line the edge of the one before cut through. Backward, a block's text starts on a whole
/// line, and the line its first byte cut through is searched whole with the block before it.
/// </para>
/// <para>
/// A block with no line break to cut at (one line longer than a block, as in a minified bundle) overlaps
/// its neighbour by as many bytes as the query can take, so a match across the edge is seen whole. The
/// search starts the same way at the window on screen, and counts only a match that is not wholly in
/// text already searched: one that ends past the end the window showed (forward) or starts before its
/// start (backward). So a match across the window's own edge is found, and one already on screen is not
/// found a second time. Each block's encoding is judged on a sample cut to a character boundary (TRAP-33)
/// unless one was chosen by hand.
/// </para>
/// </remarks>
public static class TextBlockSearch
{
    /// <summary>
    /// The first window from <paramref name="start"/> on whose text holds <paramref name="query"/>
    /// (ignoring case) ending past <paramref name="seen"/>, or null when the rest of the file has none. Start
    /// where <see cref="After"/> says. <paramref name="length"/> is where the file ends as the viewer knows
    /// it, <paramref name="encoding"/> null means "judge each window", and <paramref name="progress"/>
    /// receives the offset being read.
    /// </summary>
    public static async Task<WindowMatch?> FindForwardAsync(
        IViewerSource source, long start, long seen, long length, string query, Encoding? encoding, int windowBytes,
        IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowBytes, MinBlockBytes);
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        while (start < length)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(start);

            var bytes = await source.ReadAsync(start, windowBytes, null, ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;   // the file shrank under the viewer
            }

            var windowEncoding = encoding ?? ViewerContent.DetectWindowEncoding(bytes);
            var atEnd = start + bytes.Length >= length;
            var shown = atEnd ? bytes : ViewerContent.TrimIncompleteTail(bytes, windowEncoding);
            if (EndsPast(shown, start, seen, windowEncoding, query))
            {
                return new WindowMatch(start, seen);
            }

            if (atEnd)
            {
                return null;
            }

            (start, seen) = Step(bytes, start, windowEncoding, windowBytes, Overlap(query, windowEncoding));
        }

        return null;
    }

    /// <summary>
    /// Where find next goes on after <paramref name="window"/> (read at <paramref name="windowOffset"/>,
    /// shown in <paramref name="encoding"/>): the start of the first window to read, and the end of the
    /// text this one showed, which a match must reach past.
    /// </summary>
    public static (long Start, long Seen) After(byte[] window, long windowOffset, Encoding encoding, int windowBytes, string query) =>
        Step(window, windowOffset, encoding, windowBytes, Overlap(query, encoding));

    /// <summary>
    /// Where the window after <paramref name="window"/> (read at <paramref name="windowOffset"/>) begins:
    /// at the start of the line its edge cut through, so that line is shown whole next time instead of its
    /// rest being lost between the two. A window with no line break goes on after its last whole character.
    /// </summary>
    public static long NextWindowStart(byte[] window, long windowOffset, Encoding encoding, int windowBytes) =>
        Step(window, windowOffset, encoding, windowBytes, overlap: 0).Start;

    /// <summary>
    /// The nearest block before <paramref name="end"/> holding <paramref name="query"/> (ignoring case)
    /// that starts before <paramref name="end"/>, or null when nothing does. <paramref name="encoding"/>
    /// null means "judge each block". <paramref name="progress"/> receives the offset being read.
    /// </summary>
    public static async Task<TextBlock?> FindBackwardAsync(
        IViewerSource source, long end, string query, Encoding? encoding, int blockBytes,
        IProgress<long>? progress, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(blockBytes, MinBlockBytes);
        if (string.IsNullOrEmpty(query))
        {
            return null;
        }

        // What follows `seen` was searched already. The first block reaches past it by as much as the query
        // can take in any encoding, so a match across it is seen whole.
        var seen = Math.Min(end, source.Length);
        end = Math.Min(source.Length, seen + Overlap(query, Encoding.UTF32));
        while (end > 0)
        {
            ct.ThrowIfCancellationRequested();

            // 4-aligned so a UTF-16/32 block starts on a code unit whatever the encoding turns out to be;
            // rounded up, so a block is never longer than a window and a window read from its start holds it.
            var start = Math.Max(0, end - blockBytes);
            start += (4 - start % 4) % 4;
            progress?.Report(start);

            var bytes = await source.ReadAsync(start, (int)(end - start), null, ct).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;   // the file shrank under the viewer
            }

            var blockEncoding = encoding ?? ViewerContent.DetectWindowEncoding(bytes);
            var (textStart, previousEnd) = Cut(bytes, start, blockEncoding, Overlap(query, blockEncoding));

            var text = ViewerContent.TrimIncompleteTail(bytes[textStart..], blockEncoding);
            if (StartsBefore(text, start + textStart, seen, blockEncoding, query))
            {
                return new TextBlock(start + textStart, start + bytes.Length);
            }

            end = previousEnd;
        }

        return null;
    }

    /// <summary>
    /// Splits a block read at <paramref name="start"/>: where its text begins (an index into
    /// <paramref name="bytes"/>) and where the block before it is to end. The text begins past the first
    /// line break — the line the block's first byte cut through goes whole into the block before, which
    /// ends there. With no line break to cut at, the text begins on the first character boundary and the
    /// block before reaches <paramref name="overlap"/> bytes into this one, so a match across the cut is
    /// still seen whole.
    /// </summary>
    public static (int TextStart, long PreviousEnd) Cut(byte[] bytes, long start, Encoding encoding, int overlap)
    {
        if (start == 0)
        {
            return (0, 0);   // the start of the file: nothing before it
        }

        var lineStart = ViewerContent.FirstLineStart(bytes, encoding, bytes.Length);
        if (lineStart > 0 && lineStart < bytes.Length)
        {
            return (lineStart, start + lineStart);
        }

        var characterStart = ViewerContent.LeadingPartialCharacter(bytes, encoding);
        var previousEnd = start + characterStart + overlap;

        // The block before must end before this one does, or the search would stand still.
        return (characterStart, previousEnd < start + bytes.Length ? previousEnd : start);
    }

    /// <summary>Smallest block the search reads; real searches read a whole window at a time.</summary>
    public const int MinBlockBytes = 16;

    /// <summary>
    /// The window after <paramref name="window"/>, the mirror of <see cref="Cut"/>: it starts on the line the
    /// edge cut through. With no line break to cut at, it starts <paramref name="overlap"/> bytes before the
    /// edge, on a character boundary, so a match across the edge is seen whole (paging passes 0: no
    /// overlap). <c>Seen</c> is where the text this window showed ends.
    /// </summary>
    private static (long Start, long Seen) Step(byte[] window, long windowOffset, Encoding encoding, int windowBytes, int overlap)
    {
        var shown = window.Length <= windowBytes ? window : window[..windowBytes];
        var whole = ViewerContent.TrimIncompleteTail(shown, encoding).Length;
        var seen = windowOffset + whole;

        var lastLine = ViewerContent.LastLineStart(shown, encoding);
        if (lastLine > 0)
        {
            return (windowOffset + lastLine, seen);
        }

        var back = whole - overlap;
        if (overlap > 0 && back > 0)
        {
            back += ViewerContent.LeadingPartialCharacter(shown[back..], encoding);
            if (back < whole)
            {
                return (windowOffset + back, seen);
            }
        }

        return (windowOffset + Math.Max(1, whole), seen);
    }

    /// <summary>True when the text of <paramref name="shown"/> (read at <paramref name="offset"/>) holds a match ending past <paramref name="seen"/>.</summary>
    private static bool EndsPast(byte[] shown, long offset, long seen, Encoding encoding, string query)
    {
        var text = ViewerContent.DecodeText(shown, encoding);
        var seenChars = CharsBefore(shown, offset, seen, encoding, text);
        return text.IndexOf(query, Math.Max(0, seenChars - query.Length + 1), StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>True when the text of <paramref name="shown"/> (read at <paramref name="offset"/>) holds a match starting before <paramref name="seen"/>.</summary>
    private static bool StartsBefore(byte[] shown, long offset, long seen, Encoding encoding, string query)
    {
        var text = ViewerContent.DecodeText(shown, encoding);

        // LastIndexOf from `last` sees only matches lying wholly at or before it: those starting before `seen`.
        var last = Math.Min(text.Length - 1, CharsBefore(shown, offset, seen, encoding, text) + query.Length - 2);
        return last >= 0 && text.LastIndexOf(query, last, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>How many characters of <paramref name="text"/> (decoded from <paramref name="shown"/>, read at
    /// <paramref name="offset"/>) lie before the byte offset <paramref name="seen"/>, a character boundary.</summary>
    private static int CharsBefore(byte[] shown, long offset, long seen, Encoding encoding, string text)
    {
        var before = seen - offset;
        if (before <= 0)
        {
            return 0;
        }

        return before >= shown.Length ? text.Length : ViewerContent.DecodeText(shown[..(int)before], encoding).Length;
    }

    /// <summary>The most bytes <paramref name="query"/> can take in <paramref name="encoding"/>, in whole UTF-32 units.</summary>
    private static int Overlap(string query, Encoding encoding) => (encoding.GetMaxByteCount(query.Length) + 3) & ~3;
}
