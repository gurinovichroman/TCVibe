using System.Runtime.InteropServices;

namespace TCV.TcBridge.Wfx;

/// <summary>
/// Loads a WFX DLL via NativeLibrary and resolves all exported function pointers.
/// Prefers Unicode (W-suffix) exports when available, falls back to ANSI.
/// Keeps all callback delegates alive for the lifetime of this object to prevent GC collection.
/// </summary>
internal sealed class TcWfxAdapter : IDisposable
{
    private readonly nint _moduleHandle;
    private bool _disposed;

    // ---- mandatory exports (ANSI fallback) ----------------------------------
    private readonly WfxNative.FsFindFirstDelegate?  _fsFindFirst;
    private readonly WfxNative.FsFindNextDelegate?   _fsFindNext;
    private readonly WfxNative.FsFindCloseDelegate   _fsFindClose;

    // ---- mandatory exports (Unicode preferred) ------------------------------
    private readonly WfxNative.FsFindFirstWDelegate?  _fsFindFirstW;
    private readonly WfxNative.FsFindNextWDelegate?   _fsFindNextW;

    // ---- optional exports (ANSI) --------------------------------------------
    private readonly WfxNative.FsGetFileDelegate?    _fsGetFile;
    private readonly WfxNative.FsPutFileDelegate?    _fsPutFile;
    private readonly WfxNative.FsDeleteFileDelegate? _fsDeleteFile;
    private readonly WfxNative.FsMkDirDelegate?      _fsMkDir;
    private readonly WfxNative.FsRemoveDirDelegate?  _fsRemoveDir;
    private readonly WfxNative.FsRenMovFileDelegate? _fsRenMovFile;

    // ---- optional exports (Unicode) -----------------------------------------
    private readonly WfxNative.FsGetFileWDelegate?    _fsGetFileW;
    private readonly WfxNative.FsPutFileWDelegate?    _fsPutFileW;
    private readonly WfxNative.FsDeleteFileWDelegate? _fsDeleteFileW;
    private readonly WfxNative.FsMkDirWDelegate?      _fsMkDirW;
    private readonly WfxNative.FsRemoveDirWDelegate?  _fsRemoveDirW;
    private readonly WfxNative.FsRenMovFileWDelegate? _fsRenMovFileW;

    // ---- init ---------------------------------------------------------------
    private readonly WfxNative.FsInitDelegate?  _fsInit;
    private readonly WfxNative.FsInitWDelegate? _fsInitW;

    // Keep callback delegates rooted so GC does not collect them while native
    // code may still call back into managed memory.
    private readonly WfxNative.tProgressProc _progressProc;
    private readonly WfxNative.tLogProc      _logProc;
    private readonly WfxNative.tRequestProc  _requestProc;

    public string DllPath { get; }
    public bool HasUnicode { get; }

    public TcWfxAdapter(string dllPath)
    {
        DllPath = dllPath;
        _moduleHandle = NativeLibrary.Load(dllPath);

        // Set up managed callbacks that the plugin may call during its lifetime.
        _progressProc = OnProgress;
        _logProc      = OnLog;
        _requestProc  = OnRequest;

        // Resolve Unicode variants first.
        _fsFindFirstW  = TryGetExport<WfxNative.FsFindFirstWDelegate>("FsFindFirstW");
        _fsFindNextW   = TryGetExport<WfxNative.FsFindNextWDelegate>("FsFindNextW");
        _fsGetFileW    = TryGetExport<WfxNative.FsGetFileWDelegate>("FsGetFileW");
        _fsPutFileW    = TryGetExport<WfxNative.FsPutFileWDelegate>("FsPutFileW");
        _fsDeleteFileW = TryGetExport<WfxNative.FsDeleteFileWDelegate>("FsDeleteFileW");
        _fsMkDirW      = TryGetExport<WfxNative.FsMkDirWDelegate>("FsMkDirW");
        _fsRemoveDirW  = TryGetExport<WfxNative.FsRemoveDirWDelegate>("FsRemoveDirW");
        _fsRenMovFileW = TryGetExport<WfxNative.FsRenMovFileWDelegate>("FsRenMovFileW");
        _fsInitW       = TryGetExport<WfxNative.FsInitWDelegate>("FsInitW");

        HasUnicode = _fsFindFirstW is not null && _fsFindNextW is not null;

        // ANSI variants (always resolve for fallback or mandatory use).
        _fsFindFirst  = TryGetExport<WfxNative.FsFindFirstDelegate>("FsFindFirst");
        _fsFindNext   = TryGetExport<WfxNative.FsFindNextDelegate>("FsFindNext");
        _fsFindClose  = GetRequiredExport<WfxNative.FsFindCloseDelegate>("FsFindClose");
        _fsGetFile    = TryGetExport<WfxNative.FsGetFileDelegate>("FsGetFile");
        _fsPutFile    = TryGetExport<WfxNative.FsPutFileDelegate>("FsPutFile");
        _fsDeleteFile = TryGetExport<WfxNative.FsDeleteFileDelegate>("FsDeleteFile");
        _fsMkDir      = TryGetExport<WfxNative.FsMkDirDelegate>("FsMkDir");
        _fsRemoveDir  = TryGetExport<WfxNative.FsRemoveDirDelegate>("FsRemoveDir");
        _fsRenMovFile = TryGetExport<WfxNative.FsRenMovFileDelegate>("FsRenMovFile");
        _fsInit       = TryGetExport<WfxNative.FsInitDelegate>("FsInit");

        // Call FsInit / FsInitW so the plugin can set itself up.
        // Plugin number 0 is used; a real host would assign unique numbers.
        if (_fsInitW is not null)
            _fsInitW(0, _progressProc, _logProc, _requestProc);
        else
            _fsInit?.Invoke(0, _progressProc, _logProc, _requestProc);
    }

