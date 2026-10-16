using System.IO;
using System.Text;
using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// Read-only ISO 9660 archive engine.
/// Parses the ISO 9660 Primary Volume Descriptor and directory records
/// directly from the raw disc image — no native library required.
/// Supports: .iso, .img (ISO 9660 layout).
/// Does NOT support Joliet or UDF extended volumes (falls back to 8.3 names for those).
/// </summary>
public sealed class IsoEngine : IArchiveEngine
{
    private const int SectorSize = 2048;
    private const int PvdSector = 16;       // Primary Volume Descriptor
    private const int PvdType = 1;          // PVD type code
    private const int ElToritoType = 0;     // Boot record descriptor

    public bool CanWrite => false;

    // ──────────────────────────────────────────────────────────────────────
    // IArchiveEngine

    public IReadOnlyList<ArchiveEntry> ListAll(string archivePath)
    {
        using var stream = File.OpenRead(archivePath);
        var pvd = ReadPvd(stream);
        var entries = new List<ArchiveEntry>();
        ReadDirectory(stream, pvd.RootDirLba, pvd.RootDirSize, string.Empty, entries);
        return entries;
    }

    public Stream ExtractEntry(string archivePath, string entryPath)
    {
        using var stream = File.OpenRead(archivePath);
        var pvd = ReadPvd(stream);

        var entry = FindEntry(stream, pvd, entryPath);
        if (entry is null)
            throw new FileNotFoundException($"Entry not found in ISO: {entryPath}");

        // Copy the file data into a seekable buffer so the caller can seek freely
        return EntryBuffer.Fill(entry.Value.Size, buffer =>
        {
            stream.Seek((long)entry.Value.Lba * SectorSize, SeekOrigin.Begin);
            CopyRange(stream, buffer, entry.Value.Size);
        });
    }

    private static void CopyRange(Stream source, Stream destination, long count)
    {
        var chunk = new byte[81920];
        while (count > 0)
        {
            int read = source.Read(chunk, 0, (int)Math.Min(chunk.Length, count));
            if (read <= 0) throw new EndOfStreamException("Unexpected end of ISO image.");
            destination.Write(chunk, 0, read);
            count -= read;
        }
    }

