using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem.Native;
using TCV.Core.Domain;
using TCV.Core.Exceptions;
using TCV.Core.Interfaces;

namespace TCV.App.FileSystem;

public sealed class WindowsFileSystem : IVirtualFileSystem, IModifiedTimeWriter
{
    private readonly ILogger<WindowsFileSystem> _logger;

    public WindowsFileSystem(ILogger<WindowsFileSystem> logger)
    {
        _logger = logger;
    }

    public string Scheme => "file";
    public string DisplayName => "Local File System";

    public event EventHandler<VfsChangedEventArgs>? Changed;

    // Called by FileWatcher to propagate changes
    internal void RaiseChanged(VfsChangedEventArgs args) => Changed?.Invoke(this, args);

    public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default)
    {
        return await Task.Run(() => ListInternal(path, ct), ct);
    }

    private IReadOnlyList<IFileEntry> ListInternal(VfsPath path, CancellationToken ct)
    {
        var results = new List<FileEntry>();
        var winPath = ToWindowsPath(path);
        var searchPattern = Path.Combine(winPath, "*");

        var handle = Kernel32.FindFirstFile(searchPattern, out var findData);
        if (handle == Kernel32.INVALID_HANDLE_VALUE)
        {
            var error = Marshal.GetLastWin32Error();
            if (error is 2 or 3 or 18 or 267) return results; // not found / no more files / not a directory
            throw new VfsException($"Cannot list directory '{path}' (error {error})", path);
        }

        try
        {
            do
            {
                ct.ThrowIfCancellationRequested();
                var name = findData.cFileName;
                if (name is "." or "..") continue;

                var entryPath = path.Combine(name);
                var attrs = (FileAttributes)findData.dwFileAttributes;
                var isDir = attrs.HasFlag(FileAttributes.Directory);
                var size = isDir ? 0L : (((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow);
                var lastModified = findData.ftLastWriteTime.ToDateTime();

                results.Add(new FileEntry(entryPath, name, isDir, size, lastModified, attrs));
            }
            while (Kernel32.FindNextFile(handle, out findData));
        }
        finally
        {
            Kernel32.FindClose(handle);
        }

        _logger.LogDebug("Listed {Count} entries in {Path}", results.Count, path);
        return results;
    }

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
    {
        return Task.Run<IFileEntry?>(() =>
        {
            var winPath = ToWindowsPath(path);
            var fileInfo = new FileInfo(winPath);
            if (fileInfo.Exists)
                return new FileEntry(path, fileInfo.Name, false, fileInfo.Length,
                    fileInfo.LastWriteTime, fileInfo.Attributes);

            var dirInfo = new DirectoryInfo(winPath);
            if (dirInfo.Exists)
                return new FileEntry(path, dirInfo.Name, true, 0,
                    dirInfo.LastWriteTime, dirInfo.Attributes);

            return null;
        }, ct);
    }

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
    {
        return Task.FromResult<Stream>(File.OpenRead(ToWindowsPath(path)));
    }

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
        return Task.FromResult<Stream>(
            new FileStream(ToWindowsPath(path), mode, FileAccess.Write, FileShare.None));
    }

    public async Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        var src = ToWindowsPath(source);
        var dst = ToWindowsPath(destination);
        var totalBytes = new FileInfo(src).Length;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        const int BufferSize = 1_048_576; // 1 MB
        await using var input = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        await using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true);

        var buffer = new byte[BufferSize];
        long bytesCopied = 0;
        int bytesRead;
        long lastReportTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        long reportIntervalTicks = System.Diagnostics.Stopwatch.Frequency / 10; // 100 ms

        while ((bytesRead = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            bytesCopied += bytesRead;
            if (progress is not null)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (bytesCopied == totalBytes || now - lastReportTicks >= reportIntervalTicks)
                {
                    lastReportTicks = now;
                    progress.Report(new FileProgress(bytesCopied, totalBytes, source.FileName, sw.Elapsed));
                }
            }
        }
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var src = ToWindowsPath(source);
            var dst = ToWindowsPath(destination);

            if (!Directory.Exists(src))
            {
                // A read-only file in the way would fail the replace; the caller already decided to overwrite.
                ClearReadOnlyFlag(dst);
                File.Move(src, dst, overwrite: true);
                return;
            }

            // Directory.Move cannot cross volumes — fall back to copy + delete there.
            if (SameVolume(src, dst))
                Directory.Move(src, dst);
            else
                MoveDirectoryAcrossVolumes(src, dst, ct);
        }, ct);
    }

    /// <summary>Clears the read-only flag of <paramref name="windowsPath"/> if it is a file that has it.</summary>
    private static void ClearReadOnlyFlag(string windowsPath)
    {
        try
        {
            if (!File.Exists(windowsPath)) return;
            var attrs = File.GetAttributes(windowsPath);
            if (attrs.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(windowsPath, attrs & ~FileAttributes.ReadOnly);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The operation that follows reports the real problem.
        }
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(
            Path.GetPathRoot(Path.GetFullPath(a)),
            Path.GetPathRoot(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    private static void MoveDirectoryAcrossVolumes(string src, string dst, CancellationToken ct)
    {
        // Copying through a junction would duplicate its target and then delete only the link. The
        // whole tree is checked before anything is copied, so a refusal leaves no half-copied folder
        // behind.
        if (DirectoryTree.FindDirectoryLink(src) is { } link)
        {
            var shown = Path.GetRelativePath(Path.GetDirectoryName(src) ?? src, link);
            throw new IOException($"'{shown}' is a junction or symbolic link and cannot be moved to another volume.");
        }

        CopyDirectory(src, dst, ct);

        // Only once everything has arrived: a failure midway must not delete what never landed.
        DirectoryTree.Delete(src);
    }

    private static void CopyDirectory(string src, string dst, CancellationToken ct)
    {
        Directory.CreateDirectory(dst);

        foreach (var file in Directory.EnumerateFiles(src))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(dst, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            ct.ThrowIfCancellationRequested();
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)), ct);
        }
    }

    public async Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var winPath = ToWindowsPath(path);
            if (Directory.Exists(winPath))
            {
                // Links inside, or the path itself being one, go as links: what they point to stays.
                if (recursive)
                {
                    DirectoryTree.Delete(winPath);
                }
                else
                {
                    Directory.Delete(winPath);
                }
            }
            else
            {
                ClearReadOnlyFlag(winPath);
                File.Delete(winPath);
            }
        }, ct);
    }

    public async Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        await Task.Run(() => Directory.CreateDirectory(ToWindowsPath(path)), ct);
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            var winPath = ToWindowsPath(path);
            var dir = Path.GetDirectoryName(winPath)!;
            var newPath = Path.Combine(dir, newName);
            if (Directory.Exists(winPath))
            {
                // Directory.Move refuses a rename that only changes case ("src" -> "Src"),
                // so go through a temporary name.
                if (string.Equals(winPath.TrimEnd('\\'), newPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    var temp = Path.Combine(dir, "." + Guid.NewGuid().ToString("N")[..8] + ".tcvtmp");
                    Directory.Move(winPath, temp);
                    Directory.Move(temp, newPath);
                }
                else
                {
                    Directory.Move(winPath, newPath);
                }
            }
            else
            {
                File.Move(winPath, newPath);
            }
        }, ct);
    }

    public bool SupportsOperation(VfsOperation operation) => true;

    public Task<bool> TrySetLastModifiedAsync(VfsPath path, DateTime lastModified, CancellationToken ct = default) =>
        Task.Run(() =>
        {
            try
            {
                File.SetLastWriteTime(ToWindowsPath(path), lastModified);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }, ct);

    public void Dispose() { }

    public static string ToWindowsPath(VfsPath path) =>
        path.Value.Replace('/', Path.DirectorySeparatorChar);
}
