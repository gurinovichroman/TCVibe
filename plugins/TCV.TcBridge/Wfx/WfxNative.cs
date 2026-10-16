using System.Runtime.InteropServices;

namespace TCV.TcBridge.Wfx;

/// <summary>
/// P/Invoke declarations for the Total Commander WFX (File System plugin) API.
/// TC WFX plugins are Windows DLLs that export C-style functions.
/// Supports both ANSI (FsFindFirst) and Unicode (FsFindFirstW) variants.
/// </summary>
internal static class WfxNative
{
    // -------------------------------------------------------------------------
    // Constants
    // -------------------------------------------------------------------------

    public const nint INVALID_HANDLE_VALUE = -1;

    // FsGetFile / FsPutFile copy flags
    public const int FS_COPYFLAGS_OVERWRITE        = 0x0001;
    public const int FS_COPYFLAGS_RESUME           = 0x0002;
    public const int FS_COPYFLAGS_MOVE             = 0x0004;
    public const int FS_COPYFLAGS_EXISTS_SAMECASE  = 0x0008;
    public const int FS_COPYFLAGS_EXISTS_DIFFERENTCASE = 0x0010;

    // FsGetFile / FsPutFile return values
    public const int FS_FILE_OK         = 0;
    public const int FS_FILE_EXISTS     = 1;
    public const int FS_FILE_NOTFOUND   = 2;
    public const int FS_FILE_READERROR  = 3;
    public const int FS_FILE_WRITEERROR = 4;
    public const int FS_FILE_USERABORT  = 5;
    public const int FS_FILE_NOTSUPPORTED = 6;
    public const int FS_FILE_EXISTSRESUMEALLOWED = 7;

    // FsRenMovFile return values
    public const int FS_FILE_CREATED      = 0; // same as FS_FILE_OK
    public const int FS_FILE_RENMOV_OK    = 0;

    // FsMkDir / FsRemoveDir return values
    public const int FS_ERR_OK        = 0;
    public const int FS_ERR_EXISTS    = 1;
    public const int FS_ERR_NOTFOUND  = 2;

    // WIN32_FIND_DATA attribute flags (subset)
    public const uint FILE_ATTRIBUTE_READONLY  = 0x00000001;
    public const uint FILE_ATTRIBUTE_HIDDEN    = 0x00000002;
    public const uint FILE_ATTRIBUTE_SYSTEM    = 0x00000004;
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    public const uint FILE_ATTRIBUTE_ARCHIVE   = 0x00000020;

    // -------------------------------------------------------------------------
    // WIN32_FIND_DATA structures
    // -------------------------------------------------------------------------

    /// <summary>ANSI version of WIN32_FIND_DATA.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct WIN32_FIND_DATA_A
    {
        public uint   dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint   nFileSizeHigh;
        public uint   nFileSizeLow;
        public uint   dwReserved0;
        public uint   dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    /// <summary>Unicode version of WIN32_FIND_DATA.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WIN32_FIND_DATA_W
    {
        public uint   dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint   nFileSizeHigh;
        public uint   nFileSizeLow;
        public uint   dwReserved0;
        public uint   dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    /// <summary>RemoteInfoStruct passed to FsGetFile / FsPutFile / FsRenMovFile.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct RemoteInfoStruct
    {
        public long   SizeLow;   // low 32 bits of file size (as signed, TC convention)
        public long   SizeHigh;  // high 32 bits
        public uint   LastWriteTime; // DOS date/time
        public uint   Attr;
    }

    // -------------------------------------------------------------------------
    // Callback delegate types
    // -------------------------------------------------------------------------

    /// <summary>Progress callback: return non-zero to abort.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int tProgressProc(int PluginNr, string SourceName, string TargetName, int PercentDone);

    /// <summary>Log callback for messages.</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void tLogProc(int PluginNr, int MsgType, string LogString);

    /// <summary>Request callback (password dialogs, etc.).</summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool tRequestProc(int PluginNr, int RequestType, string? CustomTitle, string? CustomText, IntPtr ReturnedText, int MaxLen);

    // -------------------------------------------------------------------------
    // Delegate types for dynamically-loaded WFX exports (ANSI)
    // -------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate nint FsInitDelegate(int PluginNr, tProgressProc pProgressProc, tLogProc pLogProc, tRequestProc pRequestProc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate nint FsFindFirstDelegate([MarshalAs(UnmanagedType.LPStr)] string Path, ref WIN32_FIND_DATA_A FindData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsFindNextDelegate(nint Hdl, ref WIN32_FIND_DATA_A FindData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int FsFindCloseDelegate(nint Hdl);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int FsGetFileDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string RemoteName,
        [MarshalAs(UnmanagedType.LPStr)] string LocalName,
        int CopyFlags,
        ref RemoteInfoStruct ri);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int FsPutFileDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string LocalName,
        [MarshalAs(UnmanagedType.LPStr)] string RemoteName,
        int CopyFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsDeleteFileDelegate([MarshalAs(UnmanagedType.LPStr)] string RemoteName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsMkDirDelegate([MarshalAs(UnmanagedType.LPStr)] string Path);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsRemoveDirDelegate([MarshalAs(UnmanagedType.LPStr)] string RemoteName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int FsRenMovFileDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string OldName,
        [MarshalAs(UnmanagedType.LPStr)] string NewName,
        [MarshalAs(UnmanagedType.Bool)] bool Move,
        [MarshalAs(UnmanagedType.Bool)] bool OverWrite,
        ref RemoteInfoStruct ri);

    // -------------------------------------------------------------------------
    // Delegate types for dynamically-loaded WFX exports (Unicode)
    // -------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate nint FsInitWDelegate(int PluginNr, tProgressProc pProgressProc, tLogProc pLogProc, tRequestProc pRequestProc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate nint FsFindFirstWDelegate([MarshalAs(UnmanagedType.LPWStr)] string Path, ref WIN32_FIND_DATA_W FindData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsFindNextWDelegate(nint Hdl, ref WIN32_FIND_DATA_W FindData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int FsGetFileWDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string RemoteName,
        [MarshalAs(UnmanagedType.LPWStr)] string LocalName,
        int CopyFlags,
        ref RemoteInfoStruct ri);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int FsPutFileWDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string LocalName,
        [MarshalAs(UnmanagedType.LPWStr)] string RemoteName,
        int CopyFlags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsDeleteFileWDelegate([MarshalAs(UnmanagedType.LPWStr)] string RemoteName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsMkDirWDelegate([MarshalAs(UnmanagedType.LPWStr)] string Path);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool FsRemoveDirWDelegate([MarshalAs(UnmanagedType.LPWStr)] string RemoteName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int FsRenMovFileWDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string OldName,
        [MarshalAs(UnmanagedType.LPWStr)] string NewName,
        [MarshalAs(UnmanagedType.Bool)] bool Move,
        [MarshalAs(UnmanagedType.Bool)] bool OverWrite,
        ref RemoteInfoStruct ri);

    // -------------------------------------------------------------------------
    // Helper: convert FILETIME pair to DateTime
    // -------------------------------------------------------------------------

    public static DateTime FileTimeToDateTime(System.Runtime.InteropServices.ComTypes.FILETIME ft)
    {
        long ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        if (ticks <= 0) return DateTime.MinValue;
        try { return DateTime.FromFileTimeUtc(ticks).ToLocalTime(); }
        catch { return DateTime.MinValue; }
    }
}
