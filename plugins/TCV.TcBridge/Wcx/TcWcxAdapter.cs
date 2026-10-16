using System.IO;
using System.Runtime.InteropServices;
using TCV.Core.Domain;
using TCV.Core.Exceptions;
using TCV.Core.Interfaces;

namespace TCV.TcBridge.Wcx;

/// <summary>
/// Loads a WCX DLL and wraps it as an <see cref="IVirtualFileSystem"/> that exposes
/// archive contents as a navigable directory tree.
/// Each instance is created per archive file; the archive handle is kept open only
/// during enumeration and extraction, then closed.
/// </summary>
internal sealed class TcWcxAdapter : IVirtualFileSystem
{
    private readonly nint  _moduleHandle;
    private readonly string _archivePath;
    private bool _disposed;

    // ---- exports (prefer Unicode, fall back to ANSI) -----------------------
    private readonly WcxNative.OpenArchiveDelegate?       _openArchive;
    private readonly WcxNative.OpenArchiveWDelegate?      _openArchiveW;
    private readonly WcxNative.ReadHeaderDelegate?        _readHeader;
    private readonly WcxNative.ReadHeaderExWDelegate?     _readHeaderExW;
    private readonly WcxNative.ProcessFileDelegate?       _processFile;
    private readonly WcxNative.ProcessFileWDelegate?      _processFileW;
    private readonly WcxNative.CloseArchiveDelegate       _closeArchive;
    private readonly WcxNative.PackFilesDelegate?         _packFiles;
    private readonly WcxNative.DeleteFilesDelegate?       _deleteFiles;
    private readonly WcxNative.CanYouHandleThisFileDelegate?  _canHandle;
    private readonly WcxNative.CanYouHandleThisFileWDelegate? _canHandleW;

    // Keep callbacks alive to prevent GC collection while native code holds them.
    private readonly WcxNative.tProcessDataProc  _processDataProc;
    private readonly WcxNative.tChangeVolProc    _changeVolProc;
    private readonly WcxNative.tProcessDataProcW _processDataProcW;
    private readonly WcxNative.tChangeVolProcW   _changeVolProcW;

    private readonly bool _hasUnicode;

    // Lazily-built index: virtualPath -> entry metadata
    private List<WcxEntry>? _cachedEntries;

    public string Scheme      { get; }
    public string DisplayName { get; }

    public event EventHandler<VfsChangedEventArgs>? Changed;

    public TcWcxAdapter(string dllPath, string archivePath, string scheme)
    {
        _moduleHandle = NativeLibrary.Load(dllPath);
        _archivePath  = archivePath;
        Scheme        = scheme;
        DisplayName   = System.IO.Path.GetFileName(archivePath);

        // Resolve callbacks kept alive.
        _processDataProc  = OnProcessData;
        _changeVolProc    = OnChangeVol;
        _processDataProcW = OnProcessDataW;
        _changeVolProcW   = OnChangeVolW;

        // Unicode exports
        _openArchiveW  = TryGet<WcxNative.OpenArchiveWDelegate>("OpenArchiveW");
        _readHeaderExW = TryGet<WcxNative.ReadHeaderExWDelegate>("ReadHeaderExW");
        _processFileW  = TryGet<WcxNative.ProcessFileWDelegate>("ProcessFileW");
        _canHandleW    = TryGet<WcxNative.CanYouHandleThisFileWDelegate>("CanYouHandleThisFileW");

        _hasUnicode = _openArchiveW is not null && _readHeaderExW is not null && _processFileW is not null;

        // ANSI exports
        _openArchive  = TryGet<WcxNative.OpenArchiveDelegate>("OpenArchive");
        _readHeader   = TryGet<WcxNative.ReadHeaderDelegate>("ReadHeader");
        _processFile  = TryGet<WcxNative.ProcessFileDelegate>("ProcessFile");
        _canHandle    = TryGet<WcxNative.CanYouHandleThisFileDelegate>("CanYouHandleThisFile");
        _closeArchive = GetRequired<WcxNative.CloseArchiveDelegate>("CloseArchive");
        _packFiles    = TryGet<WcxNative.PackFilesDelegate>("PackFiles");
        _deleteFiles  = TryGet<WcxNative.DeleteFilesDelegate>("DeleteFiles");
    }

