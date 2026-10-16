using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TCV.App.FileSystem.Native;

/// <summary>
/// Tells whether two Windows paths name the same file or folder on disk. Two different spellings can:
/// a <c>subst</c> drive and its real folder, a mapped drive and its UNC path, a junction and its
/// target. Comparing the strings misses that, and a move that copies "across volumes" and then deletes
/// its source would delete the only copy.
/// </summary>
public static class FileIdentity
{
    /// <summary>
    /// True when both paths exist and resolve to the same object (volume serial number plus file id).
    /// False when either cannot be opened: not knowing is not the same as knowing they are one.
    /// </summary>
    public static bool IsSameItem(string pathA, string pathB)
    {
        var a = TryGetId(pathA);
        if (a is null) return false;
        var b = TryGetId(pathB);
        return b is not null && a.Value == b.Value;
    }

    private static (uint Volume, uint High, uint Low)? TryGetId(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // CreateFileW does not get the BCL's long-path handling for free.
        if (full.Length >= 248 && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;

        // Backup semantics so folders open too; no share restriction so an open file is no obstacle.
        using var handle = CreateFileW(full, FileReadAttributes,
            FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        if (!GetFileInformationByHandle(handle, out var info)) return null;
        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
}
