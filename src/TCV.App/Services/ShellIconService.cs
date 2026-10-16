using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using TCV.App.FileSystem.Native;

namespace TCV.App.Services;

/// <summary>Loads and caches small shell icons by file extension/type.</summary>
public static class ShellIconService
{
    private static readonly Dictionary<string, BitmapSource?> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly object _lock = new();

    public static BitmapSource? GetSmallIcon(string extension, bool isDirectory)
    {
        var key = isDirectory ? "\x00[DIR]" : (extension.Length > 0 ? extension : "\x00[FILE]");

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var icon = LoadIcon(extension, isDirectory, small: true);

        lock (_lock)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    public static BitmapSource? GetLargeIcon(string extension, bool isDirectory)
    {
        var key = (isDirectory ? "\x00[DIR]L" : (extension.Length > 0 ? extension + "L" : "\x00[FILE]L"));

        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var icon = LoadIcon(extension, isDirectory, small: false);

        lock (_lock)
        {
            _cache[key] = icon;
        }

        return icon;
    }

    /// <summary>Returns the file type description string (e.g. "Text Document").</summary>
    public static string GetTypeName(string extension, bool isDirectory)
    {
        if (isDirectory) return "Folder";

        var sfi = new Shell32.SHFILEINFO();
        var fakeName = extension.Length > 0 ? "file." + extension.TrimStart('.') : "file";
        var attrs = Shell32.FILE_ATTRIBUTE_NORMAL;

        Shell32.SHGetFileInfo(
            fakeName, attrs, ref sfi,
            (uint)Marshal.SizeOf(sfi),
            Shell32.SHGFI_TYPENAME | Shell32.SHGFI_USEFILEATTRIBUTES);

        return sfi.szTypeName ?? string.Empty;
    }

    private static BitmapSource? LoadIcon(string extension, bool isDirectory, bool small)
    {
        var sfi = new Shell32.SHFILEINFO();
        var fakeName = isDirectory ? "folder" : ("file." + extension.TrimStart('.'));
        var attrs = isDirectory ? Shell32.FILE_ATTRIBUTE_DIRECTORY : Shell32.FILE_ATTRIBUTE_NORMAL;
        var flags = Shell32.SHGFI_ICON | Shell32.SHGFI_USEFILEATTRIBUTES
                    | (small ? Shell32.SHGFI_SMALLICON : Shell32.SHGFI_LARGEICON);

        var result = Shell32.SHGetFileInfo(fakeName, attrs, ref sfi,
            (uint)Marshal.SizeOf(sfi), flags);

        if (result == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
            return null;

        try
        {
            var bmp = Imaging.CreateBitmapSourceFromHIcon(
                sfi.hIcon, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            Shell32.DestroyIcon(sfi.hIcon);
        }
    }
}
