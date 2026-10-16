using System.IO;
using TCV.Archives.Engines;
using TCV.Core.Domain;
using TCV.Core.Exceptions;
using TCV.Core.Interfaces;

namespace TCV.Archives;

/// <summary>
/// Virtual file system that exposes the contents of an archive as a navigable tree.
/// Path "/" is the archive root. Entries like "/folder/file.txt" map to archive entries.
/// </summary>
public sealed class ArchiveFileSystem : IVirtualFileSystem, IBatchRenameFileSystem
{
    private readonly string _archivePath;
    private readonly TCV.Core.PluginSdk.IArchiveEngine _engine;
    private IReadOnlyList<TCV.Core.PluginSdk.ArchiveEntry>? _cachedEntries;
    private ILookup<string, TCV.Core.PluginSdk.ArchiveEntry>? _byParent;
    private readonly object _cacheLock = new();

    public ArchiveFileSystem(string archivePath, TCV.Core.PluginSdk.IArchiveEngine engine,
        IReadOnlyList<TCV.Core.PluginSdk.ArchiveEntry>? preloadedEntries = null)
    {
        _archivePath = archivePath;
        _engine = engine;
        if (preloadedEntries is not null)
        {
            Store(preloadedEntries);
        }
    }

    /// <summary>Full path to the archive file on disk.</summary>
    public string ArchivePath => _archivePath;

    /// <summary>The engine used to read/write this archive.</summary>
    public TCV.Core.PluginSdk.IArchiveEngine Engine => _engine;

