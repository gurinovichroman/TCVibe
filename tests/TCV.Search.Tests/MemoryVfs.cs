using System.Text;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Search.Tests;

/// <summary>
/// Minimal read-only in-memory file system for the search tests: a flat map of slash-separated
/// paths to file contents, from which directories are derived. Enough to exercise traversal,
/// <c>.gitignore</c> handling and content matching without touching a disk.
/// </summary>
internal sealed class MemoryVfs : IVirtualFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    public string Scheme => "mem";
    public string DisplayName => "memory";

    /// <summary>
    /// Adds a text file at <paramref name="path"/> ("/src/a.cs"), written the way an editor would:
    /// the encoding's byte-order mark first, if it has one. UTF-8-without-BOM and the legacy code
    /// pages have an empty preamble, so only UTF-16/32 files actually gain one.
    /// </summary>
    public MemoryVfs Add(string path, string content = "", Encoding? encoding = null)
    {
        var enc = encoding ?? new UTF8Encoding(false);
        _files[Normalize(path)] = [.. enc.GetPreamble(), .. enc.GetBytes(content)];
        return this;
    }

    /// <summary>Adds a file with raw bytes — used for binary and legacy-code-page cases.</summary>
    public MemoryVfs AddBytes(string path, byte[] content)
    {
        _files[Normalize(path)] = content;
        return this;
    }

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        var dir = Normalize(path.Value);
        var prefix = dir == "/" ? "/" : dir + "/";

        var files = new List<IFileEntry>();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var full in _files.Keys)
        {
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = full[prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
                files.Add(new MemoryEntry(full, rest, false, _files[full].Length));
            else
                dirs.Add(rest[..slash]);
        }

        var entries = dirs
            .Select(d => (IFileEntry)new MemoryEntry(prefix + d, d, true, 0))
            .Concat(files)
            .ToList();
        return Task.FromResult<IReadOnlyList<IFileEntry>>(entries);
    }

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var full = Normalize(path.Value);
        if (!_files.TryGetValue(full, out var bytes)) return Task.FromResult<IFileEntry?>(null);
        var name = full[(full.LastIndexOf('/') + 1)..];
        return Task.FromResult<IFileEntry?>(new MemoryEntry(full, name, false, bytes.Length));
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var full = Normalize(path.Value);
        if (!_files.TryGetValue(full, out var bytes))
            throw new FileNotFoundException(full);
        return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
    }

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    // ── Not needed by the read-only search path ───────────────────────────────

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) =>
        throw new NotSupportedException();
    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) =>
        throw new NotSupportedException();
    public bool SupportsOperation(VfsOperation operation) => operation == VfsOperation.Read;

    public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

    public void Dispose() { }

    private sealed record MemoryEntry(string FullPath, string Name, bool IsDirectory, long Size) : IFileEntry
    {
        public VfsPath Path => new(FullPath);
        public DateTime LastModified => new(2026, 1, 1);
        public FileAttributes Attributes => IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
    }
}
