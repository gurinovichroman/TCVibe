using System.IO;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.Domain;

namespace TCV.App.Sync;

/// <summary>
/// What a synchronisation does to an archive it writes into: new folders and every file in one rewrite,
/// all or nothing. An archive takes no single file through its file system, and going file by file would
/// rewrite it once for each. Files that are not on the disk are first brought to a scratch folder, keeping
/// their times: the archive stores what the disk says, and the two sides must settle on the next comparison.
/// </summary>
internal sealed class SyncIntoArchive
{
    private readonly ArchiveFileSystem _archive;
    private readonly SyncEndpoint _source;
    private readonly IReadOnlyList<(SyncPlanItem Item, string EntryName)> _folders;
    private readonly IReadOnlyList<(SyncPlanItem Item, string EntryName)> _files;

    public SyncIntoArchive(ArchiveFileSystem archive, IReadOnlyList<SyncPlanItem> items, SyncEndpoint source, SyncEndpoint target)
    {
        _archive = archive;
        _source = source;

        // A file that is there is replaced under the name it has, whatever the case of the other side's.
        (SyncPlanItem, string) Named(SyncPlanItem item) =>
            (item, ArchiveFileSystem.NormalizePath(item.Target?.Path.Value ?? target.Combine(item.Row.RelativePath)));

        _folders = items.Where(i => i.Row.IsDirectory && i.Target is null).Select(Named).ToList();
        _files = items.Where(i => !i.Row.IsDirectory).Select(Named).ToList();
    }

    public int NewFolders => _folders.Count;

    public int Files => _files.Count;

    public int Replaced => _files.Count(f => f.Item.Target is not null);

    /// <summary>The line in the log: a pack into the archive, which is what the agent dashboard claims by name.</summary>
    public OperationLogDescriptor Describe() =>
        new(OperationKind.Pack, $"{_source.Root} ({NewFolders + Files})", _archive.ArchivePath, NewFolders + Files);

    public async Task RunAsync(CancellationToken ct)
    {
        var additions = _folders.Select(f => (string.Empty, f.EntryName + "/")).ToList();
        string? scratch = null;
        try
        {
            foreach (var (item, entryName) in _files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = item.Source!;
                string local;
                if (_source.IsLocalDisk)
                {
                    local = WindowsFileSystem.ToWindowsPath(entry.Path);
                }
                else
                {
                    // Where scratch copies go: what comes out of an encrypted archive does not outlive a
                    // crash there, the next start sweeps it.
                    scratch ??= Path.GetDirectoryName(TemporaryFiles.PathFor("sync"))!;
                    local = Path.Combine(scratch, additions.Count.ToString());
                    await BringAsync(entry.Path, local, ct).ConfigureAwait(false);
                    if (entry.LastModified != DateTime.MinValue)
                    {
                        File.SetLastWriteTime(local, entry.LastModified);
                    }
                }

                additions.Add((local, entryName));
            }

            await _archive.AddFilesAsync(additions, ct).ConfigureAwait(false);
        }
        finally
        {
            RemoteFetch.DeleteFolder(scratch);
        }
    }

    private async Task BringAsync(VfsPath path, string local, CancellationToken ct)
    {
        await using var input = await _source.Fs.OpenReadAsync(path, ct).ConfigureAwait(false);
        await using var output = File.Create(local);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }
}