    // -------------------------------------------------------------------------
    // IVirtualFileSystem
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        EnsureCachedEntries();

        string prefix = path == VfsPath.Root
            ? string.Empty
            : path.Value.TrimStart('/') + "/";

        IReadOnlyList<IFileEntry> result = _cachedEntries!
            .Where(e =>
            {
                // Include only direct children of 'path'.
                string rel = e.VirtualPath.TrimStart('/');
                if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
                string remainder = rel[prefix.Length..];
                // Direct child: no more slashes.
                return !remainder.Contains('/') && remainder.Length > 0;
            })
            .Select(e => (IFileEntry)e.ToFileEntry())
            .ToList();

        return Task.FromResult(result);
    }

    public async Task<IFileEntry> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var parent  = path.Parent ?? VfsPath.Root;
        var entries = await ListAsync(parent, ct).ConfigureAwait(false);
        var entry   = entries.FirstOrDefault(e =>
            string.Equals(e.Name, path.FileName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
            throw new VfsException($"Entry not found: {path}", path);
        return entry;
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string destFile = ExtractToTemp(path, ct);
        return Task.FromResult<Stream>(
            new FileStream(destFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous));
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
        => throw new VfsException("Writing to WCX archives is not supported via stream API.", path);

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
        => throw new VfsException("Cross-path copy within WCX archives is not supported.", source);

    public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
        => throw new VfsException("Move within WCX archives is not supported.", source);

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_deleteFiles is null)
            throw new VfsException("WCX plugin does not support deletion.", path);

        // DeleteFiles expects a null-terminated list of filenames (double-null terminated string).
        string nativePath = path.Value.TrimStart('/').Replace('/', '\\') + "\0\0";
        int rc = _deleteFiles(_archivePath, nativePath);
        if (rc != WcxNative.E_SUCCESS)
            throw new VfsException($"WCX DeleteFiles returned error {rc}.", path);

        _cachedEntries = null; // Invalidate cache.
        Changed?.Invoke(this, new VfsChangedEventArgs(path, System.IO.WatcherChangeTypes.Deleted));
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
        => throw new VfsException("Creating directories in WCX archives is not supported.", path);

    public Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
        => throw new VfsException("Renaming within WCX archives is not supported.", path);

    public bool SupportsOperation(VfsOperation operation) => operation switch
    {
        VfsOperation.Read   => true,
        VfsOperation.Delete => _deleteFiles is not null,
        _                   => false,
    };

    // -------------------------------------------------------------------------
    // Capability check
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns true if the plugin claims to handle the specified archive file.
    /// </summary>
    public bool CanHandleFile(string filePath)
    {
        if (_canHandleW is not null) return _canHandleW(filePath);
        if (_canHandle  is not null) return _canHandle(filePath);
        return false;
    }

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_moduleHandle);
    }

    // -------------------------------------------------------------------------
    // Private: archive enumeration
    // -------------------------------------------------------------------------

    private void EnsureCachedEntries()
    {
        if (_cachedEntries is not null) return;
        _cachedEntries = _hasUnicode ? ReadEntriesUnicode() : ReadEntriesAnsi();
    }

    private List<WcxEntry> ReadEntriesUnicode()
    {
        var archData = new WcxNative.tOpenArchiveDataW
        {
            ArcName  = _archivePath,
            OpenMode = WcxNative.PK_OM_LIST,
        };
        nint hArc = _openArchiveW!(ref archData);
        if (hArc == 0 || archData.OpenResult != WcxNative.E_SUCCESS)
            throw new VfsException($"Cannot open archive '{_archivePath}': error {archData.OpenResult}.");

        var entries = new List<WcxEntry>();
        try
        {
            var hdr = new WcxNative.tHeaderDataExW();
            while (_readHeaderExW!(hArc, ref hdr) == WcxNative.E_SUCCESS)
            {
                entries.Add(WcxEntry.FromHeaderExW(in hdr));
                int rc = _processFileW!(hArc, WcxNative.PK_SKIP, null, null);
                if (rc != WcxNative.E_SUCCESS) break;
            }
        }
        finally
        {
            _closeArchive(hArc);
        }
        return entries;
    }

    private List<WcxEntry> ReadEntriesAnsi()
    {
        if (_openArchive is null || _readHeader is null || _processFile is null)
            return new List<WcxEntry>();

        var archData = new WcxNative.tOpenArchiveData
        {
            ArcName  = _archivePath,
            OpenMode = WcxNative.PK_OM_LIST,
        };
        nint hArc = _openArchive(ref archData);
        if (hArc == 0 || archData.OpenResult != WcxNative.E_SUCCESS)
            throw new VfsException($"Cannot open archive '{_archivePath}': error {archData.OpenResult}.");

        var entries = new List<WcxEntry>();
        try
        {
            var hdr = new WcxNative.tHeaderData();
            while (_readHeader(hArc, ref hdr) == WcxNative.E_SUCCESS)
            {
                entries.Add(WcxEntry.FromHeaderData(in hdr));
                int rc = _processFile(hArc, WcxNative.PK_SKIP, null, null);
                if (rc != WcxNative.E_SUCCESS) break;
            }
        }
        finally
        {
            _closeArchive(hArc);
        }
        return entries;
    }

    private string ExtractToTemp(VfsPath path, CancellationToken ct)
    {
        string nativeName = path.Value.TrimStart('/').Replace('/', '\\');
        string destDir    = Path.Combine(Path.GetTempPath(), "TCV.TcBridge", Scheme,
                                         Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(destDir);
        string destFile = Path.Combine(destDir, System.IO.Path.GetFileName(nativeName));

        if (_hasUnicode)
            ExtractUnicode(nativeName, destDir, destFile, ct);
        else
            ExtractAnsi(nativeName, destDir, destFile, ct);

        return destFile;
    }

    private void ExtractUnicode(string nativeName, string destDir, string destFile, CancellationToken ct)
    {
        var archData = new WcxNative.tOpenArchiveDataW
        {
            ArcName  = _archivePath,
            OpenMode = WcxNative.PK_OM_EXTRACT,
        };
        nint hArc = _openArchiveW!(ref archData);
        if (hArc == 0)
            throw new VfsException($"Cannot open archive for extraction: '{_archivePath}'.");
        try
        {
            var hdr = new WcxNative.tHeaderDataExW();
            while (_readHeaderExW!(hArc, ref hdr) == WcxNative.E_SUCCESS)
            {
                ct.ThrowIfCancellationRequested();
                bool match = string.Equals(
                    hdr.FileName.Replace('/', '\\').TrimStart('\\'),
                    nativeName.TrimStart('\\'),
                    StringComparison.OrdinalIgnoreCase);
                int op = match ? WcxNative.PK_EXTRACT : WcxNative.PK_SKIP;
                string? dp = match ? destDir  : null;
                string? dn = match ? destFile : null;
                int rc = _processFileW!(hArc, op, dp, dn);
                if (rc != WcxNative.E_SUCCESS && match)
                    throw new VfsException($"WCX extract error {rc} for '{nativeName}'.");
                if (match) break;
            }
        }
        finally
        {
            _closeArchive(hArc);
        }
    }

    private void ExtractAnsi(string nativeName, string destDir, string destFile, CancellationToken ct)
    {
        if (_openArchive is null || _readHeader is null || _processFile is null) return;

        var archData = new WcxNative.tOpenArchiveData
        {
            ArcName  = _archivePath,
            OpenMode = WcxNative.PK_OM_EXTRACT,
        };
        nint hArc = _openArchive(ref archData);
        if (hArc == 0)
            throw new VfsException($"Cannot open archive for extraction: '{_archivePath}'.");
        try
        {
            var hdr = new WcxNative.tHeaderData();
            while (_readHeader(hArc, ref hdr) == WcxNative.E_SUCCESS)
            {
                ct.ThrowIfCancellationRequested();
                bool match = string.Equals(
                    hdr.FileName.Replace('/', '\\').TrimStart('\\'),
                    nativeName.TrimStart('\\'),
                    StringComparison.OrdinalIgnoreCase);
                int op = match ? WcxNative.PK_EXTRACT : WcxNative.PK_SKIP;
                string? dp = match ? destDir  : null;
                string? dn = match ? destFile : null;
                int rc = _processFile(hArc, op, dp, dn);
                if (rc != WcxNative.E_SUCCESS && match)
                    throw new VfsException($"WCX extract error {rc} for '{nativeName}'.");
                if (match) break;
            }
        }
        finally
        {
            _closeArchive(hArc);
        }
    }

    // -------------------------------------------------------------------------
    // Native callbacks
    // -------------------------------------------------------------------------

    private static int OnProcessData(string fileName, int size) => 1; // continue
    private static int OnChangeVol(string arcName, int mode) => 1;
    private static int OnProcessDataW(string fileName, int size) => 1;
    private static int OnChangeVolW(string arcName, int mode) => 1;

    // -------------------------------------------------------------------------
    // Export resolution helpers
    // -------------------------------------------------------------------------

    private T GetRequired<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_moduleHandle, name, out nint addr))
            throw new EntryPointNotFoundException($"WCX plugin does not export required function '{name}'.");
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private T? TryGet<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_moduleHandle, name, out nint addr)) return null;
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }
}

