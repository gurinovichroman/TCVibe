using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Sync;

/// <summary>
/// Whether what a synchronisation is about to replace is still what the comparison saw. The window is
/// not modal (6.96): while it is open the user works on in the panels, and an agent or a colleague goes
/// on changing the server. A row computed before that must not overwrite what came after it, so the
/// target folders of the plan are listed once more before anything is run. One listing per folder, not
/// a command per file.
/// </summary>
public static class SyncPlanFreshness
{
    /// <summary>Two readings of one listing differ by nothing; a little room for a server that rounds differently per command.</summary>
    private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The relative paths of the plan whose target is no longer as compared: a file about to be replaced
    /// changed, or a name about to be created was taken. A target that went away is not on the list:
    /// nothing would be overwritten. A folder that cannot be listed counts for the files that would
    /// replace something in it; a new file in a folder that does not exist yet is fine.
    /// </summary>
    public static async Task<IReadOnlyList<string>> FindChangedAsync(
        IReadOnlyList<SyncPlanItem> plan, SyncEndpoint left, SyncEndpoint right, CancellationToken ct)
    {
        var changed = new List<string>();
        var folders = plan
            .Where(i => !i.KindMismatch && i.Source is not null && !i.Row.IsDirectory)
            .GroupBy(i => (i.Direction, i.Row.RelativeDirectory));

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            var target = folder.Key.Direction == SyncDirection.ToRight ? right : left;
            var listed = await ListAsync(target, folder.Key.RelativeDirectory, ct).ConfigureAwait(false);

            foreach (var item in folder)
            {
                if (listed is null)
                {
                    if (item.Target is not null)
                    {
                        changed.Add(item.Row.RelativePath);
                    }

                    continue;
                }

                listed.TryGetValue(item.Row.Name, out var now);
                if (IsChanged(item.Target, now))
                {
                    changed.Add(item.Row.RelativePath);
                }
            }
        }

        return changed;
    }

    private static bool IsChanged(IFileEntry? compared, IFileEntry? now) =>
        (compared, now) switch
        {
            (null, null) => false,
            (null, _) => true,
            (_, null) => false,
            _ => now.IsDirectory != compared.IsDirectory
                 || now.Size != compared.Size
                 || DirectoryComparison.TimeDifference(now.LastModified, compared.LastModified).Duration() > Tolerance,
        };

    /// <summary>The folder's entries by name; null when it cannot be read.</summary>
    private static async Task<IReadOnlyDictionary<string, IFileEntry>?> ListAsync(
        SyncEndpoint side, string relativeDirectory, CancellationToken ct)
    {
        try
        {
            var entries = await side.Fs.ListAsync(new VfsPath(side.Combine(relativeDirectory)), ct).ConfigureAwait(false);
            var byName = new Dictionary<string, IFileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                byName.TryAdd(entry.Name, entry);
            }

            return byName;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
