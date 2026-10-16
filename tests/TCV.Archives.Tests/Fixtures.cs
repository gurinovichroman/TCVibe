using System.Diagnostics;
using System.Text;

namespace TCV.Archives.Tests;

/// <summary>
/// Builders for the archive formats the engines can only read, so tests can carry real content
/// without checking binary fixtures into the repository.
/// </summary>
internal static class Fixtures
{
    private const int SectorSize = 2048;

    // ── ISO 9660 ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a small ISO 9660 image: <c>README.TXT</c> and <c>DOCS/GUIDE.TXT</c>, both with a
    /// <c>;1</c> version suffix like a real mastered disc.
    /// </summary>
    public static string CreateIso(string directory, string readmeText, string guideText)
    {
        var readme = Encoding.ASCII.GetBytes(readmeText);
        var guide = Encoding.ASCII.GetBytes(guideText);

        const int rootLba = 18, docsLba = 19, readmeLba = 20, guideLba = 21;
        var data = new byte[24 * SectorSize];

        var pvd = 16 * SectorSize;
        data[pvd] = 0x01;
        Encoding.ASCII.GetBytes("CD001").CopyTo(data, pvd + 1);
        data[pvd + 6] = 0x01;
        var rootRecord = DirectoryRecord(".", rootLba, SectorSize, isDirectory: true, isSelf: true);
        rootRecord.CopyTo(data, pvd + 156);
        data[17 * SectorSize] = 0xFF;

        var root = new List<byte>();
        root.AddRange(DirectoryRecord(".", rootLba, SectorSize, true, isSelf: true));
        root.AddRange(DirectoryRecord("..", rootLba, SectorSize, true, isParent: true));
        root.AddRange(DirectoryRecord("DOCS", docsLba, SectorSize, true));
        root.AddRange(DirectoryRecord("README.TXT;1", readmeLba, readme.Length, false));
        root.ToArray().CopyTo(data, rootLba * SectorSize);

        var docs = new List<byte>();
        docs.AddRange(DirectoryRecord(".", docsLba, SectorSize, true, isSelf: true));
        docs.AddRange(DirectoryRecord("..", rootLba, SectorSize, true, isParent: true));
        docs.AddRange(DirectoryRecord("GUIDE.TXT;1", guideLba, guide.Length, false));
        docs.ToArray().CopyTo(data, docsLba * SectorSize);

        readme.CopyTo(data, readmeLba * SectorSize);
        guide.CopyTo(data, guideLba * SectorSize);

        var path = Path.Combine(directory, $"fixture_{Guid.NewGuid():N}.iso");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static byte[] DirectoryRecord(
        string name, int lba, int size, bool isDirectory, bool isSelf = false, bool isParent = false)
    {
        var nameBytes = isSelf ? new byte[] { 0 } : isParent ? new byte[] { 1 } : Encoding.ASCII.GetBytes(name);
        var length = 33 + nameBytes.Length;
        if (length % 2 != 0)
        {
            length++;
        }

        var record = new byte[length];
        record[0] = (byte)length;
        WriteBothEndian(record, 2, (uint)lba);
        WriteBothEndian(record, 10, (uint)size);
        record[18] = 126; // 2026 - 1900
        record[19] = 1;
        record[20] = 15;
        record[25] = (byte)(isDirectory ? 0x02 : 0x00);
        record[28] = 1;
        record[32] = (byte)nameBytes.Length;
        nameBytes.CopyTo(record, 33);
        return record;
    }

    private static void WriteBothEndian(byte[] buffer, int offset, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            buffer[offset + i] = (byte)(value >> (8 * i));
            buffer[offset + 4 + i] = (byte)(value >> (8 * (3 - i)));
        }
    }

    // ── CAB ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an uncompressed one-folder cabinet. Names may contain backslashes
    /// (<c>sub\b.txt</c>) — the way a real cabinet stores a directory.
    /// </summary>
    public static string CreateCab(string directory, params (string name, string content)[] files)
    {
        var bodies = files.Select(f => Encoding.ASCII.GetBytes(f.content)).ToArray();
        var totalData = bodies.Sum(b => b.Length);

        var fileRecords = new List<byte>();
        var offset = 0;
        for (var i = 0; i < files.Length; i++)
        {
            fileRecords.AddRange(BitConverter.GetBytes((uint)bodies[i].Length));
            fileRecords.AddRange(BitConverter.GetBytes((uint)offset));
            fileRecords.AddRange(BitConverter.GetBytes((ushort)0));     // folder index
            fileRecords.AddRange(BitConverter.GetBytes((ushort)0x5A21)); // 2026-09-01
            fileRecords.AddRange(BitConverter.GetBytes((ushort)0x6000)); // 12:00:00
            fileRecords.AddRange(BitConverter.GetBytes((ushort)0));     // attributes
            fileRecords.AddRange(Encoding.ASCII.GetBytes(files[i].name));
            fileRecords.Add(0);
            offset += bodies[i].Length;
        }

        const int headerSize = 36, folderSize = 8;
        var coffFiles = headerSize + folderSize;
        var coffData = coffFiles + fileRecords.Count;
        var dataBlockSize = 8 + totalData;
        var total = coffData + dataBlockSize;

        var cab = new List<byte>();
        cab.AddRange(Encoding.ASCII.GetBytes("MSCF"));
        cab.AddRange(BitConverter.GetBytes(0u));
        cab.AddRange(BitConverter.GetBytes((uint)total));
        cab.AddRange(BitConverter.GetBytes(0u));
        cab.AddRange(BitConverter.GetBytes((uint)coffFiles));
        cab.AddRange(BitConverter.GetBytes(0u));
        cab.Add(3);
        cab.Add(1);
        cab.AddRange(BitConverter.GetBytes((ushort)1));
        cab.AddRange(BitConverter.GetBytes((ushort)files.Length));
        cab.AddRange(BitConverter.GetBytes((ushort)0));
        cab.AddRange(BitConverter.GetBytes((ushort)1234));
        cab.AddRange(BitConverter.GetBytes((ushort)0));

        cab.AddRange(BitConverter.GetBytes((uint)coffData));
        cab.AddRange(BitConverter.GetBytes((ushort)1));
        cab.AddRange(BitConverter.GetBytes((ushort)0)); // stored, no compression

        cab.AddRange(fileRecords);

        cab.AddRange(BitConverter.GetBytes(0u));                 // no checksum
        cab.AddRange(BitConverter.GetBytes((ushort)totalData));
        cab.AddRange(BitConverter.GetBytes((ushort)totalData));
        foreach (var body in bodies)
        {
            cab.AddRange(body);
        }

        var path = Path.Combine(directory, $"fixture_{Guid.NewGuid():N}.cab");
        File.WriteAllBytes(path, cab.ToArray());
        return path;
    }

    // ── 7-Zip (only when it is installed) ────────────────────────────────────

    /// <summary>Path of 7z.exe, or null on a machine without 7-Zip; such tests return early.</summary>
    public static string? SevenZip { get; } = FindSevenZip();

    private static string? FindSevenZip()
    {
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 })
        {
            var candidate = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Runs 7z.exe with the given arguments in <paramref name="workingDirectory"/>.</summary>
    public static void RunSevenZip(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo(SevenZip!)
        {
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode > 1)
        {
            throw new InvalidOperationException($"7z.exe failed with exit code {proc.ExitCode}");
        }
    }
}
