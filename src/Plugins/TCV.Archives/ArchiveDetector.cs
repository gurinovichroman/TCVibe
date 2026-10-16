using TCV.Archives.Engines;

namespace TCV.Archives;

/// <summary>Detects archive format from magic bytes and file extension.</summary>
public static class ArchiveDetector
{
    private static readonly byte[] ZipMagic = [0x50, 0x4B, 0x03, 0x04];   // PK..
    private static readonly byte[] RarMagic = [0x52, 0x61, 0x72, 0x21];   // Rar!
    private static readonly byte[] SevenZMagic = [0x37, 0x7A, 0xBC, 0xAF]; // 7z..
    private static readonly byte[] GzMagic = [0x1F, 0x8B];                 // GZ
    private static readonly byte[] Bz2Magic = [0x42, 0x5A, 0x68];          // BZh
    private static readonly byte[] TarMagic = [0x75, 0x73, 0x74, 0x61, 0x72]; // ustar (offset 257)
    private static readonly byte[] CabMagic = [0x4D, 0x53, 0x43, 0x46];    // MSCF
    // ISO 9660: sector 16 contains Primary Volume Descriptor starting with 0x01 + "CD001"
    private static readonly byte[] IsoCd001 = [0x43, 0x44, 0x30, 0x30, 0x31]; // CD001

    public enum ArchiveFormat { Zip, SevenZip, Rar, Tar, Iso, Cab, Unknown }

    public static ArchiveFormat DetectFormat(string filePath)
    {
        try
        {
            Span<byte> header = stackalloc byte[262];
            using var fs = File.OpenRead(filePath);
            int read = fs.Read(header);
            if (read < 4) return ArchiveFormat.Unknown;

            if (StartsWith(header, ZipMagic)) return ArchiveFormat.Zip;
            if (StartsWith(header, SevenZMagic)) return ArchiveFormat.SevenZip;
            if (StartsWith(header, RarMagic)) return ArchiveFormat.Rar;
            if (StartsWith(header, GzMagic)) return ArchiveFormat.Tar;
            if (StartsWith(header, Bz2Magic)) return ArchiveFormat.Tar;
            if (StartsWith(header, CabMagic)) return ArchiveFormat.Cab;

            // TAR: magic "ustar" at offset 257
            if (read >= 262 && StartsWith(header.Slice(257), TarMagic)) return ArchiveFormat.Tar;

            // ISO 9660: PVD at sector 16 (byte 32768); too large to read in header buffer
            // — fall back to extension detection for ISO
        }
        catch { /* fall through to extension-based detection */ }

        return DetectByExtension(filePath);
    }

    public static ArchiveFormat DetectByExtension(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".zip" or ".jar" or ".nupkg" or ".xpi" => ArchiveFormat.Zip,
            ".7z" => ArchiveFormat.SevenZip,
            ".rar" or ".r00" => ArchiveFormat.Rar,
            ".tar" or ".gz" or ".tgz" or ".bz2" or ".tbz2" or ".xz" or ".txz" => ArchiveFormat.Tar,
            ".iso" or ".img" => ArchiveFormat.Iso,
            ".cab" => ArchiveFormat.Cab,
            ".exe" => DetectSfx(filePath), // SFX check
            _ => ArchiveFormat.Unknown,
        };
    }

    /// <summary>Check if an .exe is a self-extracting archive (SFX).</summary>
    private static ArchiveFormat DetectSfx(string filePath)
    {
        try
        {
            Span<byte> buf = stackalloc byte[64];
            using var fs = File.OpenRead(filePath);
            // Search for 7z SFX or ZIP SFX magic within the first 512KB
            var bigBuf = new byte[524288];
            int read = fs.Read(bigBuf, 0, bigBuf.Length);
            var span = bigBuf.AsSpan(0, read);

            // 7z SFX: contains "7z\xBC\xAF\x27\x1C"
            byte[] sevenZSfx = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
            if (IndexOf(span, sevenZSfx) >= 0) return ArchiveFormat.SevenZip;

            // RAR SFX: contains "Rar!" magic
            byte[] rarSfx = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07];
            if (IndexOf(span, rarSfx) >= 0) return ArchiveFormat.Rar;

            // ZIP SFX: contains PK\x03\x04 magic
            if (IndexOf(span, ZipMagic) >= 0) return ArchiveFormat.Zip;
        }
        catch { }
        return ArchiveFormat.Unknown;
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    public static IArchiveEngine CreateEngine(string filePath, string? password = null)
    {
        return DetectFormat(filePath) switch
        {
            ArchiveFormat.Zip => new ZipEngine(),
            ArchiveFormat.SevenZip => new SevenZipEngine(password),
            ArchiveFormat.Rar => new RarEngine(),
            ArchiveFormat.Tar => new TarEngine(),
            ArchiveFormat.Iso => new IsoEngine(),
            ArchiveFormat.Cab => new CabEngine(),
            _ => throw new NotSupportedException($"Unsupported archive format: {Path.GetExtension(filePath)}"),
        };
    }

    public static bool IsArchive(string filePath)
        => DetectFormat(filePath) != ArchiveFormat.Unknown;

    private static bool StartsWith(ReadOnlySpan<byte> data, byte[] pattern)
    {
        if (data.Length < pattern.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (data[i] != pattern[i]) return false;
        return true;
    }
}
