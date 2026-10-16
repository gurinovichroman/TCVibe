using System.IO;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.FileSystem;

/// <summary>
/// Presents a set of search hits as a directory the panel can browse — Total Commander's
/// "feed to listbox". Only the root listing is synthetic; every other call is forwarded to the
/// file system the hits came from, so the entries keep their real paths and the ordinary panel
/// commands (F3, F5, F8, the operation queue) act on the real files.
/// </summary>
/// <remarks>
/// Does <b>not</b> own <paramref name="inner"/>: the panel's base disk file system outlives every
/// mount, and disposing it here would take the whole application's file access down with the
/// results view (TRAP-05).
/// </remarks>
public sealed class SearchResultsFileSystem : IVirtualFileSystem, IBatchRenameFileSystem
{
    private readonly IVirtualFileSystem _inner;

    // Paths only: every listing reads the entries fresh, and a rename or move through this view
    // rewrites the path, so the hit stays in the list under its new name.
    private readonly List<VfsPath> _hits;

    public SearchResultsFileSystem(IVirtualFileSystem inner, IReadOnlyList<IFileEntry> hits)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _hits = (hits ?? throw new ArgumentNullException(nameof(hits))).Select(h => h.Path).ToList();
    }

    /// <summary>
    /// The file system the hits live on — the disk. Code that has only a file system and must know
    /// whether its entries are disk files (the Recycle Bin) looks through the view to this.
    /// </summary>
    public IVirtualFileSystem Inner => _inner;

    public string Scheme => "search";

    public string DisplayName => "Search results";

    /// <summary>The synthetic root; anything else is a real directory on the inner file system.</summary>
    public const string RootPath = "/";

    public async Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) =>
        IsRoot(path) ? await LiveHitsAsync(ct).ConfigureAwait(false) : await _inner.ListAsync(path, ct).ConfigureAwait(false);

    /// <summary>
    /// The hits that still exist, refreshed from disk. A copy, move or delete started from the results
    /// re-lists this view, and a frozen snapshot would keep showing files that are gone (and let F5
    /// act on them). Missing hits are dropped for good, so the view shrinks as the work is done.
    /// </summary>
    private async Task<IReadOnlyList<IFileEntry>> LiveHitsAsync(CancellationToken ct)
    {
        VfsPath[] snapshot;
        lock (_hits)
        {
            snapshot = _hits.ToArray();
        }

        var fresh = new IFileEntry?[snapshot.Length];
        using var gate = new SemaphoreSlim(16);
        await Task.WhenAll(snapshot.Select(async (hit, i) =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                fresh[i] = await _inner.GetEntryAsync(hit, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch { fresh[i] = null; }
            finally { gate.Release(); }
        })).ConfigureAwait(false);

        var live = new List<IFileEntry>(snapshot.Length);
        var gone = new List<VfsPath>();
        for (var i = 0; i < snapshot.Length; i++)
        {
            if (fresh[i] is { } entry)
            {
                live.Add(entry);
            }
            else
            {
                gone.Add(snapshot[i]);
            }
        }

        if (gone.Count > 0)
        {
            lock (_hits)
            {
                foreach (var g in gone)
                {
                    _hits.Remove(g);
                }
            }
        }

        return live;
    }

    public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) =>
        IsRoot(path) ? Task.FromResult<IFileEntry?>(null) : _inner.GetEntryAsync(path, ct);

    public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
        _inner.OpenReadAsync(path, ct);

    public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
    {
        RejectSyntheticTarget(path);
        return _inner.OpenWriteAsync(path, overwrite, ct);
    }

    public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct)
    {
        RejectSyntheticTarget(destination);
        return _inner.CopyAsync(source, destination, progress, ct);
    }

    public async Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct)
    {
        RejectSyntheticTarget(destination);
        await _inner.MoveAsync(source, destination, ct).ConfigureAwait(false);
        Follow(source, destination);
    }

    public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        RejectSyntheticTarget(path);
        return _inner.DeleteAsync(path, recursive, ct);
    }

    public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct)
    {
        RejectSyntheticTarget(path);
        return _inner.CreateDirectoryAsync(path, ct);
    }

    public async Task RenameAsync(VfsPath path, string newName, CancellationToken ct)
    {
        await _inner.RenameAsync(path, newName, ct).ConfigureAwait(false);
        if (path.Parent is { } parent)
        {
            Follow(path, parent.Combine(newName));
        }
    }

    /// <summary>The hits of an archive search rename in one rewrite of the archive, as its own panel does.</summary>
    public bool CanRenameAsBatch => _inner is IBatchRenameFileSystem { CanRenameAsBatch: true };

    public async Task RenameBatchAsync(IReadOnlyList<(VfsPath Path, string NewName)> renames, CancellationToken ct)
    {
        if (_inner is not IBatchRenameFileSystem batch)
        {
            throw new NotSupportedException("The file system under the search results does not rename in batches.");
        }

        await batch.RenameBatchAsync(renames, ct).ConfigureAwait(false);

        // Deepest first: a hit renamed inside a folder that is renamed too takes its own new name before
        // the folder's rename carries it along. The other way round its old path was gone by then.
        foreach (var (path, newName) in renames.OrderByDescending(r => r.Path.Value.Count(c => c == '/')))
        {
            if (path.Parent is { } parent)
            {
                Follow(path, parent.Combine(newName));
            }
        }
    }

    /// <summary>
    /// Keeps a hit renamed or moved through this view in the list under its new path, and the hits
    /// inside a renamed folder under theirs. Without it the next listing found the old path gone and
    /// dropped the file for good, as if it had been deleted.
    /// </summary>
    private void Follow(VfsPath from, VfsPath to)
    {
        var prefix = from.Value.TrimEnd('/') + "/";
        lock (_hits)
        {
            for (var i = 0; i < _hits.Count; i++)
            {
                var hit = _hits[i];
                if (hit.Equals(from))
                {
                    _hits[i] = to;
                }
                else if (hit.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _hits[i] = new VfsPath(to.Value.TrimEnd('/') + "/" + hit.Value[prefix.Length..]);
                }
            }
        }
    }

    public bool SupportsOperation(VfsOperation operation) => _inner.SupportsOperation(operation);

    public event EventHandler<VfsChangedEventArgs>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }

    public void Dispose() { /* the inner file system is borrowed, not owned */ }

    private static bool IsRoot(VfsPath path) =>
        string.IsNullOrEmpty(path.Value) || path.Value == RootPath;

    /// <summary>
    /// True for the synthetic root and for a name directly under it ("/New Folder") — paths that
    /// exist only in this view. Hits keep their real paths ("/D:/proj/a.cs"), which are never there.
    /// </summary>
    public static bool IsSyntheticPath(VfsPath path) =>
        IsRoot(path) || path.Parent is not { } parent || IsRoot(parent);

    /// <summary>
    /// The one folder every entry of <paramref name="entries"/> lies in, or null when they lie in
    /// several (or there are none). On the list, which is no folder, a command that works in "this
    /// folder" — AI rename — works in that one.
    /// </summary>
    public static VfsPath? SharedFolder(IEnumerable<IFileEntry> entries)
    {
        VfsPath? shared = null;
        foreach (var entry in entries)
        {
            if (entry.Path.Parent is not { } parent)
            {
                return null;
            }

            if (shared is null)
            {
                shared = parent;
            }
            else if (!shared.Equals(parent))
            {
                return null;
            }
        }

        return shared;
    }

    /// <summary>
    /// The results are a list, not a folder: nothing can be created in it. Passed through, a path
    /// such as "/New Folder" reached the disk as a rooted Windows path — the root of the current drive — so
    /// F7, F5 into this panel or a drop onto it put files where nobody would look for them.
    /// </summary>
    private static void RejectSyntheticTarget(VfsPath path)
    {
        if (IsSyntheticPath(path))
        {
            throw new SearchResultsReadOnlyException();
        }
    }
}

/// <summary>A write aimed at the search-results list itself rather than at a real folder.</summary>
public sealed class SearchResultsReadOnlyException : IOException
{
    public SearchResultsReadOnlyException()
        : base(TCV.App.Localization.LocalizationManager.Get("Search_ResultsReadOnly"))
    {
    }
}
