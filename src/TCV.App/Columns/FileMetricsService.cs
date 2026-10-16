using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using TCV.Core.Domain;

namespace TCV.App.Columns;

/// <summary>
/// Measures the values the dev columns show but a directory listing does not carry: line count and
/// allocated size on disk (6.19).
/// </summary>
/// <remarks>
/// Both cost real IO, so they are computed only for the rows the panel is showing, only when the
/// column is on, off the UI thread (INV-06), and cached per path+timestamp+size so scrolling and
/// refreshes do not re-read anything. A pass also carries a byte budget for the line counting, so a
/// folder of huge logs cannot turn one scroll into gigabytes of reading. The cache is bounded — a
/// session that walks a big tree must not grow it without limit.
/// </remarks>
public sealed class FileMetricsService
{
    /// <summary>Files larger than this are not counted: LOC on a 50 MB log helps nobody.</summary>
    public const long MaxLineCountBytes = 8L * 1024 * 1024;

    /// <summary>Bytes of file content one pass may read for line counts before it stops counting.</summary>
    public const long DefaultPassBudgetBytes = 64L * 1024 * 1024;

    private const int MaxCacheEntries = 20_000;

    private readonly Dictionary<CacheKey, FileMetrics> _cache = new();
    private readonly Lock _gate = new();

    /// <summary>Identity of a measurement: re-measure when the file changed underneath it.</summary>
    private readonly record struct CacheKey(string Path, long Size, DateTime Modified);

    /// <summary>Measured values; either may be null when it does not apply or could not be read.</summary>
    /// <param name="Lines">Line count, or null for a directory, an oversized file or a binary one.</param>
    /// <param name="SizeOnDisk">Allocated bytes, or null when the platform would not say.</param>
    /// <param name="Deferred">True when the line count was skipped for lack of pass budget: ask again later.</param>
    public readonly record struct FileMetrics(int? Lines, long? SizeOnDisk, bool Deferred = false);

    /// <summary>
    /// Measures one file. Blocking by design — callers run it inside <c>Task.Run</c>; keeping the
    /// synchronous form makes the cache lock trivial and the whole thing testable.
    /// </summary>
    public FileMetrics Measure(string windowsPath, long size, DateTime modified, bool isDirectory) =>
        Measure(windowsPath, size, modified, isDirectory, new ReadBudget(long.MaxValue));

    /// <summary>
    /// As <see cref="Measure(string, long, DateTime, bool)"/>, charging the bytes a line count reads to
    /// <paramref name="budget"/>. A file that no longer fits the budget gets a size on disk but no line
    /// count, and is not cached, so a later pass with a fresh budget can still count it.
    /// </summary>
    public FileMetrics Measure(string windowsPath, long size, DateTime modified, bool isDirectory, ReadBudget budget)
    {
        if (isDirectory || string.IsNullOrEmpty(windowsPath))
        {
            return default;
        }

        var key = new CacheKey(windowsPath, size, modified);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var countable = size <= MaxLineCountBytes;
        var affordable = !countable || budget.TryCharge(size);
        var metrics = new FileMetrics(affordable ? CountLines(windowsPath, size) : null, GetSizeOnDisk(windowsPath, size), Deferred: !affordable);

        if (!affordable)
        {
            return metrics;   // over budget for this pass: report what is cheap, cache nothing
        }

        lock (_gate)
        {
            // A plain clear beats an LRU here: the cost of re-measuring is bounded by what is on
            // screen, and the alternative is bookkeeping on every scroll tick.
            if (_cache.Count >= MaxCacheEntries)
            {
                _cache.Clear();
            }

            _cache[key] = metrics;
        }
        return metrics;
    }

    /// <summary>Drops every cached measurement — used when the user asks for a hard refresh.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    /// <summary>True when the measurement for these exact file attributes is already cached.</summary>
    public bool IsCached(string windowsPath, long size, DateTime modified)
    {
        lock (_gate)
        {
            return _cache.ContainsKey(new CacheKey(windowsPath, size, modified));
        }
    }

    /// <summary>Bytes a pass may still read. Thread-safe; a charge that does not fit is refused, not partly taken.</summary>
    public sealed class ReadBudget(long bytes)
    {
        private long _remaining = bytes;

        public long Remaining => Interlocked.Read(ref _remaining);

