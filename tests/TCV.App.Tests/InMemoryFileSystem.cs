using System.IO;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Minimal in-memory <see cref="IVirtualFileSystem"/> standing in for a remote host in tests.
/// Paths are POSIX-style, like FTP/SFTP.
/// </summary>
public sealed class InMemoryFileSystem : IVirtualFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase) { "/" };

    public InMemoryFileSystem(string scheme = "mem") => Scheme = scheme;

    public string Scheme { get; }
    public string DisplayName => $"{Scheme}://test";

    public event EventHandler<VfsChangedEventArgs>? Changed;

    /// <summary>Number of times a byte stream was opened for writing — proves data flowed through us.</summary>
    public int WritesOpened { get; private set; }

    /// <summary>Delete round trips issued — on a real server each one costs a request.</summary>
    public int DeleteCalls { get; private set; }

    /// <summary>When set, listing this directory throws — stands in for one we may not read.</summary>
    public string? FailListingOf { get; set; }

    /// <summary>What listing <see cref="FailListingOf"/> throws; access denied when not set.</summary>
    public Func<Exception>? FailListingWith { get; set; }

    public void AddFile(string path, byte[] content)
    {
        _files[Normalize(path)] = content;
        var parent = ParentOf(Normalize(path));
        while (parent is not null)
        {
            _dirs.Add(parent);
            parent = ParentOf(parent);
        }
    }

    /// <summary>Adds a file with a chosen modification time, for the "newer / older" cases.</summary>
    public void AddFile(string path, byte[] content, DateTime modified)
    {
        AddFile(path, content);
        _modified[Normalize(path)] = modified;
    }

    private readonly Dictionary<string, DateTime> _modified = new(StringComparer.OrdinalIgnoreCase);

    public void AddDirectory(string path) => _dirs.Add(Normalize(path));

    public bool HasFile(string path) => _files.ContainsKey(Normalize(path));
    public bool HasDirectory(string path) => _dirs.Contains(Normalize(path));
    public byte[] ReadFile(string path) => _files[Normalize(path)];

    // ── IVirtualFileSystem ────────────────────────────────────────────────────

    /// <summary>Listings asked for — proves a panel re-read the folder.</summary>
    public int ListCalls { get; private set; }

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        ListCalls++;
        var dir = Normalize(path.Value);
        if (!_dirs.Contains(dir))
            throw new DirectoryNotFoundException(dir);
        if (FailListingOf is not null && dir == Normalize(FailListingOf))
            throw FailListingWith?.Invoke() ?? new UnauthorizedAccessException(dir);

        var entries = new List<IFileEntry>();

        foreach (var d in _dirs.Where(d => d != "/" && ParentOf(d) == dir))
            entries.Add(MakeEntry(d, isDirectory: true, 0));

        foreach (var (f, content) in _files.Where(kv => ParentOf(kv.Key) == dir))
            entries.Add(MakeEntry(f, isDirectory: false, content.Length));

        return Task.FromResult<IReadOnlyList<IFileEntry>>(entries);
    }

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        var p = Normalize(path.Value);
        if (_dirs.Contains(p)) return Task.FromResult<IFileEntry?>(MakeEntry(p, true, 0));
        if (_files.TryGetValue(p, out var content))
            return Task.FromResult<IFileEntry?>(MakeEntry(p, false, content.Length));
        return Task.FromResult<IFileEntry?>(null);
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        var p = Normalize(path.Value);
        if (!_files.TryGetValue(p, out var content))
            throw new FileNotFoundException(p);
        return Task.FromResult<Stream>(new HookedStream(content, () => OnFirstRead?.Invoke()));
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        WritesOpened++;
        var p = Normalize(path.Value);
        return Task.FromResult<Stream>(new CapturingStream(this, p));
    }

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        // A missing source fails the way the disk does, so per-item error handling has something real to catch
        if (!_files.TryGetValue(Normalize(source.Value), out var content))
            throw new FileNotFoundException(source.Value);
        _files[Normalize(destination.Value)] = content;
        return Task.CompletedTask;
    }

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        var s = Normalize(source.Value);
        if (!_files.ContainsKey(s))
            throw new FileNotFoundException(source.Value);
        _files[Normalize(destination.Value)] = _files[s];
        _files.Remove(s);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        DeleteCalls++;
        var p = Normalize(path.Value);
        _files.Remove(p);
        _dirs.Remove(p);
        if (recursive)
        {
            var prefix = p.TrimEnd('/') + "/";
            foreach (var f in _files.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _files.Remove(f);
            foreach (var d in _dirs.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
                _dirs.Remove(d);
        }
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        _dirs.Add(Normalize(path.Value));
        return Task.CompletedTask;
    }

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        var p = Normalize(path.Value);
        var target = Normalize((ParentOf(p) ?? "/").TrimEnd('/') + "/" + newName);
        if (_files.Remove(p, out var content)) _files[target] = content;
        else if (_dirs.Remove(p)) _dirs.Add(target);
        return Task.CompletedTask;
    }

    public bool SupportsOperation(VfsOperation operation) => true;

    public void Dispose() => IsDisposed = true;

    public bool IsDisposed { get; private set; }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Normalize(string path)
    {
        var p = path.Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        return p.Length > 1 ? p.TrimEnd('/') : p;
    }

    private static string? ParentOf(string path)
    {
        if (path == "/") return null;
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    private IFileEntry MakeEntry(string path, bool isDirectory, long size)
    {
        // Name comes from VfsPath so the fake reports entries the way production paths do
        var vfsPath = new VfsPath(path);
        var modified = _modified.TryGetValue(path, out var m) ? m : new DateTime(2026, 1, 1);
        return new FileEntry(vfsPath, vfsPath.FileName,
            isDirectory, size, modified,
            isDirectory ? FileAttributes.Directory : FileAttributes.Normal);
    }

    /// <summary>Runs after the first chunk of any read stream has been handed out — lets a test cancel mid-file.</summary>
    public Action? OnFirstRead { get; set; }

    private sealed class HookedStream : MemoryStream
    {
        private readonly Action _afterFirstRead;
        private bool _fired;

        public HookedStream(byte[] content, Action afterFirstRead) : base(content, writable: false) =>
            _afterFirstRead = afterFirstRead;

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            if (!_fired)
            {
                _fired = true;
                _afterFirstRead();
            }
            return ValueTask.FromResult(read);
        }
    }

    /// <summary>Writes land in the parent dictionary when the stream is disposed.</summary>
    private sealed class CapturingStream : MemoryStream
    {
        private readonly InMemoryFileSystem _owner;
        private readonly string _path;

        public CapturingStream(InMemoryFileSystem owner, string path)
        {
            _owner = owner;
            _path = path;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _owner._files[_path] = ToArray();
            base.Dispose(disposing);
        }
    }
}
