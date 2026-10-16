using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>One entry of a <see cref="CopyPlan"/>.</summary>
/// <param name="Parent">Index of the folder item that holds this one, or -1 for a selected item.</param>
public sealed record CopyPlanItem(IFileEntry Entry, int Parent)
{
    public bool IsSelected => Parent < 0;
}

/// <summary>
/// The selected trees flattened for a parallel copy: every folder and file, in the order the
/// sequential copy visits them (depth first, each folder before what it holds). The folders are
/// made from it one by one, parents first; the files can then go several at a time. A junction is
/// listed but never entered, as in the sequential walk.
/// </summary>
public sealed class CopyPlan
{
    private CopyPlan(IReadOnlyList<CopyPlanItem> items)
    {
        Items = items;
        Directories = Enumerable.Range(0, items.Count).Where(i => items[i].Entry.IsDirectory).ToArray();
        Files = Enumerable.Range(0, items.Count).Where(i => !items[i].Entry.IsDirectory).ToArray();
    }

    public IReadOnlyList<CopyPlanItem> Items { get; }

    /// <summary>Indexes of the folders in <see cref="Items"/>, every parent before its children.</summary>
    public IReadOnlyList<int> Directories { get; }

    /// <summary>Indexes of the files in <see cref="Items"/>.</summary>
    public IReadOnlyList<int> Files { get; }

    /// <param name="listAsync">Lists a source folder; the copy passes its cached listing.</param>
    public static async Task<CopyPlan> BuildAsync(
        IReadOnlyList<IFileEntry> selected,
        Func<VfsPath, CancellationToken, Task<IReadOnlyList<IFileEntry>>> listAsync,
        CancellationToken ct)
    {
        var items = new List<CopyPlanItem>();
        foreach (var entry in selected)
        {
            await AddAsync(entry, -1);
        }
        return new CopyPlan(items);

        async Task AddAsync(IFileEntry entry, int parent)
        {
            ct.ThrowIfCancellationRequested();
            var index = items.Count;
            items.Add(new CopyPlanItem(entry, parent));

            if (!entry.IsDirectory || OperationPaths.IsReparseDirectory(entry))
            {
                return;
            }
            foreach (var child in await listAsync(entry.Path, ct))
            {
                await AddAsync(child, index);
            }
        }
    }
}