        /// <summary>Takes <paramref name="amount"/> from the budget; false when it does not fit.</summary>
        public bool TryCharge(long amount)
        {
            while (true)
            {
                var current = Interlocked.Read(ref _remaining);
                if (amount > current)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _remaining, current - amount, current) == current)
                {
                    return true;
                }
            }
        }
    }

    // ── Line count ────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts lines the way an editor does: LF terminates a line and a file not ending in one
    /// still counts its last line. Returns null for a binary or unreadable file — a byte count
    /// dressed up as "lines" is worse than a blank cell.
    /// </summary>
    public static int? CountLines(string path, long size)
    {
        if (size > MaxLineCountBytes)
        {
            return null;
        }

        if (size == 0)
        {
            return 0;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 64 * 1024, FileOptions.SequentialScan);

            var buffer = new byte[64 * 1024];
            var lines = 0;
            byte last = 0, beforeLast = 0;
            var first = true;
            var wide = false;

            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                if (first)
                {
                    first = false;
                    if (TextProbe.LooksBinary(buffer.AsSpan(0, read)))
                    {
                        return null;
                    }
                    // UTF-16 text is full of NULs by design; its newline is 0A 00 (or 00 0A), not a bare 0A.
                    wide = TextProbe.BomLength(buffer.AsSpan(0, read)) is 2 or 4
                           || buffer.AsSpan(0, Math.Min(read, 64)).Contains((byte)0);
                }

                for (var i = 0; i < read; i++)
                {
                    if (buffer[i] == (byte)'\n')
                    {
                        lines++;
                    }
                }

                beforeLast = read > 1 ? buffer[read - 2] : last;
                last = buffer[read - 1];
            }

            // No trailing newline ⇒ the final partial line still counts. In UTF-16 the file ends in
            // "0A 00" or "00 0A", so the last byte alone would report a phantom extra line.
            var endsWithNewline = last == (byte)'\n' || (wide && last == 0 && beforeLast == (byte)'\n');
            if (!endsWithNewline)
            {
                lines++;
            }

            return lines;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    // ── Size on disk ──────────────────────────────────────────────────────────

    /// <summary>
    /// Bytes the file actually occupies: the allocation NTFS reports, i.e. the logical size rounded up
    /// to whole clusters — or the compressed size for compressed and sparse files, or nothing for a
    /// cloud placeholder that is not downloaded. Falls back to the compressed-size API, rounded to the
    /// cluster, and finally to the logical size when the platform declines to answer.
    /// </summary>
    /// <remarks>
    /// The handle is opened for attribute access only, so a OneDrive placeholder is measured without
    /// being downloaded. Paths go through the <c>\\?\</c> prefix, so a deep tree past 260 characters
    /// works instead of silently falling back to the logical size.
    /// </remarks>
    public static long? GetSizeOnDisk(string path, long logicalSize)
    {
        try
        {
            var extended = ToExtendedPath(path);

            using var handle = CreateFileW(
                extended, FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
            if (!handle.IsInvalid &&
                GetFileInformationByHandleEx(handle, FileStandardInfoClass, out var info, (uint)Marshal.SizeOf<FileStandardInfo>()))
            {
                return info.AllocationSize;
            }

            return CompressedSizeRounded(extended, path, logicalSize);
        }
        catch (EntryPointNotFoundException) { return logicalSize; }
        catch (DllNotFoundException) { return logicalSize; }
    }

    private static long CompressedSizeRounded(string extendedPath, string path, long logicalSize)
    {
        var low = GetCompressedFileSizeW(extendedPath, out var high);
        // The documented failure signal: INVALID_FILE_SIZE *and* a non-zero last error.
        if (low == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0)
        {
            return logicalSize;
        }

        var size = ((long)high << 32) | low;
        if (size == 0)
        {
            return logicalSize > 0 ? logicalSize : 0;
        }

        var cluster = ClusterSize(path);
        return cluster > 0 ? (size + cluster - 1) / cluster * cluster : size;
    }

    private static long ClusterSize(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return 0;
        }

        return GetDiskFreeSpaceW(root, out var sectorsPerCluster, out var bytesPerSector, out _, out _)
            ? (long)sectorsPerCluster * bytesPerSector
            : 0;
    }

    /// <summary>
    /// The <c>\\?\</c> form of an absolute path (<c>\\?\UNC\…</c> for a share), which lifts the 260
    /// character limit. Relative and already-prefixed paths are returned unchanged.
    /// </summary>
    public static string ToExtendedPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path;
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + path[2..];
        }

        return path.Length >= 3 && path[1] == ':' ? @"\\?\" + path : path;
    }

    private const uint FileReadAttributes = 0x0080;
    private const uint FileShareAll = 0x7;                  // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000; // also lets a directory handle open
    private const int FileStandardInfoClass = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle hFile, int fileInformationClass, out FileStandardInfo lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(
        string lpRootPathName, out uint lpSectorsPerCluster, out uint lpBytesPerSector,
        out uint lpNumberOfFreeClusters, out uint lpTotalNumberOfClusters);
}
