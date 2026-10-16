using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Sync;

/// <summary>How far a running comparison has got.</summary>
/// <param name="Folders">Folder pairs listed so far.</param>
/// <param name="FilesCompared">Files whose bytes were compared (content mode only).</param>
/// <param name="Current">Relative path being worked on.</param>
public sealed record ComparisonProgress(int Folders, int FilesCompared, string Current);

/// <summary>The root of a comparison could not be listed, so there is nothing to compare.</summary>
public sealed class ComparisonFailedException : IOException
{
    public ComparisonFailedException(string side, VfsPath path, Exception inner)
        : base($"Cannot list the {side} folder '{path}': {inner.Message}", inner)
    {
        Side = side;
        Path = path;
    }

    /// <summary>"left" or "right".</summary>
    public string Side { get; }

    /// <summary>The folder that could not be listed.</summary>
    public VfsPath Path { get; }
}

/// <summary>
/// Walks two directory trees over any pair of <see cref="IVirtualFileSystem"/>s and yields
/// comparison rows as they are found (6.23). Works disk-to-disk, disk-to-FTP and between two
/// worktrees alike, because it only ever asks for listings (INV-02).
/// </summary>
/// <remarks>
/// The verdicts themselves come from <see cref="DirectoryComparison"/>, which is pure; this class
/// contributes the traversal. It runs on a pool thread and hands rows over through a channel (INV-06):
/// a remote listing or a content comparison never touches the UI thread.
/// <para>
/// A listing that fails is <b>not</b> an empty folder. Treating it as one made every file on the other
/// side look "only here", pre-ticked for copying over whatever really was there. The root failing
/// aborts the comparison; a subfolder failing yields an <see cref="ComparisonStatus.Unreadable"/> row
/// and its contents are left out.
/// </para>
/// </remarks>
public sealed class DirectoryComparer
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

    private readonly IVirtualFileSystem _leftFs;
    private readonly IVirtualFileSystem _rightFs;
    private readonly ComparisonOptions _options;
    private int _folders;
    private int _filesCompared;
    private int _unreadable;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastReport;

    public DirectoryComparer(
        IVirtualFileSystem leftFs, IVirtualFileSystem rightFs, ComparisonOptions? options = null)
    {
        _leftFs = leftFs;
        _rightFs = rightFs;
        _options = options ?? new ComparisonOptions();
    }

    /// <summary>Optional progress sink, called from the comparing thread a few times a second.</summary>
    public IProgress<ComparisonProgress>? Progress { get; set; }

    /// <summary>Folders that could not be listed on one side or the other.</summary>
    public int UnreadableFolders => Volatile.Read(ref _unreadable);

    public async IAsyncEnumerable<ComparisonRow> CompareAsync(
        VfsPath leftRoot,
        VfsPath rightRoot,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<ComparisonRow>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var producer = Task.Run(async () =>
        {
            try
            {
                await EnsureRootAsync(_leftFs, leftRoot, "left", linked.Token).ConfigureAwait(false);
                await EnsureRootAsync(_rightFs, rightRoot, "right", linked.Token).ConfigureAwait(false);
                await CompareDirectoryAsync(leftRoot, rightRoot, string.Empty, 0, null, channel.Writer, linked.Token)
                    .ConfigureAwait(false);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                channel.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        try
        {
            await foreach (var row in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return row;
            }
        }
        finally
        {
            linked.Cancel();
            await producer.ConfigureAwait(false);
        }
    }

    private async Task CompareDirectoryAsync(
        VfsPath left,
        VfsPath right,
        string prefix,
        int depth,
        ComparisonRow? container,
        ChannelWriter<ComparisonRow> writer,
        CancellationToken ct)
    {
        if (depth > _options.MaxDepth)
        {
            return;
        }

        IReadOnlyList<IFileEntry> leftEntries;
        IReadOnlyList<IFileEntry> rightEntries;
        try
        {
            leftEntries = await ListAsync(_leftFs, left, "left", ct).ConfigureAwait(false);
            rightEntries = await ListAsync(_rightFs, right, "right", ct).ConfigureAwait(false);
        }
        catch (ComparisonFailedException) when (container is not null)
        {
            // A subfolder: say so in its row instead of pretending it is empty.
            Interlocked.Increment(ref _unreadable);
            await writer.WriteAsync(container with { Status = ComparisonStatus.Unreadable }, ct).ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref _folders);
        ReportProgress(prefix);

        // Identical pairs must reach RefineByContentAsync (equal time and size can still differ in bytes);
        // they are dropped after it, below, unless the caller wants them.
        var levelOptions = _options.CompareContent ? _options with { IncludeSame = true } : _options;
        var rows = DirectoryComparison.CompareLevel(leftEntries, rightEntries, levelOptions, prefix);

        foreach (var original in rows)
        {
            ct.ThrowIfCancellationRequested();

            if (original.IsDirectory && _options.SkipDirectories.Contains(original.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = await RefineByContentAsync(original, ct).ConfigureAwait(false);

            // A directory present on both sides is a container, not a difference: report it only
            // when the caller asked for identical rows, and descend either way.
            var isPairedDirectory = row.IsDirectory && row is { Left: not null, Right: not null };

            if (isPairedDirectory)
            {
                if (_options.IncludeSame)
                {
                    await writer.WriteAsync(row, ct).ConfigureAwait(false);
                }

                if (_options.Recursive)
                {
                    await CompareDirectoryAsync(
                        row.Left!.Path, row.Right!.Path, row.RelativePath, depth + 1, row, writer, ct).ConfigureAwait(false);
                }

                continue;
            }

            if (row.Status == ComparisonStatus.Same && !_options.IncludeSame)
            {
                continue;
            }

            if (row.IsDirectory && _options.Recursive)
            {
                // A directory only one side has: list its contents so the sync copies the whole
                // subtree rather than an empty folder.
                await WriteOneSidedAsync(row, depth + 1, writer, ct).ConfigureAwait(false);
                continue;
            }

            await writer.WriteAsync(row, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes a folder that exists on one side only, followed by everything under it with the same
    /// status. The folder is listed <b>first</b>: if that fails the row says "unreadable" instead of
    /// offering an empty copy.
    /// </summary>
    private async Task WriteOneSidedAsync(
        ComparisonRow parent, int depth, ChannelWriter<ComparisonRow> writer, CancellationToken ct)
    {
        var onLeft = parent.Left is not null;
        var fs = onLeft ? _leftFs : _rightFs;
        var root = onLeft ? parent.Left!.Path : parent.Right!.Path;

        IReadOnlyList<IFileEntry> children;
        try
        {
            children = depth > _options.MaxDepth
                ? Array.Empty<IFileEntry>()
                : await ListAsync(fs, root, onLeft ? "left" : "right", ct).ConfigureAwait(false);
        }
        catch (ComparisonFailedException)
        {
            Interlocked.Increment(ref _unreadable);
            await writer.WriteAsync(parent with { Status = ComparisonStatus.Unreadable }, ct).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(parent, ct).ConfigureAwait(false);

        foreach (var entry in children)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.IsDirectory && _options.SkipDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var row = new ComparisonRow(
                parent.RelativePath + "/" + entry.Name,
                onLeft ? entry : null,
                onLeft ? null : entry,
                parent.Status,
                entry.IsDirectory);

            if (entry.IsDirectory && _options.Recursive)
            {
                await WriteOneSidedAsync(row, depth + 1, writer, ct).ConfigureAwait(false);
            }
            else
            {
                await writer.WriteAsync(row, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// With <see cref="ComparisonOptions.CompareContent"/>, settles a pair of equal-size files by their
    /// bytes: identical content is "same" whatever the timestamps say, and equal timestamps with
    /// different bytes are "different". A file that cannot be read keeps its timestamp verdict.
    /// </summary>
    private async Task<ComparisonRow> RefineByContentAsync(ComparisonRow row, CancellationToken ct)
    {
        if (!_options.CompareContent || row.IsDirectory || row.Left is not { IsDirectory: false } l
            || row.Right is not { IsDirectory: false } r || l.Size != r.Size)
        {
            return row;
        }

        try
        {
            var equal = await ContentComparer.AreEqualAsync(_leftFs, l.Path, _rightFs, r.Path, ct).ConfigureAwait(false);
            Interlocked.Increment(ref _filesCompared);
            ReportProgress(row.RelativePath);

            if (equal)
            {
                return row with { Status = ComparisonStatus.Same };
            }

            return row.Status == ComparisonStatus.Same ? row with { Status = ComparisonStatus.Different } : row;
        }
        catch (OperationCanceledException) { throw; }
        catch { return row; }
    }

    private void ReportProgress(string current)
    {
        if (Progress is null || _clock.Elapsed - _lastReport < ProgressInterval)
        {
            return;
        }

        _lastReport = _clock.Elapsed;
        Progress.Report(new ComparisonProgress(Volatile.Read(ref _folders), Volatile.Read(ref _filesCompared), current));
    }

    /// <summary>
    /// The compared roots must exist and be folders. Some file systems (the local disk) answer a
    /// listing of a missing folder with an empty list — fine for a folder that vanished mid-walk, but
    /// for the root it would compare the other side against "nothing" and offer to copy all of it.
    /// </summary>
    private static async Task EnsureRootAsync(IVirtualFileSystem fs, VfsPath root, string side, CancellationToken ct)
    {
        IFileEntry? entry;
        try
        {
            entry = await fs.GetEntryAsync(root, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ComparisonFailedException(side, root, ex);
        }

        // "/" is the root of a mount (archive, server); not every file system describes its own root.
        if (entry is null ? root.Value != "/" : !entry.IsDirectory)
        {
            throw new ComparisonFailedException(side, root, new DirectoryNotFoundException("The folder does not exist."));
        }
    }

    private static async Task<IReadOnlyList<IFileEntry>> ListAsync(
        IVirtualFileSystem fs, VfsPath path, string side, CancellationToken ct)
    {
        try
        {
            return await fs.ListAsync(path, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ComparisonFailedException(side, path, ex);
        }
    }
}

/// <summary>Byte-for-byte comparison of two files on any pair of file systems.</summary>
public static class ContentComparer
{
    private const int BufferSize = 64 * 1024;

    /// <summary>
    /// True when both files hold the same bytes. Streams both in chunks and stops at the first
    /// difference, so unequal files are cheap; equal ones cost one read of each.
    /// </summary>
    public static async Task<bool> AreEqualAsync(
        IVirtualFileSystem leftFs, VfsPath left, IVirtualFileSystem rightFs, VfsPath right, CancellationToken ct)
    {
        await using var a = await leftFs.OpenReadAsync(left, ct).ConfigureAwait(false);
        await using var b = await rightFs.OpenReadAsync(right, ct).ConfigureAwait(false);

        var bufferA = new byte[BufferSize];
        var bufferB = new byte[BufferSize];

        while (true)
        {
            var readA = await FillAsync(a, bufferA, ct).ConfigureAwait(false);
            var readB = await FillAsync(b, bufferB, ct).ConfigureAwait(false);
            if (readA != readB)
            {
                return false;
            }

            if (readA == 0)
            {
                return true;
            }

            if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
            {
                return false;
            }
        }
    }

    /// <summary>Reads until the buffer is full or the stream ends — a stream may return short reads mid-file.</summary>
    private static async Task<int> FillAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }
        return total;
    }
}