    // -------------------------------------------------------------------------
    // FindFirst / FindNext / FindClose
    // -------------------------------------------------------------------------

    /// <summary>
    /// Begins enumeration of a remote path. Returns entries via <paramref name="entries"/>.
    /// WFX paths use backslash; the caller passes a VFS path that is converted here.
    /// </summary>
    public List<WfxEntry> EnumeratePath(string wfxPath)
    {
        var results = new List<WfxEntry>();

        if (HasUnicode)
            FillEntriesUnicode(wfxPath, results);
        else
            FillEntriesAnsi(wfxPath, results);

        return results;
    }

    private void FillEntriesUnicode(string path, List<WfxEntry> results)
    {
        var data = new WfxNative.WIN32_FIND_DATA_W();
        nint handle = _fsFindFirstW!(path, ref data);
        if (handle == WfxNative.INVALID_HANDLE_VALUE) return;

        try
        {
            do
            {
                var entry = WfxEntry.FromFindDataW(in data, path);
                if (!IsSpecialEntry(entry.Name))
                    results.Add(entry);
            }
            while (_fsFindNextW!(handle, ref data));
        }
        finally
        {
            _fsFindClose(handle);
        }
    }

    private void FillEntriesAnsi(string path, List<WfxEntry> results)
    {
        if (_fsFindFirst is null || _fsFindNext is null) return;

        var data = new WfxNative.WIN32_FIND_DATA_A();
        nint handle = _fsFindFirst(path, ref data);
        if (handle == WfxNative.INVALID_HANDLE_VALUE) return;

        try
        {
            do
            {
                var entry = WfxEntry.FromFindDataA(in data, path);
                if (!IsSpecialEntry(entry.Name))
                    results.Add(entry);
            }
            while (_fsFindNext(handle, ref data));
        }
        finally
        {
            _fsFindClose(handle);
        }
    }

    // -------------------------------------------------------------------------
    // File operations
    // -------------------------------------------------------------------------

    public int GetFile(string remoteName, string localName, int copyFlags)
    {
        var ri = new WfxNative.RemoteInfoStruct();
        if (HasUnicode && _fsGetFileW is not null)
            return _fsGetFileW(remoteName, localName, copyFlags, ref ri);
        if (_fsGetFile is not null)
            return _fsGetFile(remoteName, localName, copyFlags, ref ri);
        return WfxNative.FS_FILE_NOTSUPPORTED;
    }

    public int PutFile(string localName, string remoteName, int copyFlags)
    {
        if (HasUnicode && _fsPutFileW is not null)
            return _fsPutFileW(localName, remoteName, copyFlags);
        if (_fsPutFile is not null)
            return _fsPutFile(localName, remoteName, copyFlags);
        return WfxNative.FS_FILE_NOTSUPPORTED;
    }

    public bool DeleteFile(string remoteName)
    {
        if (HasUnicode && _fsDeleteFileW is not null)
            return _fsDeleteFileW(remoteName);
        return _fsDeleteFile?.Invoke(remoteName) ?? false;
    }

    public bool MkDir(string path)
    {
        if (HasUnicode && _fsMkDirW is not null)
            return _fsMkDirW(path);
        return _fsMkDir?.Invoke(path) ?? false;
    }

    public bool RemoveDir(string remoteName)
    {
        if (HasUnicode && _fsRemoveDirW is not null)
            return _fsRemoveDirW(remoteName);
        return _fsRemoveDir?.Invoke(remoteName) ?? false;
    }

