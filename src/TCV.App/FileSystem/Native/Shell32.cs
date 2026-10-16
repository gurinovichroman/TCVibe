using System.Runtime.InteropServices;

namespace TCV.App.FileSystem.Native;

internal static class Shell32
{
    internal const int SW_SHOWNORMAL = 1;
    internal const uint FO_DELETE = 0x0003;
    internal const ushort FOF_ALLOWUNDO = 0x0040;
    internal const ushort FOF_NOCONFIRMATION = 0x0010;
    internal const ushort FOF_SILENT = 0x0004;
    // With NOCONFIRMATION the shell would still delete for good what does not fit the bin, silently.
    // This asks first, which is the only warning a permanent delete gets on that path.
    internal const ushort FOF_WANTNUKEWARNING = 0x4000;

    // SHGetFileInfo flags
    internal const uint SHGFI_ICON = 0x000000100;
    internal const uint SHGFI_SMALLICON = 0x000000001;
    internal const uint SHGFI_LARGEICON = 0x000000000;
    internal const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    internal const uint SHGFI_TYPENAME = 0x000000400;
    internal const uint SHGFI_DISPLAYNAME = 0x000000200;

    internal const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbSizeFileInfo,
        uint uFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr ShellExecute(
        IntPtr hwnd,
        string? lpOperation,
        string lpFile,
        string? lpParameters,
        string? lpDirectory,
        int nShowCmd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    internal static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    internal const uint SEE_MASK_INVOKEIDLIST = 0x0000000C;
    internal const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SHELLEXECUTEINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO pExecInfo);

    /// <summary>
    /// Opens <paramref name="path"/> for <em>editing</em>: the type's "edit" verb, else Notepad (not
    /// every type registers one). Never the default verb — for a script that is "run". False when
    /// neither could be started.
    /// </summary>
    internal static bool OpenForEditing(string path)
    {
        if (ShellExecute(IntPtr.Zero, "edit", path, null, null, SW_SHOWNORMAL).ToInt64() > 32)
        {
            return true;
        }

        try
        {
            // An argument list, not a command line typed by hand (TRAP-41).
            var notepad = new System.Diagnostics.ProcessStartInfo("notepad.exe") { UseShellExecute = false };
            notepad.ArgumentList.Add(path);
            System.Diagnostics.Process.Start(notepad)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the standard Windows Properties dialog for the given file or folder path.
    /// </summary>
    internal static void ShowProperties(string path, IntPtr hwnd)
    {
        var info = new SHELLEXECUTEINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<SHELLEXECUTEINFO>(),
            fMask  = SEE_MASK_INVOKEIDLIST,
            hwnd   = hwnd,
            lpVerb = "properties",
            lpFile = path,
            nShow  = SW_SHOWNORMAL,
        };
        ShellExecuteEx(ref info);
    }
}
