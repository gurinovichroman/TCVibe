using System.Text;

namespace TCV.Core.Domain;

/// <summary>
/// Byte-level questions every text consumer asks before decoding: is there a byte-order mark, what
/// encoding is this most likely, and is it text at all.
/// </summary>
/// <remarks>
/// Pure and BCL-only, so it can be shared by the internal viewer (<c>TCV.App.Viewer.ViewerContent</c>)
/// and the content search engine (<c>TCV.Search</c>), which is a plugin and cannot see the app.
/// Only encodings built into .NET are produced here; legacy Windows code pages need the
/// <c>System.Text.Encoding.CodePages</c> provider and are therefore chosen explicitly by the caller.
/// </remarks>
public static class TextProbe
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding Utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>UTF-8 without a leading byte-order mark — the default for source files.</summary>
    public static Encoding Utf8 => Utf8NoBom;

    /// <summary>Length of the leading byte-order mark, or 0 when there is none.</summary>
    public static int BomLength(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 0xFF && data[1] == 0xFE && data[2] == 0x00 && data[3] == 0x00) return 4; // UTF-32 LE
        if (data.Length >= 4 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0xFE && data[3] == 0xFF) return 4; // UTF-32 BE
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) return 3;                    // UTF-8
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return 2;                                       // UTF-16 LE
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return 2;                                       // UTF-16 BE
        return 0;
    }

    /// <summary>
    /// Picks the most likely text encoding: a byte-order mark wins, then a NUL-pattern check for
    /// BOM-less UTF-16, then strict UTF-8 validation, then a statistical check for the legacy Russian
    /// code pages (1251, 866, KOI8-R), falling back to Latin-1 (which never throws
    /// and maps every byte 1:1, so the caller always gets something).
    /// </summary>
    public static (Encoding Encoding, int BomLength) DetectEncoding(ReadOnlySpan<byte> data)
    {
        int bom = BomLength(data);
        switch (bom)
        {
            case 4: return (data[0] == 0xFF ? new UTF32Encoding(false, true) : new UTF32Encoding(true, true), 4);
            case 3: return (Utf8NoBom, 3);
            case 2: return (data[0] == 0xFF ? Encoding.Unicode : Encoding.BigEndianUnicode, 2);
        }

        if (LooksLikeBomlessUtf16(data, out bool bigEndian))
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode, 0);

        if (IsValidUtf8(data))
            return (Utf8NoBom, 0);

        // Old Russian text (Windows-1251, DOS CP866, KOI8-R) is not UTF-8 either; recognise it before giving up.
        if (LegacyCyrillic.TryDetect(data) is { } legacy)
        {
            return (legacy, 0);
        }

        return (Encoding.Latin1, 0);
    }

    /// <summary>
    /// Heuristic: a stream that is not UTF-16/32 text but carries a NUL byte, or a high fraction
    /// of non-printable control bytes, is treated as binary.
    /// </summary>
    public static bool LooksBinary(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return false;

        // UTF-16/32 text is full of NULs by design — never binary.
        // (Only the BOM and NUL-pattern checks matter here; the full detection would also decode the
        // buffer with three legacy code pages just to answer a yes/no question.)
        var bom = BomLength(data);
        if (bom is 2 or 4 || (bom == 0 && LooksLikeBomlessUtf16(data, out _)))
        {
            return false;
        }

        int window = Math.Min(data.Length, 8192);
        int suspicious = 0;
        for (int i = 0; i < window; i++)
        {
            byte b = data[i];
            if (b == 0x00) return true;                           // NUL outside UTF-16 ⇒ binary
            if (b < 0x09 || (b > 0x0D && b < 0x20)) suspicious++; // control chars except tab/LF/VT/FF/CR
        }
        return suspicious > window * 0.20;
    }

    /// <summary>
    /// Returns a length ≤ <c>data.Length</c> that ends on a UTF-8 code-point boundary, dropping a
    /// trailing incomplete multi-byte sequence. Used when a buffer was cut at an arbitrary byte cap
    /// so decoding doesn't emit a stray replacement character at the end.
    /// </summary>
    public static int Utf8SafeLength(ReadOnlySpan<byte> data)
    {
        // Walk back over continuation bytes (10xxxxxx) to the lead byte of the last sequence.
        int i = data.Length - 1;
        int continuation = 0;
        while (i >= 0 && (data[i] & 0xC0) == 0x80) { i--; continuation++; }
        if (i < 0) return data.Length;

        byte lead = data[i];
        int expected =
            (lead & 0x80) == 0x00 ? 1 :
            (lead & 0xE0) == 0xC0 ? 2 :
            (lead & 0xF0) == 0xE0 ? 3 :
            (lead & 0xF8) == 0xF0 ? 4 : 1; // a stray lead/invalid byte — leave it to the decoder
        // Complete when the lead byte is followed by exactly the continuation bytes it needs.
        return continuation + 1 >= expected ? data.Length : i;
    }

    /// <summary>Decodes the buffer with the given encoding, skipping any leading BOM.</summary>
    public static string DecodeText(byte[] data, Encoding encoding)
    {
        int start = BomLength(data);
        return encoding.GetString(data, start, data.Length - start);
    }

    private static bool LooksLikeBomlessUtf16(ReadOnlySpan<byte> data, out bool bigEndian)
    {
        bigEndian = false;
        int window = Math.Min(data.Length, 8192);
        if (window < 4) return false;

        int zeroEven = 0, zeroOdd = 0;
        for (int i = 0; i < window; i++)
        {
            if (data[i] != 0x00) continue;
            if ((i & 1) == 0) zeroEven++; else zeroOdd++;
        }

        double half = window / 2.0;
        // ASCII text as UTF-16 LE has its zero high-byte at odd indexes; BE at even indexes.
        if (zeroOdd > half * 0.60 && zeroEven < half * 0.10) { bigEndian = false; return true; }
        if (zeroEven > half * 0.60 && zeroOdd < half * 0.10) { bigEndian = true; return true; }
        return false;
    }

    private static bool IsValidUtf8(ReadOnlySpan<byte> data)
    {
        try
        {
            Utf8Strict.GetCharCount(data);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