    public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files)
        => throw new NotSupportedException("ISO write is not supported.");

    public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files)
        => throw new NotSupportedException("Adding to ISO archives is not supported.");

    public void DeleteEntry(string archivePath, string entryPath)
        => throw new NotSupportedException("Deleting entries from ISO archives is not supported.");

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) =>
        ExtractAll(archivePath, destDir, progress, CancellationToken.None);

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        using var stream = File.OpenRead(archivePath);
        var pvd = ReadPvd(stream);
        var entries = new List<ArchiveEntry>();
        ReadDirectory(stream, pvd.RootDirLba, pvd.RootDirSize, string.Empty, entries);

        var files = entries.Where(e => !e.IsDirectory).ToList();
        foreach (var dir in entries.Where(e => e.IsDirectory))
        {
            Directory.CreateDirectory(ArchivePathSafety.ResolveDestination(destDir, dir.FullPath));
        }

        for (int i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = files[i];
            var destPath = ArchivePathSafety.ResolveDestination(destDir, entry.FullPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            // Straight from the image into the file — no per-entry re-open, no in-memory copy.
            var raw = FindEntry(stream, pvd, entry.FullPath)
                ?? throw new FileNotFoundException($"Entry not found in ISO: {entry.FullPath}");
            stream.Seek((long)raw.Lba * SectorSize, SeekOrigin.Begin);
            using var dst = File.Create(destPath);
            CopyRange(stream, dst, raw.Size);

            progress?.Report((double)(i + 1) / files.Count);
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // ISO 9660 internals

    private static PvdInfo ReadPvd(Stream stream)
    {
        Span<byte> sector = stackalloc byte[SectorSize];

        for (int s = PvdSector; s < PvdSector + 16; s++)
        {
            stream.Seek((long)s * SectorSize, SeekOrigin.Begin);
            stream.ReadExactly(sector);

            byte type = sector[0];
            if (type == 0xFF) break; // Volume Descriptor Set Terminator

            if (type != PvdType) continue; // Not the Primary Volume Descriptor

            // Verify identifier CD001
            if (sector[1] != 'C' || sector[2] != 'D' || sector[3] != '0' ||
                sector[4] != '0' || sector[5] != '1') continue;

            uint rootLba = ReadUInt32LE(sector, 158);
            uint rootSize = ReadUInt32LE(sector, 166);

            return new PvdInfo(rootLba, rootSize);
        }

        throw new InvalidDataException("ISO 9660 Primary Volume Descriptor not found.");
    }

    private static void ReadDirectory(
        Stream stream,
        uint dirLba,
        uint dirSize,
        string parentPath,
        List<ArchiveEntry> result)
    {
        var data = new byte[dirSize];
        stream.Seek((long)dirLba * SectorSize, SeekOrigin.Begin);
        stream.ReadExactly(data, 0, (int)dirSize);

        int offset = 0;
        while (offset < (int)dirSize)
        {
            byte recordLength = data[offset];
            if (recordLength == 0)
            {
                // Skip to next sector boundary
                int nextSector = (offset / SectorSize + 1) * SectorSize;
                offset = nextSector;
                continue;
            }

            if (offset + recordLength > (int)dirSize) break;

            byte flags = data[offset + 25];
            bool isDir = (flags & 0x02) != 0;

            byte nameLen = data[offset + 32];
            if (nameLen == 0) { offset += recordLength; continue; }

            // Skip . and .. entries (nameLen==1 && name==0 or 1)
            if (nameLen == 1 && (data[offset + 33] == 0 || data[offset + 33] == 1))
            {
                offset += recordLength;
                continue;
            }

            uint lba = ReadUInt32LE(data.AsSpan(offset + 2));
            uint size = ReadUInt32LE(data.AsSpan(offset + 10));

            // Strip version suffix (;1)
            var rawName = Encoding.ASCII.GetString(data, offset + 33, nameLen);
            int versionSep = rawName.IndexOf(';');
            var name = versionSep > 0 ? rawName[..versionSep] : rawName;
            name = name.TrimEnd('.');

            // Slash-free root, like every other engine: the panel matches parents by string.
            var fullPath = parentPath.Length > 0
                ? parentPath + "/" + name
                : name;

            var dt = ReadDateTime(data.AsSpan(offset + 18));

            result.Add(new ArchiveEntry(
                FullPath: fullPath,
                Name: name,
                IsDirectory: isDir,
                Size: isDir ? 0 : (long)size,
                LastModified: dt));

            if (isDir)
                ReadDirectory(stream, lba, size, fullPath, result);

            offset += recordLength;
        }
    }

    private IsoRawEntry? FindEntry(Stream stream, PvdInfo pvd, string entryPath)
    {
        // Normalize path separator
        var parts = entryPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return FindInDir(stream, pvd.RootDirLba, pvd.RootDirSize, parts, 0);
    }

    private static IsoRawEntry? FindInDir(
        Stream stream, uint dirLba, uint dirSize, string[] parts, int depth)
    {
        var data = new byte[dirSize];
        stream.Seek((long)dirLba * SectorSize, SeekOrigin.Begin);
        stream.ReadExactly(data, 0, (int)dirSize);

        int offset = 0;
        while (offset < (int)dirSize)
        {
            byte recordLength = data[offset];
            if (recordLength == 0)
            {
                int nextSector = (offset / SectorSize + 1) * SectorSize;
                offset = nextSector;
                continue;
            }
            if (offset + recordLength > (int)dirSize) break;

            byte nameLen = data[offset + 32];
            if (nameLen == 1 && (data[offset + 33] == 0 || data[offset + 33] == 1))
            {
                offset += recordLength;
                continue;
            }

            uint lba = ReadUInt32LE(data.AsSpan(offset + 2));
            uint size = ReadUInt32LE(data.AsSpan(offset + 10));
            byte flags = data[offset + 25];
            bool isDir = (flags & 0x02) != 0;

            var rawName = Encoding.ASCII.GetString(data, offset + 33, nameLen);
            int versionSep = rawName.IndexOf(';');
            var name = versionSep > 0 ? rawName[..versionSep] : rawName;
            name = name.TrimEnd('.');

            if (string.Equals(name, parts[depth], StringComparison.OrdinalIgnoreCase))
            {
                if (depth == parts.Length - 1)
                    return new IsoRawEntry(lba, size);

                if (isDir)
                    return FindInDir(stream, lba, size, parts, depth + 1);
            }

            offset += recordLength;
        }

        return null;
    }

    private static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset = 0) =>
        (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static DateTime ReadDateTime(ReadOnlySpan<byte> d)
    {
        try
        {
            int year = 1900 + d[0];
            int month = d[1];
            int day = d[2];
            int hour = d[3];
            int minute = d[4];
            int second = d[5];
            var clampedMonth = Math.Clamp(month, 1, 12);
            return new DateTime(year, clampedMonth,
                Math.Clamp(day, 1, DateTime.DaysInMonth(year, clampedMonth)),
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59),
                DateTimeKind.Local);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private readonly record struct PvdInfo(uint RootDirLba, uint RootDirSize);
    private readonly record struct IsoRawEntry(uint Lba, uint Size);
}
