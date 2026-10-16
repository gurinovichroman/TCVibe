using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.Operations;
using TCV.App.Services;

namespace TCV.App.Navigation;

/// <summary>
/// Owns the application's <see cref="NameIndex"/>: loads the cached one on first use, rebuilds it
/// from the configured roots in the background, keeps it fresh, and persists the result next to the
/// executable (INV-03), as <c>name-index.json</c> (6.21).
/// </summary>
/// <remarks>
/// Nothing here runs at startup: the cache is read (off the UI thread) the first time the name search
/// opens, so the cold-start budget is untouched. Once loaded the index is kept current three ways:
/// a background rebuild when the cache is older than <see cref="StaleAfter"/>, an incremental rescan
/// of the folders touched by operations the manager itself performed (they arrive through the
/// <see cref="OperationLog"/>), and an explicit rebuild command.
/// </remarks>
public sealed class NameIndexService : IDisposable
{
    /// <summary>Hard cap on indexed entries; a whole-drive walk must stay bounded.</summary>
    public const int MaxEntries = 400_000;

    /// <summary>A cache older than this is refreshed in the background when the search opens.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    private static readonly TimeSpan RescanDelay = TimeSpan.FromSeconds(1.5);

    private readonly ILogger<NameIndexService> _logger;
    private readonly string _cachePath;
    private readonly OperationLog? _operationLog;
    private readonly Func<IReadOnlyList<string>> _defaultRoots;
    private readonly object _gate = new();
    private readonly HashSet<string> _dirtyDirectories = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _buildCts;
    private Task? _loadTask;
    private Timer? _rescanTimer;
    private int _generation;
    private volatile bool _isBuilding;
    private volatile int _buildProgress;
    private volatile bool _defaultRootsBuild = true;
    private volatile string[] _priorityFolders = [];

    public NameIndexService(ILogger<NameIndexService> logger)
        : this(logger, global::TCV.Core.Domain.AppPaths.Data("name-index.json")) { }

    /// <summary>DI entry point: listens to the operation log to keep the index current.</summary>
    public NameIndexService(ILogger<NameIndexService> logger, OperationLog operationLog)
        : this(logger, global::TCV.Core.Domain.AppPaths.Data("name-index.json"), operationLog) { }

    public NameIndexService(
        ILogger<NameIndexService> logger, string cachePath, OperationLog? operationLog = null,
        Func<IReadOnlyList<string>>? defaultRoots = null)
    {
        _logger = logger;
        _cachePath = cachePath;
        _defaultRoots = defaultRoots ?? DefaultRoots;
        _operationLog = operationLog;
        if (_operationLog is not null)
        {
            _operationLog.Changed += OnOperationLogged;
        }
    }

    public NameIndex Index { get; } = new();

    /// <summary>Most entries an incremental rescan of one folder may list before it falls back to the folder's own children.</summary>
    public int RescanMaxEntries { get; init; } = 20_000;

    /// <summary>True while a rebuild is running.</summary>
    public bool IsBuilding => _isBuilding;

    /// <summary>Entries found so far by the running rebuild (0 when none is running).</summary>
    public int BuildProgress => _buildProgress;

    /// <summary>Raised (off the UI thread) when a rebuild finishes and the index has new content.</summary>
    public event EventHandler? Rebuilt;

    /// <summary>
    /// Raised (off the UI thread) whenever what the index shows may have changed: the cache was
    /// loaded, a rebuild made progress or finished, a folder was rescanned.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Loads the cached index the first time it is needed (off the UI thread), then starts a
    /// background rebuild when there is no cache or it is stale. Safe to call on every open; the
    /// work happens once per session, staleness is re-checked on each call. Awaiting it is
    /// optional — the search can open on an empty index and fill in.
    /// </summary>
    public Task EnsureReadyAsync()
    {
        lock (_gate)
        {
            _loadTask ??= Task.Run(LoadCacheOnceAsync);
        }

        return _loadTask.ContinueWith(_ => RefreshIfStaleAsync(), TaskScheduler.Default).Unwrap();
    }

    private Task LoadCacheOnceAsync()
    {
        try
        {
            if (Index.Count == 0 && Index.Load(_cachePath))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Name index cache could not be read");
        }

        return Task.CompletedTask;
    }

