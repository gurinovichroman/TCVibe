using System.Text;
using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// Read-only CAB (Cabinet) archive engine.
/// Uses the Windows Cabinet API (cabinet.dll FDI — File Decompression Interface)
/// via P/Invoke for extraction, and parses CFHEADER for listing.
/// </summary>
public sealed class CabEngine : IArchiveEngine
{
    public bool CanWrite => false;

    // CAB file signature
    private const uint CabSignature = 0x4643534D; // "MSCF" as a little-endian uint32

    public IReadOnlyList<ArchiveEntry> ListAll(string archivePath)
    {
        var builder = new EntryListBuilder();
        using var stream = File.OpenRead(archivePath);

        if (!IsCabFile(stream))
        {
            return builder.Build();
        }

        stream.Seek(0, SeekOrigin.Begin);
        ParseCabHeader(stream, builder);
        return builder.Build();
    }

    public Stream ExtractEntry(string archivePath, string entryPath)
    {
        // Expand into a private temp folder and hand back a buffer. Only the wanted file is
        // extracted; a cabinet whose names expand.exe will not match is extracted whole as a fallback.
        var tempDir = Path.Combine(Path.GetTempPath(), "TCV_CAB_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var relative = EntryListBuilder.NormalizePath(entryPath);
            var target = ArchivePathSafety.ResolveDestination(tempDir, relative);

            try
            {
                RunExpand(["/F:" + relative.Replace('/', (char)92), Path.GetFullPath(archivePath), tempDir], CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Fall through to the whole-cabinet extraction below.
            }

            if (!File.Exists(target))
            {
                RunExpand(["/F:*", Path.GetFullPath(archivePath), tempDir], CancellationToken.None);
            }

            if (!File.Exists(target))
            {
                throw new FileNotFoundException($"Entry not found in CAB: {entryPath}");
            }

            return EntryBuffer.Fill(new FileInfo(target).Length, buffer =>
            {
                using var source = File.OpenRead(target);
                source.CopyTo(buffer);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (IOException)
            {
                // Temp folder: swept by the OS eventually.
            }
        }
    }

    public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files)
        => throw new NotSupportedException("CAB write is not supported.");

    public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files)
        => throw new NotSupportedException("Adding to CAB archives is not supported.");

    public void DeleteEntry(string archivePath, string entryPath)
        => throw new NotSupportedException("Deleting entries from CAB archives is not supported.");

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) =>
        ExtractAll(archivePath, destDir, progress, CancellationToken.None);

    public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        // Names are validated up front: expand.exe writes wherever the cabinet says.
        foreach (var entry in ListAll(archivePath))
        {
            ArchivePathSafety.ResolveDestination(destDir, entry.FullPath);
        }

        RunExpand(["/F:*", Path.GetFullPath(archivePath), Path.GetFullPath(destDir)], ct);
        progress?.Report(1.0);
    }

    // ──────────────────────────────────────────────────────────────────────
    // CAB header parser — reads CFHEADER + CFFILE structures

    private static void ParseCabHeader(Stream stream, EntryListBuilder builder)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        // CFHEADER
        uint sig = reader.ReadUInt32();
        if (sig != CabSignature) return;

        reader.ReadUInt32(); // reserved1
        uint cabinetSize = reader.ReadUInt32();
        reader.ReadUInt32(); // reserved2
        uint firstFileOffset = reader.ReadUInt32();
        reader.ReadUInt32(); // reserved3
        byte versionMinor = reader.ReadByte();
        byte versionMajor = reader.ReadByte();
        ushort folderCount = reader.ReadUInt16();
        ushort fileCount = reader.ReadUInt16();
        ushort flags = reader.ReadUInt16();
        reader.ReadUInt16(); // setId
        reader.ReadUInt16(); // cabinetIndex

        // Optional fields based on flags
        if ((flags & 0x0001) != 0) { reader.ReadUInt32(); reader.ReadUInt16(); } // prev cabinet
        if ((flags & 0x0002) != 0) { reader.ReadUInt32(); reader.ReadUInt16(); } // next cabinet
        bool hasReserve = (flags & 0x0004) != 0;
        if (hasReserve)
        {
            ushort cbCFHeader = reader.ReadUInt16();
            reader.ReadByte(); // cbCFFolder
            reader.ReadByte(); // cbCFData
            reader.ReadBytes(cbCFHeader); // abReserve
        }

        // Seek to first CFFILE
        stream.Seek(firstFileOffset, SeekOrigin.Begin);

        for (int i = 0; i < fileCount; i++)
        {
            uint uncompressedSize = reader.ReadUInt32();
            uint uncompressedOffset = reader.ReadUInt32();
            ushort folderIndex = reader.ReadUInt16();
            ushort date = reader.ReadUInt16();
            ushort time = reader.ReadUInt16();
            ushort attribs = reader.ReadUInt16();

            // File name — null-terminated, ASCII or UTF-8
            var nameBytes = new List<byte>();
            byte b;
            while ((b = reader.ReadByte()) != 0)
                nameBytes.Add(b);

            var name = ((attribs & 0x80) != 0)
                ? Encoding.UTF8.GetString(nameBytes.ToArray())
                : Encoding.ASCII.GetString(nameBytes.ToArray());

            var dt = ParseDosDateTime(date, time);
            builder.AddFile(name, uncompressedSize, dt);
        }
    }

    private static bool IsCabFile(Stream stream)
    {
        Span<byte> magic = stackalloc byte[4];
        if (stream.Read(magic) < 4) return false;
        return magic[0] == 'M' && magic[1] == 'S' && magic[2] == 'C' && magic[3] == 'F';
    }

    private static DateTime ParseDosDateTime(ushort date, ushort time)
    {
        try
        {
            int year = ((date >> 9) & 0x7F) + 1980;
            int month = (date >> 5) & 0x0F;
            int day = date & 0x1F;
            int hour = (time >> 11) & 0x1F;
            int minute = (time >> 5) & 0x3F;
            int second = (time & 0x1F) * 2;
            var clampedMonth = Math.Clamp(month, 1, 12);
            return new DateTime(year, clampedMonth, Math.Clamp(day, 1, DateTime.DaysInMonth(year, clampedMonth)),
                Math.Clamp(hour, 0, 23), Math.Clamp(minute, 0, 59), Math.Clamp(second, 0, 59));
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // Extraction via expand.exe (ships with Windows)

    /// <summary>Upper bound for one expand.exe run; a hung process is killed rather than waited on.</summary>
    private static readonly TimeSpan ExpandTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Runs expand.exe. Both output streams are drained while it runs — leaving stdout unread
    /// deadlocks it as soon as the pipe buffer fills, which a cabinet of a few dozen files does.
    /// The process is killed on cancellation or timeout.
    /// </summary>
    private static void RunExpand(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "expand.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        using var proc = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("Cannot start expand.exe");
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();

        using var registration = ct.Register(() => Kill(proc));
        if (!proc.WaitForExit(ExpandTimeout))
        {
            Kill(proc);
            throw new TimeoutException("expand.exe did not finish in time and was stopped.");
        }

        ct.ThrowIfCancellationRequested();
        proc.WaitForExit();
        var output = (stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()).Trim();

        if (proc.ExitCode != 0)
        {
            throw new InvalidOperationException($"expand.exe failed (exit {proc.ExitCode}): {output}");
        }
    }

    private static void Kill(System.Diagnostics.Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
