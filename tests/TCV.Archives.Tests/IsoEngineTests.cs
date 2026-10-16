using System.IO;
using FluentAssertions;
using TCV.Archives.Engines;

namespace TCV.Archives.Tests;

public class IsoEngineTests
{
    private static string CreateMinimalIso()
    {
        // Build a minimal ISO 9660 image in memory:
        // Sector 0..15: padding
        // Sector 16: Primary Volume Descriptor
        // Sector 17: Volume Descriptor Set Terminator
        // Sector 18: Root directory record (empty)
        const int sectorSize = 2048;
        const int totalSectors = 20;
        var data = new byte[totalSectors * sectorSize];

        // ── PVD at sector 16 ──────────────────────────────────────────────
        int pvdOffset = 16 * sectorSize;
        data[pvdOffset + 0] = 0x01;           // Type: PVD
        data[pvdOffset + 1] = (byte)'C';
        data[pvdOffset + 2] = (byte)'D';
        data[pvdOffset + 3] = (byte)'0';
        data[pvdOffset + 4] = (byte)'0';
        data[pvdOffset + 5] = (byte)'1';      // Identifier: CD001
        data[pvdOffset + 6] = 0x01;           // Version

        // Root directory location: sector 18, size 34 (one minimal record)
        uint rootLba = 18;
        uint rootSize = 34;
        WriteUInt32LE(data, pvdOffset + 158, rootLba);
        WriteUInt32LE(data, pvdOffset + 162, rootLba);   // BE copy (ignored by our parser)
        WriteUInt32LE(data, pvdOffset + 166, rootSize);
        WriteUInt32LE(data, pvdOffset + 170, rootSize);  // BE copy

        // ── Volume Descriptor Set Terminator at sector 17 ─────────────────
        data[17 * sectorSize] = 0xFF;

        // ── Root directory at sector 18: two minimal records (. and ..) ───
        int dirOffset = 18 * sectorSize;
        // "." record
        data[dirOffset + 0] = 34;             // record length
        data[dirOffset + 25] = 0x02;          // flags: directory
        WriteUInt32LE(data, dirOffset + 2, rootLba);
        WriteUInt32LE(data, dirOffset + 10, rootSize);
        data[dirOffset + 32] = 1;
        data[dirOffset + 33] = 0;             // name byte = 0x00 → "."

        // ".." record
        int d2 = dirOffset + 34;
        data[d2 + 0] = 34;
        data[d2 + 25] = 0x02;
        WriteUInt32LE(data, d2 + 2, rootLba);
        WriteUInt32LE(data, d2 + 10, rootSize);
        data[d2 + 32] = 1;
        data[d2 + 33] = 1;                    // name byte = 0x01 → ".."

        var path = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static void WriteUInt32LE(byte[] buf, int offset, uint value)
    {
        buf[offset + 0] = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    [Fact]
    public void ListAll_EmptyIso_ReturnsEmptyList()
    {
        var iso = CreateMinimalIso();
        try
        {
            var engine = new IsoEngine();
            var entries = engine.ListAll(iso);
            entries.Should().BeEmpty("the root directory contains only . and .. entries");
        }
        finally
        {
            File.Delete(iso);
        }
    }

    [Fact]
    public void IsoEngine_CanWrite_IsFalse()
    {
        new IsoEngine().CanWrite.Should().BeFalse();
    }

    [Fact]
    public void IsoEngine_Pack_ThrowsNotSupported()
    {
        var engine = new IsoEngine();
        var act = () => engine.Pack("dummy.iso", Enumerable.Empty<(string, string)>());
        act.Should().Throw<NotSupportedException>();
    }
}
