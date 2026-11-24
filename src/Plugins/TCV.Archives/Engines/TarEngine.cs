using System.Formats.Tar;
using System.IO.Compression;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;
using SharpCompress.Writers;
using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// TAR/GZ/BZ2 engine using SharpCompress. A TAR can be created but not modified in place, so
/// <see cref="CanWrite"/> is false: the UI must not offer "add to archive" or "delete entry" for it.
/// </summary>
/// <remarks>
/// A TAR, compressed or not, is read with <see cref="TarReader"/> from the BCL. SharpCompress 0.48 shows
/// PAX headers as files (<c>PaxHeaders.N/.</c>, the default of .NET's <c>TarFile</c>; <c>pax_global_header</c>
/// of a <c>git archive</c>), ignores the long name they carry, and over a compressed stream sees only the
/// first entry (TRAP-99). What is not a TAR inside (a lone <c>notes.txt.gz</c>) stays with SharpCompress.
/// <para>
/// <see cref="IArchiveEngine"/> is listed again on purpose: the base class maps the cancellable
/// <c>Pack</c> overload to the interface's default body, which ignores the token, so without the
/// re-implementation Cancel during a TAR pack did nothing until the whole archive was written.
/// </para>
/// </remarks>
public sealed class TarEngine : SharpCompressEngineBase, IArchiveEngine
{
    public TarEngine() : base(null)
    {
    }