    private async Task RefreshIfStaleAsync()
    {
        if (_isBuilding)
        {
            return;
        }

        var missing = Index.Count == 0;
        var stale = !missing && DateTime.UtcNow - Index.BuiltUtc > StaleAfter;
        if (missing || stale)
        {
            await RebuildAsync(_defaultRoots());
        }
    }

    /// <summary>Loads the cached index, if there is one. Cheap, but not called at startup (cold-start budget).</summary>
    public bool LoadCache() => Index.Load(_cachePath);

    /// <summary>
    /// Folders the user works in — workspace tabs, bookmarks, the panels' folders — which the next
    /// whole-disk rebuild walks before the rest of the disk, so the entry cap cannot leave them out
    /// (6.56). The caller passes a snapshot taken on the UI thread; the walk only reads the copy.
    /// </summary>
    public void SetPriorityFolders(IEnumerable<string?> folders) =>
        _priorityFolders = folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!).ToArray();

    /// <summary>The walk order for a whole-disk build: the user's folders, then their profile and project folders.</summary>
    private NameIndexPriority WholeDiskPriority() =>
        NameIndexPriority.Create(_priorityFolders, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>
    /// Rebuilds the index from <paramref name="roots"/>. A new call cancels the running build.
    /// <paramref name="defaultRoots"/> says the roots are the whole-disk defaults, where dependency
    /// folders and ignored build output are left out; a folder the user chose is indexed as it is.
    /// Returns false when the build was cancelled or superseded.
    /// </summary>
    public async Task<bool> RebuildAsync(
        IReadOnlyList<string> roots, bool defaultRoots = true, CancellationToken ct = default)
    {
        CancellationTokenSource cts;
        int generation;
        lock (_gate)
        {
            _buildCts?.Cancel();
            cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _buildCts = cts;
            generation = ++_generation;
            _defaultRootsBuild = defaultRoots;
            _isBuilding = true;
            _buildProgress = 0;
        }

        try
        {
            var options = new NameIndexWalkOptions(
                SkipVendored: defaultRoots, RespectGitignore: defaultRoots,
                Priority: defaultRoots ? WholeDiskPriority() : null);
            var progress = new Progress<int>(count =>
            {
                if (generation != _generation)
                {
                    return;
                }

                _buildProgress = count;
                Changed?.Invoke(this, EventArgs.Empty);
            });

            var result = await Task.Run(() => NameIndexWalker.Walk(roots, options, cts.Token, progress), cts.Token);
            if (cts.Token.IsCancellationRequested || generation != _generation)
            {
                return false;
            }

            Index.Replace(result.Entries, roots, DateTime.UtcNow, result.Truncated);
            await Task.Run(() => Index.Save(_cachePath), CancellationToken.None);
            _logger.LogInformation("Name index rebuilt: {Count} entries from {Roots}{Truncated}",
                result.Entries.Count, string.Join(", ", roots), result.Truncated ? " (truncated)" : string.Empty);
            Rebuilt?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;   // superseded or cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Name index rebuild failed");
            return false;
        }
        finally
        {
            // Only the newest build may clear the flag: a superseded one finishing late must not
            // announce "idle" while its replacement is still walking.
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _isBuilding = false;
                    _buildProgress = 0;
                }
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops the running rebuild, if any; the previous index stays in place.</summary>
    public void CancelRebuild()
    {
        lock (_gate)
        {
            _buildCts?.Cancel();
            if (_isBuilding)
            {
                _generation++;      // make the cancelled build's late completion a no-op
                _isBuilding = false;
                _buildProgress = 0;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Roots to index when the user has not configured any: every ready fixed drive. Drive readiness
    /// can block for seconds on a disconnected network or optical drive, so callers on the UI
    /// thread use <see cref="DefaultRootsAsync"/>.
    /// </summary>
    public static IReadOnlyList<string> DefaultRoots()
    {
        try
        {
            // Type first: IsReady on a removable or network drive is the call that blocks.
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>As <see cref="DefaultRoots"/>, off the UI thread.</summary>
    public static Task<IReadOnlyList<string>> DefaultRootsAsync() => Task.Run(DefaultRoots);

    // ── Keeping the index current ─────────────────────────────────────────────

    private void OnOperationLogged(object? sender, OperationLogEntry? entry)
    {
        if (entry is null || entry.Outcome != OperationOutcome.Completed)
        {
            return;
        }

        // Where items arrived, never the destination: that names the item itself when it arrived under
        // another name (or an archive), and rescanning a file as a folder drops it from the index (6.63).
        var touched = MarkDirty(entry.ArrivedIn);
        touched |= MarkDirty(ParentOfLoggedSource(entry.Source));
        if (!touched)
        {
            return;
        }

        lock (_gate)
        {
            _rescanTimer ??= new Timer(_ => _ = RescanDirtyAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _rescanTimer.Change(RescanDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private bool MarkDirty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var windows = path.Replace('/', (char)92).TrimEnd((char)92);
        if (windows.Length == 0 || !IsUnderIndexedRoot(windows))
        {
            return false;
        }

        lock (_gate)
        {
            _dirtyDirectories.Add(windows);
        }

        return true;
    }

    /// <summary>The log records "dir (N)" for a batch and a full path for one item; both have a parent directory.</summary>
    public static string? ParentOfLoggedSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var batch = source.LastIndexOf(" (", StringComparison.Ordinal);
        if (source.EndsWith(')') && batch > 0)
        {
            return source[..batch];
        }

        var slash = source.LastIndexOfAny([(char)92, '/']);
        return slash > 0 ? source[..slash] : null;
    }

    private bool IsUnderIndexedRoot(string path) =>
        Index.Roots.Any(root => path.StartsWith(root.TrimEnd((char)92), StringComparison.OrdinalIgnoreCase));

    /// <summary>Rescans the folders operations touched since the last pass. Public for tests.</summary>
    public async Task RescanDirtyAsync()
    {
        string[] directories;
        lock (_gate)
        {
            directories = _dirtyDirectories.ToArray();
            _dirtyDirectories.Clear();
        }

        // A full rebuild replaces everything anyway.
        if (_isBuilding || directories.Length == 0)
        {
            return;
        }

        var options = new NameIndexWalkOptions(SkipVendored: _defaultRootsBuild, RespectGitignore: _defaultRootsBuild);
        await Task.Run(() =>
        {
            foreach (var directory in directories)
            {
                // A log line can still name a file where a folder is meant (an archive's entries
                // deleted are logged as «archive.zip (N)»). Rescanning it as a folder would drop the
                // file from the index; it is still there, so there is nothing to refresh (6.63).
                if (File.Exists(directory))
                {
                    continue;
                }

                if (!Directory.Exists(directory))
                {
                    Index.ReplaceSubtree(directory, Array.Empty<NameIndexEntry>());
                    continue;
                }

                var subtree = NameIndexWalker.ListSubtree(directory, options, CancellationToken.None, RescanMaxEntries);
                if (!subtree.Truncated)
                {
                    var entries = new List<NameIndexEntry>
                    {
                        new(directory, Path.GetFileName(directory), true),
                    };
                    entries.AddRange(subtree.Entries);
                    Index.ReplaceSubtree(directory, entries);
                    continue;
                }

                // Copying one file into C:\Projects must not swap hundreds of thousands of indexed
                // entries for the first capped slice of a walk. Refresh the folder's own children
                // only; deeper changes wait for the next full rebuild.
                var children = NameIndexWalker.ListSubtree(
                    directory, options with { MaxDepth = 0 }, CancellationToken.None, int.MaxValue);
                Index.ReplaceChildren(directory, children.Entries);
            }

            Index.Save(_cachePath);
        });

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Queues a folder for the next incremental rescan. Public for tests and for callers that create files.</summary>
    public void NoteChanged(string directory)
    {
        if (!MarkDirty(directory))
        {
            return;
        }

        lock (_gate)
        {
            _rescanTimer ??= new Timer(_ => _ = RescanDirtyAsync(), null, Timeout.Infinite, Timeout.Infinite);
            _rescanTimer.Change(RescanDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_operationLog is not null)
        {
            _operationLog.Changed -= OnOperationLogged;
        }

        lock (_gate)
        {
            _buildCts?.Cancel();
            _rescanTimer?.Dispose();
        }
    }
}