// ---------------------------------------------------------------------------
// Internal value type for a cached archive entry
// ---------------------------------------------------------------------------

internal sealed class WcxEntry
{
    public string  VirtualPath   { get; init; } = string.Empty;
    public string  Name          { get; init; } = string.Empty;
    public bool    IsDirectory   { get; init; }
    public long    UnpackedSize  { get; init; }
    public DateTime LastModified { get; init; }

    internal static WcxEntry FromHeaderData(in WcxNative.tHeaderData h)
    {
        string vp   = "/" + h.FileName.Replace('\\', '/').TrimStart('/');
        string name = System.IO.Path.GetFileName(vp.TrimEnd('/'));
        bool   isDir= vp.EndsWith('/') || (h.FileAttr & 0x10) != 0;
        return new WcxEntry
        {
            VirtualPath   = vp,
            Name          = name,
            IsDirectory   = isDir,
            UnpackedSize  = h.UnpSize,
            LastModified  = WcxNative.DosDateTimeToDateTime(h.FileTime),
        };
    }

    internal static WcxEntry FromHeaderExW(in WcxNative.tHeaderDataExW h)
    {
        string vp   = "/" + h.FileName.Replace('\\', '/').TrimStart('/');
        string name = System.IO.Path.GetFileName(vp.TrimEnd('/'));
        bool   isDir= vp.EndsWith('/') || (h.FileAttr & 0x10) != 0;
        long   size = ((long)h.UnpSizeHigh << 32) | h.UnpSizeLow;
        return new WcxEntry
        {
            VirtualPath   = vp,
            Name          = name,
            IsDirectory   = isDir,
            UnpackedSize  = size,
            LastModified  = WcxNative.DosDateTimeToDateTime(h.FileTime),
        };
    }

    internal FileEntry ToFileEntry()
    {
        var path       = new VfsPath(VirtualPath);
        var attributes = IsDirectory
            ? System.IO.FileAttributes.Directory
            : System.IO.FileAttributes.Archive;
        return new FileEntry(path, Name, IsDirectory, UnpackedSize, LastModified, attributes);
    }
}