    /// <summary>
    /// Only what is no TAR comes this way (<see cref="HoldsTar"/>). A lone <c>.gz</c> is opened as just that:
    /// the factory looks inside by itself, and took a file that starts with zeros for an empty TAR.
    /// </summary>
    protected override IArchive OpenArchive(Stream stream, SharpCompress.Readers.ReaderOptions options)
    {
        Span<byte> magic = stackalloc byte[2];
        var read = stream.Read(magic);
        stream.Position = 0;
        return read == magic.Length && magic.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B])
            ? SharpCompress.Archives.GZip.GZipArchive.OpenArchive(stream, options)
            : ArchiveFactory.OpenArchive(stream, options);
    }

    public override IReadOnlyList<ArchiveEntry> ListAll(string archivePath)
    {
        if (!HoldsTar(archivePath))
        {
            if (NamelessGzipEntry(archivePath) is not { } lone)
            {
                return base.ListAll(archivePath);
            }

            var single = new EntryListBuilder();
            single.AddFile(lone, GzipStatedLength(archivePath), File.GetLastWriteTime(archivePath));
            return single.Build();
        }

        var builder = new EntryListBuilder();
        Walk(archivePath, (entry, _) =>
        {
            var modified = entry.ModificationTime.LocalDateTime;
            if (IsFolder(entry))
            {
                builder.AddDirectory(entry.Name, modified);
            }
            else
            {
                builder.AddFile(entry.Name, entry.Length, modified);
            }

            return true;
        }, CancellationToken.None);
        return builder.Build();
    }

    public override Stream ExtractEntry(string archivePath, string entryPath)
    {
        if (!HoldsTar(archivePath))
        {
            if (NamelessGzipEntry(archivePath) is not { } lone)
            {
                return base.ExtractEntry(archivePath, entryPath);
            }

            if (new EntryRequests([entryPath], [lone]).Claim(lone) is null)
            {
                throw new FileNotFoundException($"Entry '{entryPath}' not found in archive.");
            }

            // Unknown length (-1), so a large one goes to a file: what the trailer says is modulo 4 GiB.
            return EntryBuffer.Fill(-1, target => CopyGzip(archivePath, target));
        }

        Stream? found = null;
        WalkRequested(archivePath, [entryPath], (_, entry) =>
        {
            found = EntryBuffer.Fill(entry.Length, (entry.DataStream ?? Stream.Null).CopyTo);
        }, CancellationToken.None);
        return found ?? throw new FileNotFoundException($"Entry '{entryPath}' not found in archive.");
    }

    public override void ExtractAll(string archivePath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        if (!HoldsTar(archivePath))
        {
            if (NamelessGzipEntry(archivePath) is not { } lone)
            {
                base.ExtractAll(archivePath, destDir, progress, ct);
                return;
            }

            ct.ThrowIfCancellationRequested();
            WriteFile(ArchivePathSafety.ResolveDestination(destDir, lone), target => CopyGzip(archivePath, target));
            progress?.Report(1d);
            return;
        }

        Directory.CreateDirectory(destDir);
        Walk(archivePath, (entry, file) =>
        {
            // "./", which `tar -C dir -czf x.tgz .` writes first, is the destination itself: no name to resolve.
            if (EntryListBuilder.NormalizePath(entry.Name).Length == 0)
            {
                return true;
            }

            // ResolveDestination throws on a zip-slip name before that entry is written.
            var dest = ArchivePathSafety.ResolveDestination(destDir, entry.Name);
            if (IsFolder(entry))
            {
                Directory.CreateDirectory(dest);
            }
            else
            {
                WriteFile(dest, target => (entry.DataStream ?? Stream.Null).CopyTo(target));

                // How far into the file: counting the entries first would decompress all of it twice, for a bar.
                progress?.Report(file.Length == 0 ? 1d : (double)file.Position / file.Length);
            }

            return true;
        }, ct);
        progress?.Report(1d);
    }

    /// <summary>One pass for the batch, as the base's; a second only for what no entry of that exact name answered.</summary>
    public override void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume,
        CancellationToken ct)
    {
        if (!HoldsTar(archivePath))
        {
            if (NamelessGzipEntry(archivePath) is not { } lone)
            {
                base.ReadEntries(archivePath, entryPaths, consume, ct);
                return;
            }

            ct.ThrowIfCancellationRequested();
            if (new EntryRequests(entryPaths, [lone]).Claim(lone) is { } requested)
            {
                using var file = OpenFile(archivePath);
                using var content = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
                consume(requested, content);
            }

            return;
        }

        WalkRequested(archivePath, entryPaths, (requested, entry) => consume(requested, entry.DataStream ?? Stream.Null), ct);
    }

    /// <summary>
    /// Exact names first, in a pass that stops at the last of them: the paths come from the listing, so that
    /// is nearly always all of them, and a compressed TAR has to be decompressed to be walked at all. What is
    /// left goes through a pass that ignores case, which is how an exact name beats another case.
    /// </summary>
    private static void WalkRequested(string archivePath, IReadOnlyList<string> entryPaths, Action<string, TarEntry> consume,
        CancellationToken ct)
    {
        var unanswered = new HashSet<string>(entryPaths, StringComparer.Ordinal);
        void Answer(EntryRequests requests)
        {
            Walk(archivePath, (entry, _) =>
            {
                if (!IsFolder(entry) && requests.Claim(entry.Name) is { } requested)
                {
                    unanswered.Remove(requested);
                    consume(requested, entry);
                }

                return requests.Remaining > 0;
            }, ct);
        }

        Answer(new EntryRequests(entryPaths, entryPaths));
        if (unanswered.Count > 0)
        {
            Answer(new EntryRequests([.. unanswered], []));
        }
    }

    /// <summary>
    /// Goes through the entries in stored order until <paramref name="visit"/> says enough; it also gets the
    /// file, whose position says how far the pass has come. An entry's data is valid during the call. PAX
    /// headers and GNU long names are the reader's: they arrive applied to the entry they describe. A global
    /// PAX header describes no entry, and is not one.
    /// </summary>
    private static void Walk(string archivePath, Func<TarEntry, Stream, bool> visit, CancellationToken ct)
    {
        using var file = OpenFile(archivePath);
        using var content = Decompressed(file);
        using var reader = new TarReader(content, leaveOpen: true);
        while (reader.GetNextEntry() is { } entry)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.EntryType != TarEntryType.GlobalExtendedAttributes && !visit(entry, file))
            {
                return;
            }
        }
    }

    private static bool IsFolder(TarEntry entry) =>
        entry.EntryType == TarEntryType.Directory || entry.Name.EndsWith('/') || entry.Name.EndsWith((char)92);

    private static FileStream OpenFile(string archivePath) =>
        new(archivePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// The bytes of the TAR: decompressed by what the file starts with, not by its extension. The caller
    /// keeps the file and closes it: <see cref="XZStream"/> would not, and it stayed open until the
    /// collector came by (TRAP-99).
    /// </summary>
    private static Stream Decompressed(FileStream file)
    {
        Span<byte> magic = stackalloc byte[6];
        var read = file.Read(magic);
        file.Position = 0;
        magic = magic[..read];

        if (magic.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B]))
        {
            return new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        }

        if (magic.StartsWith("BZh"u8))
        {
            return BZip2Stream.Create(file, SharpCompress.Compressors.CompressionMode.Decompress,
                decompressConcatenated: true, leaveOpen: true);
        }

        if (magic.StartsWith((ReadOnlySpan<byte>)[0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]))
        {
            return new XZStream(file);
        }

        return file;
    }

    /// <summary>
    /// True when a TAR comes first once decompressed: the checksum of its header adds up, or it is an empty
    /// one. Anything unreadable is left to SharpCompress, which then says what is wrong with it.
    /// </summary>
    private static bool HoldsTar(string archivePath)
    {
        try
        {
            using var file = OpenFile(archivePath);
            using var content = Decompressed(file);
            var header = new byte[TarBlockSize];
            var filled = 0;
            int read;
            while (filled < header.Length && (read = content.Read(header, filled, header.Length - filled)) > 0)
            {
                filled += read;
            }

            return filled == header.Length && (ChecksumAddsUp(header) || IsEmptyTar(header, content));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or SharpCompressException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The name a lone <c>.gz</c> goes by when its header holds none, or null when it holds one or is no
    /// gzip: the file's own name without the extension, as <c>gunzip</c> would write it. The name is
    /// optional in the format (.NET's <c>GZipStream</c>, <c>gzip -n</c> and stdin never write it), and
    /// SharpCompress reports such an entry with no key, so the archive looked empty.
    /// </summary>
    private static string? NamelessGzipEntry(string archivePath)
    {
        const int FlagsOffset = 3;
        const byte HasName = 0x08;
        Span<byte> header = stackalloc byte[FlagsOffset + 1];
        try
        {
            using var file = OpenFile(archivePath);
            if (file.Read(header) < header.Length || !header.StartsWith((ReadOnlySpan<byte>)[0x1F, 0x8B])
                || (header[FlagsOffset] & HasName) != 0)
            {
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(archivePath);
        return name.Length > 0 ? name : Path.GetFileName(archivePath);
    }

    /// <summary>
    /// What the gzip trailer says the content takes: the last four bytes, modulo 4 GiB. Only a size to
    /// show (<c>gzip -l</c> shows the same); nothing relies on it to hold or to end the data.
    /// </summary>
    private static long GzipStatedLength(string archivePath)
    {
        Span<byte> trailer = stackalloc byte[sizeof(uint)];
        using var file = OpenFile(archivePath);
        if (file.Length < trailer.Length)
        {
            return 0;
        }

        file.Seek(-trailer.Length, SeekOrigin.End);
        file.ReadExactly(trailer);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer);
    }

    private static void CopyGzip(string archivePath, Stream target)
    {
        using var file = OpenFile(archivePath);
        using var content = new GZipStream(file, CompressionMode.Decompress, leaveOpen: true);
        content.CopyTo(target);
    }

    private const int TarBlockSize = 512;

    /// <summary>Far more than an end marker padded to any blocking factor in use.</summary>
    private const int MaxEmptyTarLength = 1024 * 1024;

    /// <summary>
    /// An empty TAR is its end marker and nothing else: a few blocks of zeros. A first block of zeros alone
    /// says little, since many a file in a lone <c>.gz</c> starts that way (a disk image, the system area of
    /// an ISO), and taking one for an empty TAR showed an archive with nothing to extract.
    /// </summary>
    private static bool IsEmptyTar(byte[] header, Stream content)
    {
        if (header.AsSpan().ContainsAnyExcept((byte)0))
        {
            return false;
        }

        var block = new byte[TarBlockSize];
        var length = (long)header.Length;
        int read;
        while ((read = content.Read(block, 0, block.Length)) > 0)
        {
            length += read;
            if (length > MaxEmptyTarLength || block.AsSpan(0, read).ContainsAnyExcept((byte)0))
            {
                return false;
            }
        }

        return length % TarBlockSize == 0;
    }

    /// <summary>The stored checksum, in octal, is the sum of the header's bytes with its own eight counted as spaces.</summary>
    private static bool ChecksumAddsUp(byte[] header)
    {
        const int ChecksumOffset = 148;
        const int ChecksumLength = 8;

        var stored = 0;
        var digits = 0;
        foreach (var b in header.AsSpan(ChecksumOffset, ChecksumLength))
        {
            if (b is >= (byte)'0' and <= (byte)'7')
            {
                stored = stored * 8 + (b - '0');
                digits++;
            }
            else if (digits > 0)
            {
                break;
            }
        }

        var sum = 0;
        for (var i = 0; i < header.Length; i++)
        {
            sum += i is >= ChecksumOffset and < ChecksumOffset + ChecksumLength ? ' ' : header[i];
        }

        return digits > 0 && stored == sum;
    }

    public override void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files) =>
        Pack(archivePath, files, System.IO.Compression.CompressionLevel.Optimal, CancellationToken.None);

    /// <summary>
    /// Writes a new TAR (compression picked from the extension: .gz/.tgz, .bz2/.tbz2, else none).
    /// Directory entries (names ending in <c>/</c>) are skipped: SharpCompress' TAR writer can only
    /// emit files, so an empty directory does not survive a TAR round trip.
    /// </summary>
    /// <remarks>
    /// Gzip is the BCL's, at the level asked for: the writer's own stored the data (a 3 KB TAR grew by the
    /// gzip framing), and it never heard of the level.
    /// </remarks>
    public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files,
        System.IO.Compression.CompressionLevel compressionLevel, CancellationToken ct)
    {
        var completed = false;
        try
        {
            using (var outStream = File.Create(archivePath))
            {
                var ext = Path.GetExtension(archivePath).ToLowerInvariant();
                using var gzip = ext is ".gz" or ".tgz"
                    ? new GZipStream(outStream, compressionLevel, leaveOpen: true)
                    : null;
                var compType = ext is ".bz2" or ".tbz2" ? CompressionType.BZip2 : CompressionType.None;

                var opts = new SharpCompress.Writers.Tar.TarWriterOptions(compType, finalizeArchiveOnClose: true);
                using var writer = WriterFactory.OpenWriter((Stream?)gzip ?? outStream, ArchiveType.Tar, opts);

                foreach (var (localPath, entryName) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entryName.EndsWith('/'))
                    {
                        continue;
                    }

                    writer.Write(entryName, localPath);
                }
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(archivePath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the caller reports the original failure.
        }
    }
}
