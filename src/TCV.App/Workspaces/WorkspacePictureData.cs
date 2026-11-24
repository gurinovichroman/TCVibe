namespace TCV.App.Workspaces;

/// <summary>
/// A workspace picture as text in a file that leaves this machine: a <c>*.tcvproj</c> or a settings
/// bundle (ADR-0044). Inline rather than a file beside it: it needs no path, so there is no path to
/// lead anywhere. Such a file may come from anywhere (a cloned repository), so nothing is believed
/// before the decoder sees it: a size, a PNG signature and the header's own width and height, which a
/// few bytes can set to gigapixels.
/// </summary>
public static class WorkspacePictureData
{
    /// <summary>
    /// The largest picture that travels. A stored picture is 64×64, 16 KiB of raw pixels, and PNG adds
    /// little even on noise, so whatever the store drew fits; a logo is 2–8 KiB. Above it the picture
    /// stays out of the file and the workspace falls back to its emoji or letters.
    /// </summary>
    public const int MaxBytes = 24 * 1024;

    /// <summary>
    /// All the pictures of a settings bundle together, as text. A bundle is mostly settings to read and
    /// diff: about a hundred logos fit, and the ones past this stay out, which the export says.
    /// </summary>
    public const int MaxBundleChars = 1024 * 1024;

    /// <summary>The base64 length of <see cref="MaxBytes"/>.</summary>
    private const int MaxChars = (MaxBytes + 2) / 3 * 4;

    private const int HeaderLength = 8 + 4 + 4 + 13;   // signature, chunk length, chunk type, IHDR

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> HeaderChunk => [0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R'];

    /// <summary>A stored picture as text; null when it is not one this format carries.</summary>
    public static string? ToText(byte[] png) => IsAcceptable(png) ? Convert.ToBase64String(png) : null;

    /// <summary>The PNG in a file's text; null for anything refused — not base64, too large, not such a PNG.</summary>
    public static byte[]? FromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxChars)
        {
            return null;
        }

        var buffer = new byte[MaxBytes];
        return Convert.TryFromBase64String(text, buffer, out var written) && IsAcceptable(buffer.AsSpan(0, written))
            ? buffer[..written]
            : null;
    }

    /// <summary>Within the size, a PNG, and exactly as wide and high as the store's pictures.</summary>
    public static bool IsAcceptable(ReadOnlySpan<byte> png)
    {
        return png.Length is >= HeaderLength and <= MaxBytes
            && png.StartsWith(Signature)
            && png[Signature.Length..].StartsWith(HeaderChunk)
            && BigEndian(png[16..]) == WorkspaceIconStore.PixelSize
            && BigEndian(png[20..]) == WorkspaceIconStore.PixelSize;

        static uint BigEndian(ReadOnlySpan<byte> bytes) =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }
}
