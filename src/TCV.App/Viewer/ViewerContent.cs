using System.Text;
using TCV.Core.Domain;

namespace TCV.App.Viewer;

/// <summary>The rendering modes offered by the internal file viewer (F3 Lister).</summary>
public enum ViewerMode
{
    /// <summary>Decoded text with a selectable encoding.</summary>
    Text,
    /// <summary>Offset / hex bytes / ASCII gutter dump for binary files.</summary>
    Hex,
    /// <summary>Decoded bitmap (PNG/JPEG/GIF/BMP/TIFF/ICO).</summary>
    Image,
    /// <summary>Source with syntax highlighting and a line-number gutter, or a coloured diff (6.18).</summary>
    Code,
    /// <summary>Rendered markdown: headings, lists, quotes, code blocks and links (6.18).</summary>
    Markdown,
}

/// <summary>
/// Pure, UI-free helpers behind the internal file viewer: text/binary/image classification and
/// hex-dump formatting, plus the byte-order-mark and encoding detection it shares with the search
/// engine through <see cref="TextProbe"/>.
/// <para>
/// Kept free of WPF and any file-system dependency on purpose — the viewer reads bytes from
/// whatever <c>IVirtualFileSystem</c> the panel is showing (local disk, archive, FTP), so the
/// logic here operates purely on the resulting <see cref="byte"/> buffer and is unit-testable.
/// </para>
/// </summary>
public static class ViewerContent
{
    static ViewerContent()
    {
        // Windows code pages (1251/1252/866/KOI8-R…) are not built into modern .NET; the viewer
        // offers them for legacy text, so register the provider before any GetEncoding call.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>Resolves a Windows code page, having ensured the provider is registered.</summary>
    public static Encoding GetCodePage(int codePage) => Encoding.GetEncoding(codePage);

    /// <summary>UTF-8 without a leading byte-order mark — the viewer's ASCII/UTF-8 default.</summary>
    public static Encoding Utf8 => TextProbe.Utf8;

    /// <summary>Length of the leading byte-order mark, or 0 when there is none.</summary>
    public static int BomLength(byte[] data) => TextProbe.BomLength(data);

    /// <summary>
    /// Picks the most likely text encoding: a byte-order mark wins, then a NUL-pattern check for
    /// BOM-less UTF-16, then strict UTF-8 validation, falling back to Latin-1 (which never throws
    /// and maps every byte 1:1, so the user always sees something).
    /// </summary>
    public static (Encoding Encoding, int BomLength) DetectEncoding(byte[] data) =>
        TextProbe.DetectEncoding(data);

    /// <summary>
    /// Encoding of a window (or a search block) read at an arbitrary offset, judged on a sample cut back
    /// to a character boundary (TRAP-33). Judging the raw buffer meant a window that ended mid-character
    /// failed strict UTF-8 and turned a whole Russian or Chinese file into Latin-1 mojibake; the sample
    /// is also bounded, so a 32 MB buffer is not scanned end to end. A window that <em>begins</em>
    /// mid-character fails the same way, so when the sample is not UTF at all, it is judged once more
    /// without the continuation bytes it starts with.
    /// </summary>
    public static Encoding DetectWindowEncoding(byte[] window)
    {
        var sample = window.Length > DetectionSampleBytes ? window[..DetectionSampleBytes] : window;

        var safe = sample[..Utf8SafeLength(sample)];
        var encoding = DetectEncoding(safe).Encoding;
        if (encoding is UTF8Encoding or UnicodeEncoding or UTF32Encoding)
        {
            return encoding;
        }

        // Only as a second try: dropping a byte from UTF-16 would put it on the wrong half of its code units.
        var lead = LeadingPartialCharacter(safe, Utf8);
        return lead > 0 && DetectEncoding(safe[lead..]).Encoding is UTF8Encoding utf8 ? utf8 : encoding;
    }

    private const int DetectionSampleBytes = 1024 * 1024;

    /// <summary>
    /// Heuristic: a stream that is not UTF-16/32 text but carries a NUL byte, or a high fraction
    /// of non-printable control bytes, is treated as binary and shown as a hex dump.
    /// </summary>
    public static bool LooksBinary(byte[] data) => TextProbe.LooksBinary(data);

    /// <summary>True when the bytes carry a known image magic number or the name has an image extension.</summary>
    public static bool IsImage(string fileName, byte[] data) => HasImageMagic(data) || IsImageExtension(fileName);

    /// <summary>Chooses the mode the viewer opens in: image, hex for binary, otherwise text.</summary>
    public static ViewerMode ClassifyDefault(string fileName, byte[] data)
    {
        if (IsImage(fileName, data)) return ViewerMode.Image;
        if (LooksBinary(data)) return ViewerMode.Hex;
        return ViewerMode.Text;
    }

    /// <summary>
    /// Returns a length ≤ <c>data.Length</c> that ends on a UTF-8 code-point boundary, dropping a
    /// trailing incomplete multi-byte sequence. Used when a buffer was cut at an arbitrary byte cap
    /// so decoding doesn't emit a stray replacement character at the end.
    /// </summary>
    public static int Utf8SafeLength(byte[] data) => TextProbe.Utf8SafeLength(data);

    /// <summary>Decodes the buffer to a string with the given encoding, skipping any leading BOM.</summary>
    public static string DecodeText(byte[] data, Encoding encoding) =>
        TextProbe.DecodeText(data, encoding);

    /// <summary>
    /// Drops a trailing partial character of a window that was cut at an arbitrary byte, for the given
    /// encoding: an incomplete UTF-8 sequence, or an odd byte of UTF-16 / a partial UTF-32 unit. Decoding
    /// the cut buffer would otherwise end in a replacement character (TRAP-33).
    /// </summary>
    public static byte[] TrimIncompleteTail(byte[] data, Encoding encoding)
    {
        var keep = encoding switch
        {
            UTF8Encoding => TextProbe.Utf8SafeLength(data),
            UnicodeEncoding => data.Length & ~1,
            UTF32Encoding => data.Length & ~3,
            _ => data.Length,
        };
        return keep == data.Length ? data : data[..keep];
    }

    /// <summary>
    /// Number of leading bytes of a window that started at an arbitrary offset to skip so it begins on a
    /// character boundary: the continuation bytes of a UTF-8 character that began before the window.
    /// </summary>
    public static int LeadingPartialCharacter(byte[] data, Encoding encoding)
    {
        if (encoding is not UTF8Encoding)
        {
            return 0;
        }

        var skip = 0;
        while (skip < 3 && skip < data.Length && (data[skip] & 0xC0) == 0x80)
        {
            skip++;
        }

        return skip;
    }

    /// <summary>
    /// Just past the first line feed within <paramref name="limit"/> bytes — where the first whole line of
    /// a window starts. Zero when there is none, so a file without line breaks is shown from its start.
    /// The line feed is looked for in <paramref name="encoding"/>'s own code units: a lone 0x0A byte in
    /// UTF-16LE is half of <c>0A 00</c>, and starting after it put the window on an odd byte (garbage).
    /// </summary>
    public static int FirstLineStart(byte[] data, Encoding encoding, int limit = 4096)
    {
        var lf = LineFeed(encoding);
        var end = Math.Min(data.Length, limit) - lf.Length;
        for (var i = 0; i <= end; i += lf.Length)
        {
            if (data.AsSpan(i, lf.Length).SequenceEqual(lf))
            {
                return i + lf.Length;
            }
        }

        return 0;
    }

    /// <summary>
    /// Just past the last line feed in <paramref name="data"/> (in <paramref name="encoding"/>'s code
    /// units): where the line cut by the end of a window starts. Zero when the window has no line break.
    /// </summary>
    public static int LastLineStart(byte[] data, Encoding encoding)
    {
        var lf = LineFeed(encoding);
        for (var i = (data.Length - lf.Length) / lf.Length * lf.Length; i >= 0; i -= lf.Length)
        {
            if (data.AsSpan(i, lf.Length).SequenceEqual(lf))
            {
                return i + lf.Length;
            }
        }

        return 0;
    }

    /// <summary>"\n" as <paramref name="encoding"/> writes it: one byte, or a whole UTF-16/32 code unit.</summary>
    private static byte[] LineFeed(Encoding encoding)
    {
        var lf = encoding.GetBytes("\n");
        return lf.Length == 0 ? [10] : lf;
    }

    /// <summary>
    /// Formats a classic hex dump: 8-digit offset, sixteen hex bytes (split 8+8) and an ASCII
    /// gutter. <paramref name="baseOffset"/> lets the caller show real offsets when it passes a slice.
    /// </summary>
    public static string ToHexDump(byte[] data, long baseOffset = 0)
    {
        const int perRow = 16;
        // 8 offset + 2 gap + (16*3 + 1) hex + 1 gap + 16 ascii + newline ≈ 77 chars per row
        var sb = new StringBuilder(data.Length / perRow * 78 + 16);
        for (int off = 0; off < data.Length; off += perRow)
        {
            sb.Append((baseOffset + off).ToString("X8")).Append("  ");
            int rowLen = Math.Min(perRow, data.Length - off);
            for (int i = 0; i < perRow; i++)
            {
                if (i == 8) sb.Append(' ');
                if (i < rowLen) sb.Append(data[off + i].ToString("X2")).Append(' ');
                else sb.Append("   ");
            }
            sb.Append(' ');
            for (int i = 0; i < rowLen; i++)
            {
                byte b = data[off + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static bool HasImageMagic(byte[] d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true; // PNG
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return true;                  // JPEG
        if (d.Length >= 6 && d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46 && d[3] == 0x38) return true;   // GIF8
        if (d.Length >= 2 && d[0] == 0x42 && d[1] == 0x4D) return true;                                   // BMP
        if (d.Length >= 4 && d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A && d[3] == 0x00) return true;   // TIFF LE
        if (d.Length >= 4 && d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x00 && d[3] == 0x2A) return true;   // TIFF BE
        if (d.Length >= 4 && d[0] == 0x00 && d[1] == 0x00 && d[2] == 0x01 && d[3] == 0x00) return true;   // ICO
        return false;
    }

    /// <summary>True when the file name has an image extension the viewer can decode.</summary>
    public static bool HasImageExtension(string fileName) => IsImageExtension(fileName);

    private static bool IsImageExtension(string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".jfif" or ".gif" or ".bmp"
            or ".ico" or ".tif" or ".tiff" or ".wmp";
    }
}