    /// <summary>Clears the cached entry list so the next ListAsync re-reads from disk.</summary>
    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _cachedEntries = null;
            _byParent = null;
        }
    }

    public string Scheme => DetermineScheme(_archivePath);
    public string DisplayName => Path.GetFileName(_archivePath);

    public event EventHandler<VfsChangedEventArgs>? Changed;

    /// <summary>
    /// The entry list, read on a worker thread when it is not cached: listing a big 7z or ISO is
    /// seconds of decoding and must not run on the caller's (UI) thread (INV-06).
    /// </summary>
    private async Task<IReadOnlyList<TCV.Core.PluginSdk.ArchiveEntry>> GetEntriesAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedEntries is not null)
            {
                return _cachedEntries;
            }
        }

        var listed = await Task.Run(() => _engine.ListAll(_archivePath), ct).ConfigureAwait(false);
        lock (_cacheLock)
        {
            return _cachedEntries ?? Store(listed);
        }
    }

    /// <summary>
    /// Caches a listing in normal form — slash-free root, forward slashes, every parent directory
    /// present — so panel navigation does not depend on how each engine spells its paths.
    /// </summary>
    private IReadOnlyList<TCV.Core.PluginSdk.ArchiveEntry> Store(IReadOnlyList<TCV.Core.PluginSdk.ArchiveEntry> raw)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<TCV.Core.PluginSdk.ArchiveEntry>(raw.Count);
        foreach (var e in raw)
        {
            var path = NormalizePath(e.FullPath);
            if (path.Length == 0)
            {
                continue;
            }

            if (e.IsDirectory && !known.Add(path))
            {
                continue;
            }

            list.Add(e with { FullPath = path });
        }

        // Directories the engine left implicit (CAB and ISO store only files).
        foreach (var e in list.ToList())
        {
            var slash = e.FullPath.LastIndexOf('/');
            while (slash > 0)
            {
                var parent = e.FullPath[..slash];
                if (!known.Add(parent))
                {
                    break;
                }

                list.Add(new TCV.Core.PluginSdk.ArchiveEntry(
                    parent, parent[(parent.LastIndexOf('/') + 1)..], true, 0, e.LastModified));
                slash = parent.LastIndexOf('/');
            }
        }

        _cachedEntries = list;
        _byParent = list.ToLookup(x => GetParentPath(x.FullPath), StringComparer.OrdinalIgnoreCase);
        return list;
    }

    // ── Listing ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path.Value);
        await GetEntriesAsync(ct).ConfigureAwait(false);

        ILookup<string, TCV.Core.PluginSdk.ArchiveEntry> byParent;
        lock (_cacheLock)
        {
            byParent = _byParent!;
        }

        return byParent[normalized].Select(ToFileEntry).ToList();
    }

    public async Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path.Value);

        if (normalized == string.Empty)
        {
            // Archive root
            return new ArchiveFileEntry(path, DisplayName, true, 0, File.GetLastWriteTime(_archivePath));
        }

        var entries = await GetEntriesAsync(ct).ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e =>
            string.Equals(e.FullPath, normalized, StringComparison.OrdinalIgnoreCase));

        return entry is null ? null : ToFileEntry(entry);
    }

    // ── Reading ───────────────────────────────────────────────────────────────

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path.Value);

        // Decoding an entry is synchronous library work (a solid 7z can take seconds): keep it off
        // the caller's thread (INV-06).
        return Task.Run(() => _engine.ExtractEntry(_archivePath, normalized), ct);
    }

    // ── Writing (ZIP only) ────────────────────────────────────────────────────

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
        => throw new NotSupportedException("Writing individual entries inside archives is not supported.");

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
        => throw new NotSupportedException("Copy inside archive is not supported.");

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
        => throw new NotSupportedException("Move inside archive is not supported.");

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
        => throw new NotSupportedException("Delete inside archive is not supported.");

    // ── Editing in place (formats the engine can write: ZIP) ─────────────────
    //
    // Each call rewrites the archive (all or nothing, see the engine), on a worker thread: the engine is
    // synchronous and a big archive takes seconds (INV-06). The listing is read again afterwards.

    /// <summary>An empty folder (F7): a directory entry, which a ZIP can hold without any file in it.</summary>
    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        RejectUnlessWritable("Creating directories inside this archive format is not supported.");
        var entry = NormalizePath(path.Value);
        if (entry.Length == 0)
        {
            throw new IOException("The archive root already exists.");
        }

        try
        {
            await Task.Run(() => _engine.AddFiles(_archivePath, [(string.Empty, entry + "/")], ct), ct)
                .ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) =>
        RenameBatchAsync([(path, newName)], ct);

    /// <inheritdoc/>
    public bool CanRenameAsBatch => _engine.CanWrite;

    /// <summary>
    /// Renames entries in one rewrite of the archive (Multi-Rename runs its whole batch through this), a
    /// directory with everything inside it.
    /// </summary>
    public async Task RenameBatchAsync(IReadOnlyList<(VfsPath Path, string NewName)> renames, CancellationToken ct)
    {
        RejectUnlessWritable("Renaming entries inside this archive format is not supported.");
        var requests = renames.Select(r => (NormalizePath(r.Path.Value), r.NewName)).ToList();
        try
        {
            await Task.Run(() => _engine.RenameEntries(_archivePath, requests, ct), ct).ConfigureAwait(false);
        }
        finally
        {
            Invalidate();
        }
    }

    public bool SupportsOperation(VfsOperation operation) =>
        operation == VfsOperation.Read ||
        (operation is VfsOperation.Delete or VfsOperation.Copy or VfsOperation.Rename or VfsOperation.CreateDirectory
         && _engine.CanWrite);

    private void RejectUnlessWritable(string message)
    {
        if (!_engine.CanWrite)
        {
            throw new NotSupportedException(message);
        }
    }

    public void Dispose() { }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private IFileEntry ToFileEntry(TCV.Core.PluginSdk.ArchiveEntry entry) =>
        new ArchiveFileEntry(
            new VfsPath("/" + entry.FullPath),
            entry.Name,
            entry.IsDirectory,
            entry.Size,
            entry.LastModified);

    /// <summary>Strip leading "/" and normalize slashes. Returns "" for root.</summary>
    public static string NormalizePath(string path) =>
        path.Replace('\\', '/').Trim('/');

    /// <summary>Returns the parent path of an entry. Empty string = root.</summary>
    private static string GetParentPath(string fullPath)
    {
        var lastSlash = fullPath.LastIndexOf('/');
        return lastSlash < 0 ? string.Empty : fullPath[..lastSlash];
    }

    private static string DetermineScheme(string filePath) =>
        Path.GetExtension(filePath).ToLowerInvariant().TrimStart('.') switch
        {
            "zip" or "jar" or "nupkg" or "xpi" => "zip",
            "7z" => "7z",
            "rar" or "r00" => "rar",
            "tar" or "gz" or "tgz" or "bz2" or "tbz2" or "xz" or "txz" => "tar",
            "iso" or "img" => "iso",
            "cab" => "cab",
            _ => "zip",
        };

    // ── Inner FileEntry ───────────────────────────────────────────────────────

    private sealed record ArchiveFileEntry(
        VfsPath Path,
        string Name,
        bool IsDirectory,
        long Size,
        DateTime LastModified) : IFileEntry
    {
        public System.IO.FileAttributes Attributes =>
            IsDirectory ? System.IO.FileAttributes.Directory : System.IO.FileAttributes.Normal;
    }
}
