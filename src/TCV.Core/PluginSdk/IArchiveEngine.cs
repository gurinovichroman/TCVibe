using System.IO.Compression;

namespace TCV.Core.PluginSdk;

/// <summary>Abstraction over a specific archive format (ZIP, 7z, RAR, TAR).</summary>
public interface IArchiveEngine
{
    /// <summary>
    /// Gets whether entries of an existing archive can be added, deleted and renamed
    /// (<see cref="AddFiles(string, IEnumerable{ValueTuple{string, string}})"/> /
    /// <see cref="DeleteEntry"/> / <see cref="RenameEntries"/>). Creating a fresh archive with
    /// <see cref="Pack(string, IEnumerable{ValueTuple{string, string}})"/> does not require it — a TAR can be
    /// created but not modified, so it reports false.
    /// </summary>
    bool CanWrite { get; }

    /// <summary>Returns all entries (files + directories) in the archive.</summary>
    IReadOnlyList<ArchiveEntry> ListAll(string archivePath);

    /// <summary>Opens a read stream for the given entry path inside the archive.</summary>
    Stream ExtractEntry(string archivePath, string entryPath);

    /// <summary>
    /// Packs files into a new archive. An entry name ending in <c>/</c> is an (empty) directory
    /// entry: <c>localPath</c> is ignored and the directory is recreated on extraction.
    /// </summary>
    void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files);

    /// <summary>
    /// Packs with an explicit compression level and cancellation. Engines that support neither
    /// fall back to <see cref="Pack(string, IEnumerable{ValueTuple{string, string}})"/>.
    /// </summary>
    void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files,
        CompressionLevel compressionLevel, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Pack(archivePath, files);
    }

    /// <summary>Extracts the entire archive to a destination directory.</summary>
    void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null);

    /// <summary>Cancellable extraction; cancellation is honoured between entries.</summary>
    void ExtractAll(string archivePath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ExtractAll(archivePath, destDir, progress);
    }

    /// <summary>
    /// Extracts several entries to <c>destPath</c>s in one pass. Solid formats (7z, RAR, TAR.GZ)
    /// override this to open the archive once instead of once per entry, which turns extracting a
    /// folder from O(N²) into O(N). The default extracts one by one.
    /// </summary>
    void ExtractEntries(string archivePath, IReadOnlyList<(string entryPath, string destPath)> entries,
        CancellationToken ct)
    {
        foreach (var (entryPath, destPath) in entries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var source = ExtractEntry(archivePath, entryPath);
            using var target = File.Create(destPath);
            source.CopyTo(target);
        }
    }

    /// <summary>
    /// Reads several file entries in one pass and hands each to <paramref name="consume"/> as a
    /// forward-only stream, together with the path it was asked for. The stream is valid only during
    /// that call, and the order is the archive's, not the request's. Paths are compared after slash
    /// normalisation, an exact name before one that differs only by case, and each entry is handed
    /// over at most once. Solid formats (7z, RAR, TAR.GZ) override this to decode the archive once
    /// instead of from the start for every entry, and stop once everything asked for was handed over;
    /// they leave out a path that names no file. The default reads the entries one by one through
    /// <see cref="ExtractEntry"/>, which throws <see cref="FileNotFoundException"/> for such a path.
    /// Callers that need every entry compare what they were handed with what they asked for.
    /// </summary>
    void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume,
        CancellationToken ct)
    {
        foreach (var entryPath in entryPaths.Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            using var source = ExtractEntry(archivePath, entryPath);
            consume(entryPath, source);
        }
    }

    /// <summary>
    /// Adds files to an existing archive. Only supported for writable formats (ZIP). As in
    /// <see cref="Pack(string, IEnumerable{ValueTuple{string, string}})"/>, an entry name ending in <c>/</c>
    /// adds an empty directory and its <c>localPath</c> is ignored (F7 inside an archive).
    /// </summary>
    void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files);

    /// <summary>
    /// Cancellable add. ZIP honours cancellation between entries and leaves the archive untouched
    /// when cancelled or failed; the default only checks before it starts.
    /// </summary>
    void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        AddFiles(archivePath, files);
    }

    /// <summary>Deletes an entry (file or directory) from an existing archive. Only supported for writable formats (ZIP).</summary>
    void DeleteEntry(string archivePath, string entryPath);

    /// <summary>
    /// Deletes several entries at once. ZIP overrides this to rewrite the archive once for the whole
    /// batch instead of once per entry, and all or nothing. The default deletes one by one.
    /// </summary>
    void DeleteEntries(string archivePath, IReadOnlyList<string> entryPaths, CancellationToken ct)
    {
        foreach (var entryPath in entryPaths)
        {
            ct.ThrowIfCancellationRequested();
            DeleteEntry(archivePath, entryPath);
        }
    }

    /// <summary>
    /// Renames entries of an existing archive in one rewrite, all or nothing. Each pair names an entry by
    /// its path (a directory takes everything inside it along) and the new name it gets in the same
    /// folder. The renames apply at once, so a swap or a chain needs no temporary name; a name that is
    /// taken, or an entry that is not there, refuses the whole batch before anything is written. Every
    /// engine that reports <see cref="CanWrite"/> implements it; the default refuses.
    /// </summary>
    void RenameEntries(string archivePath, IReadOnlyList<(string EntryPath, string NewName)> renames,
        CancellationToken ct) =>
        throw new NotSupportedException("Renaming entries in this archive format is not supported.");
}

/// <summary>Represents an entry (file or directory) inside an archive.</summary>
public record ArchiveEntry(
    string FullPath,
    string Name,
    bool IsDirectory,
    long Size,
    DateTime LastModified);
