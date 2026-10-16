using System.IO;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.FileSystem;

/// <summary>
/// Adds up what a folder holds: every file under it, at any depth. It walks the panel's own file system
/// (INV-02), so a folder is measured the same way on the disk, in an archive and on a server.
/// </summary>
public static class FolderSizeCounter
{
    /// <summary>Deeper than any real tree; a cycle the link test missed stops here instead of running forever.</summary>
    public const int MaxDepth = 256;

    /// <summary>What a folder holds. <paramref name="Complete"/> is false when a subfolder could not be read.</summary>
    public readonly record struct Result(long Bytes, int Files, int Folders, bool Complete);

    /// <summary>Without a better test, any reparse point is a link: nothing else on a non-disk file system has one.</summary>
    public static bool ReparsePointIsLink(IFileEntry entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    /// <summary>
    /// Measures <paramref name="folder"/>. A folder <paramref name="isLink"/> calls a link (a junction, a
    /// symbolic link) is counted as a folder but not entered: its content lives elsewhere, and following
    /// it counts that content twice or loops (TRAP-50, TRAP-64). A folder that cannot be listed is
    /// skipped and the result is marked incomplete; the rest still counts.
    /// </summary>
    public static async Task<Result> CountAsync(
        IVirtualFileSystem fs, VfsPath folder, Func<IFileEntry, bool>? isLink, CancellationToken ct)
    {
        isLink ??= ReparsePointIsLink;

        long bytes = 0;
        var files = 0;
        var folders = 0;
        var complete = true;

        var pending = new Stack<(VfsPath Path, int Depth)>();
        pending.Push((folder, 0));
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (path, depth) = pending.Pop();

            IReadOnlyList<IFileEntry> entries;
            try
            {
                entries = await fs.ListAsync(path, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A listing that timed out (an HTTP client's TaskCanceledException on a cloud drive) is
                // an unreadable folder too, not the walk being stopped.
                complete = false;
                continue;
            }

            foreach (var entry in entries)
            {
                if (!entry.IsDirectory)
                {
                    bytes += entry.Size;
                    files++;
                    continue;
                }

                folders++;
                if (isLink(entry))
                {
                    continue;
                }

                if (depth + 1 >= MaxDepth)
                {
                    complete = false;
                    continue;
                }

                pending.Push((entry.Path, depth + 1));
            }
        }

        return new Result(bytes, files, folders, complete);
    }
}
