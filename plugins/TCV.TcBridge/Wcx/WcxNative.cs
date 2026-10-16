using System.Runtime.InteropServices;

namespace TCV.TcBridge.Wcx;

/// <summary>
/// P/Invoke declarations for the Total Commander WCX (Packer plugin) API.
/// WCX plugins expose archive formats (ZIP-like) as virtual file systems.
/// </summary>
internal static class WcxNative
{
    // -------------------------------------------------------------------------
    // OpenArchive mode flags
    // -------------------------------------------------------------------------

    public const int PK_OM_LIST    = 0;
    public const int PK_OM_EXTRACT = 1;

    // -------------------------------------------------------------------------
    // ProcessFile operation codes
    // -------------------------------------------------------------------------

    public const int PK_SKIP    = 0;  // skip this file
    public const int PK_TEST    = 1;  // test integrity only
    public const int PK_EXTRACT = 2;  // extract to disk

    // -------------------------------------------------------------------------
    // PackFiles flags
    // -------------------------------------------------------------------------

    public const int PK_PACK_MOVE_FILES    = 1;
    public const int PK_PACK_SAVE_PATHS    = 2;
    public const int PK_PACK_ENCRYPT       = 4;

    // -------------------------------------------------------------------------
    // Error codes returned by WCX functions
    // -------------------------------------------------------------------------

    public const int E_SUCCESS          = 0;
    public const int E_END_ARCHIVE      = 10;  // no more files in archive
    public const int E_NO_MEMORY        = 11;
    public const int E_BAD_DATA         = 12;
    public const int E_BAD_ARCHIVE      = 13;
    public const int E_UNKNOWN_FORMAT   = 14;
    public const int E_EOPEN            = 15;  // cannot open archive
    public const int E_ECREATE          = 16;  // cannot create archive
    public const int E_ECLOSE           = 17;
    public const int E_EREAD            = 18;
    public const int E_EWRITE           = 19;
    public const int E_SMALL_BUF        = 20;
    public const int E_EABORTED         = 21;
    public const int E_NO_FILES         = 22;
    public const int E_TOO_MANY_FILES   = 23;
    public const int E_NOT_SUPPORTED    = 24;

    // -------------------------------------------------------------------------
    // Structures
    // -------------------------------------------------------------------------

    /// <summary>Passed to OpenArchive to control how the archive is opened.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct tOpenArchiveData
    {
        [MarshalAs(UnmanagedType.LPStr)]
        public string ArcName;
        public int    OpenMode;
        public int    OpenResult;   // out: error code
        [MarshalAs(UnmanagedType.LPStr)]
        public string? CmtBuf;
        public int    CmtBufSize;
        public int    CmtSize;
        public int    CmtState;
    }

    /// <summary>Unicode variant of tOpenArchiveData.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct tOpenArchiveDataW
    {
        [MarshalAs(UnmanagedType.LPWStr)]
        public string ArcName;
        public int    OpenMode;
        public int    OpenResult;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? CmtBuf;
        public int    CmtBufSize;
        public int    CmtSize;
        public int    CmtState;
    }

    /// <summary>Filled by ReadHeader to describe the next file in the archive.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct tHeaderData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ArcName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;
        public int    Flags;
        public int    PackSize;
        public int    UnpSize;
        public int    HostOS;
        public int    FileCRC;
        public int    FileTime;   // DOS date/time format
        public int    UnpVer;
        public int    Method;
        public int    FileAttr;
        [MarshalAs(UnmanagedType.LPStr)]
        public string? CmtBuf;
        public int    CmtBufSize;
        public int    CmtSize;
        public int    CmtState;
    }

    /// <summary>Unicode variant with extended (64-bit) sizes.</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct tHeaderDataExW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string ArcName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 1024)]
        public string FileName;
        public int    Flags;
        public uint   PackSizeLow;
        public uint   PackSizeHigh;
        public uint   UnpSizeLow;
        public uint   UnpSizeHigh;
        public int    HostOS;
        public int    FileCRC;
        public int    FileTime;
        public int    UnpVer;
        public int    Method;
        public int    FileAttr;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? CmtBuf;
        public int    CmtBufSize;
        public int    CmtSize;
        public int    CmtState;
    }

    // -------------------------------------------------------------------------
    // Callback delegate types
    // -------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int tChangeVolProc([MarshalAs(UnmanagedType.LPStr)] string ArcName, int Mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int tProcessDataProc([MarshalAs(UnmanagedType.LPStr)] string FileName, int Size);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int tChangeVolProcW([MarshalAs(UnmanagedType.LPWStr)] string ArcName, int Mode);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int tProcessDataProcW([MarshalAs(UnmanagedType.LPWStr)] string FileName, int Size);

    // -------------------------------------------------------------------------
    // Delegate types for dynamically-loaded WCX exports
    // -------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate nint OpenArchiveDelegate(ref tOpenArchiveData ArchiveData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate nint OpenArchiveWDelegate(ref tOpenArchiveDataW ArchiveData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int ReadHeaderDelegate(nint hArcData, ref tHeaderData HeaderData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int ReadHeaderExWDelegate(nint hArcData, ref tHeaderDataExW HeaderData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int ProcessFileDelegate(
        nint hArcData,
        int  Operation,
        [MarshalAs(UnmanagedType.LPStr)] string? DestPath,
        [MarshalAs(UnmanagedType.LPStr)] string? DestName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    public delegate int ProcessFileWDelegate(
        nint hArcData,
        int  Operation,
        [MarshalAs(UnmanagedType.LPWStr)] string? DestPath,
        [MarshalAs(UnmanagedType.LPWStr)] string? DestName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate int CloseArchiveDelegate(nint hArcData);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SetChangeVolProcDelegate(nint hArcData, tChangeVolProc pChangeVolProc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SetProcessDataProcDelegate(nint hArcData, tProcessDataProc pProcessDataProc);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SetChangeVolProcWDelegate(nint hArcData, tChangeVolProcW pChangeVolProcW);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void SetProcessDataProcWDelegate(nint hArcData, tProcessDataProcW pProcessDataProcW);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int PackFilesDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string PackedFile,
        [MarshalAs(UnmanagedType.LPStr)] string? SubPath,
        [MarshalAs(UnmanagedType.LPStr)] string SrcPath,
        [MarshalAs(UnmanagedType.LPStr)] string AddList,
        int Flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate int DeleteFilesDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string PackedFile,
        [MarshalAs(UnmanagedType.LPStr)] string DeleteList);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool CanYouHandleThisFileDelegate([MarshalAs(UnmanagedType.LPStr)] string FileName);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public delegate bool CanYouHandleThisFileWDelegate([MarshalAs(UnmanagedType.LPWStr)] string FileName);

    // -------------------------------------------------------------------------
    // Helper: convert DOS date/time integer to DateTime
    // -------------------------------------------------------------------------

    public static DateTime DosDateTimeToDateTime(int dosDateTime)
    {
        try
        {
            int time = dosDateTime & 0xFFFF;
            int date = (dosDateTime >> 16) & 0xFFFF;
            int sec  = (time & 0x1F) * 2;
            int min  = (time >> 5) & 0x3F;
            int hr   = (time >> 11) & 0x1F;
            int day  = date & 0x1F;
            int mon  = (date >> 5) & 0x0F;
            int yr   = ((date >> 9) & 0x7F) + 1980;
            if (day < 1 || mon < 1 || mon > 12) return DateTime.MinValue;
            return new DateTime(yr, mon, day, hr, min, sec);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