    public int RenMovFile(string oldName, string newName, bool move, bool overwrite)
    {
        var ri = new WfxNative.RemoteInfoStruct();
        if (HasUnicode && _fsRenMovFileW is not null)
            return _fsRenMovFileW(oldName, newName, move, overwrite, ref ri);
        if (_fsRenMovFile is not null)
            return _fsRenMovFile(oldName, newName, move, overwrite, ref ri);
        return WfxNative.FS_FILE_NOTSUPPORTED;
    }

    public bool CanRead   => _fsGetFile   is not null || _fsGetFileW   is not null;
    public bool CanWrite  => _fsPutFile   is not null || _fsPutFileW   is not null;
    public bool CanDelete => _fsDeleteFile is not null || _fsDeleteFileW is not null;
    public bool CanMkDir  => _fsMkDir     is not null || _fsMkDirW     is not null;
    public bool CanRemoveDir => _fsRemoveDir is not null || _fsRemoveDirW is not null;
    public bool CanRename => _fsRenMovFile is not null || _fsRenMovFileW is not null;

    // -------------------------------------------------------------------------
    // Managed callbacks invoked by the native plugin
    // -------------------------------------------------------------------------

    private static int OnProgress(int pluginNr, string sourceName, string targetName, int percentDone)
    {
        // Return 0 to continue; 1 to abort. For now always continue.
        return 0;
    }

    private static void OnLog(int pluginNr, int msgType, string logString)
    {
        // Could forward to ILogger if injected later.
    }

    private static bool OnRequest(int pluginNr, int requestType, string? customTitle, string? customText, IntPtr returnedText, int maxLen)
    {
        // Simplified: reject all interactive requests (password prompts etc.)
        return false;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private T GetRequiredExport<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_moduleHandle, name, out nint addr))
            throw new EntryPointNotFoundException($"WFX plugin '{DllPath}' does not export required function '{name}'.");
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private T? TryGetExport<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_moduleHandle, name, out nint addr))
            return null;
        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static bool IsSpecialEntry(string name) => name is "." or "..";

    // -------------------------------------------------------------------------
    // IDisposable
    // -------------------------------------------------------------------------

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NativeLibrary.Free(_moduleHandle);
    }
}

/// <summary>A single directory entry returned by WFX enumeration.</summary>
internal sealed class WfxEntry
{
    public string  Name           { get; init; } = string.Empty;
    public bool    IsDirectory    { get; init; }
    public long    Size           { get; init; }
    public DateTime LastModified  { get; init; }
    public System.IO.FileAttributes Attributes { get; init; }

    internal static WfxEntry FromFindDataA(in WfxNative.WIN32_FIND_DATA_A d, string parentPath)
        => new()
        {
            Name        = d.cFileName ?? string.Empty,
            IsDirectory = (d.dwFileAttributes & WfxNative.FILE_ATTRIBUTE_DIRECTORY) != 0,
            Size        = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow,
            LastModified = WfxNative.FileTimeToDateTime(d.ftLastWriteTime),
            Attributes  = MapAttributes(d.dwFileAttributes),
        };

    internal static WfxEntry FromFindDataW(in WfxNative.WIN32_FIND_DATA_W d, string parentPath)
        => new()
        {
            Name        = d.cFileName ?? string.Empty,
            IsDirectory = (d.dwFileAttributes & WfxNative.FILE_ATTRIBUTE_DIRECTORY) != 0,
            Size        = ((long)d.nFileSizeHigh << 32) | d.nFileSizeLow,
            LastModified = WfxNative.FileTimeToDateTime(d.ftLastWriteTime),
            Attributes  = MapAttributes(d.dwFileAttributes),
        };

    private static System.IO.FileAttributes MapAttributes(uint raw)
    {
        System.IO.FileAttributes fa = 0;
        if ((raw & WfxNative.FILE_ATTRIBUTE_READONLY)  != 0) fa |= System.IO.FileAttributes.ReadOnly;
        if ((raw & WfxNative.FILE_ATTRIBUTE_HIDDEN)    != 0) fa |= System.IO.FileAttributes.Hidden;
        if ((raw & WfxNative.FILE_ATTRIBUTE_SYSTEM)    != 0) fa |= System.IO.FileAttributes.System;
        if ((raw & WfxNative.FILE_ATTRIBUTE_DIRECTORY) != 0) fa |= System.IO.FileAttributes.Directory;
        if ((raw & WfxNative.FILE_ATTRIBUTE_ARCHIVE)   != 0) fa |= System.IO.FileAttributes.Archive;
        return fa == 0 ? System.IO.FileAttributes.Normal : fa;
    }
}
