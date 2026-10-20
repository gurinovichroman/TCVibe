using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.App.Localization;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

public sealed class CopyOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _sourceFs;
    private readonly IVirtualFileSystem _destFs;
    private readonly IReadOnlyList<IFileEntry> _entries;
    private readonly VfsPath _destination;
    private readonly IConflictStrategy _conflictStrategy;
    private readonly CopyOptions _options;
    private readonly ILogger<CopyOperation> _logger;

    public IProgress<FileProgress>? Progress { get; set; }

    /// <summary>
    /// The name the single entry gets at the destination (F5 to «D:\x\new.txt»). Null keeps its name.
    /// Only the top-level entry is renamed; what is inside a folder keeps its names.
    /// </summary>
    public string? TargetName { get; init; }

    /// <summary>Creates the destination folder when it is not there yet (a folder typed in the dialog).</summary>
    public bool CreateDestination { get; init; }

    /// <summary>
    /// An item copied into the folder it is already in gets a free name («a (1).txt»), as Explorer
    /// does, instead of being left out. Off for headless callers, which keep the plain skip.
    /// </summary>
    public bool DuplicateInPlace { get; init; }

    private string NameAt(IFileEntry entry) =>
        TargetName is not null && _entries.Count == 1 && ReferenceEquals(entry, _entries[0])
            ? TargetName
            : entry.Name;

    public CopyOperation(
        IVirtualFileSystem sourceFs,
        IVirtualFileSystem destFs,
        IReadOnlyList<IFileEntry> entries,
        VfsPath destination,
        IConflictStrategy conflictStrategy,
        CopyOptions options,
        ILogger<CopyOperation> logger)
    {
        _sourceFs = sourceFs;
        _destFs = destFs;
        _entries = entries;
        _destination = destination;
        _conflictStrategy = conflictStrategy;
        _options = options;
        _logger = logger;
    }

    // Backwards-compatible overload (default options)
    public CopyOperation(
        IVirtualFileSystem sourceFs,
        IVirtualFileSystem destFs,
        IReadOnlyList<IFileEntry> entries,
        VfsPath destination,
        IConflictStrategy conflictStrategy,
        ILogger<CopyOperation> logger)
        : this(sourceFs, destFs, entries, destination, conflictStrategy, new CopyOptions(), logger)
    {
    }

    public override OperationLane Lane => LaneOf(_sourceFs, _destFs);

    public override OperationLogDescriptor LogDescriptor => new(
        OperationKind.Copy,
        OperationLogDescriptor.DescribeEntries(_entries),
        TargetName is null ? _destination.Value : _destination.Combine(TargetName).Value,
        _entries.Count)
    {
        ArrivedIn = _destination.Value,
    };

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            EnsureDestinationOutsideSources();

            if (CreateDestination && await _destFs.GetEntryAsync(_destination, ct) is null)
                await _destFs.CreateDirectoryAsync(_destination, ct);

            long totalBytes = await SumFileBytesAsync(_sourceFs, _entries, ct);
            var progress = new AggregateProgress(Progress, totalBytes, Stopwatch.StartNew());

            if (_options.Threads > 1)
                await CopyParallelAsync(progress, ct);
            else
                await CopySequentialAsync(_entries, _destination, progress, ct);

            State = OperationState.Completed;
        }
        catch (OperationCanceledException)
        {
            State = OperationState.Cancelled;
            throw;
        }
        catch (Exception ex)
        {
            State = OperationState.Failed;
            FailureException = ex;
            throw;
        }
    }

    /// <summary>
    /// Copying a folder into itself would either recurse or, thanks to the cached listing of the
    /// sizing pass, quietly nest a copy inside the original. Both are wrong; refuse up front.
    /// </summary>
    private void EnsureDestinationOutsideSources()
    {
        if (!FileSystemIdentity.Same(_sourceFs, _destFs)) return;

        foreach (var entry in _entries)
            if (entry.IsDirectory && OperationPaths.IsSameOrInside(_destination, entry.Path))
                throw new DestinationInsideSourceException(entry.Name);
    }

    private async Task CopySequentialAsync(
        IReadOnlyList<IFileEntry> entries, VfsPath destDir,
        AggregateProgress progress, CancellationToken ct)
    {
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (PauseHandle is not null)
                await PauseHandle.WaitAsync(ct);
            await ProcessEntryAsync(entry, destDir, progress, ct);
        }
    }

    /// <summary>
    /// Parallel by file, not by selected item: a single big folder is where the threads matter most.
    /// The folders are made first, one by one and parents first, with the same checks as in the
    /// sequential walk; then the files go <see cref="CopyOptions.Threads"/> at a time.
    /// </summary>
    private async Task CopyParallelAsync(AggregateProgress progress, CancellationToken ct)
    {
        // The sizing pass has listed the whole tree already, so planning costs no round trips.
        var plan = await CopyPlan.BuildAsync(_entries, (path, token) => ListSourceAsync(_sourceFs, path, token), ct);
        _logger.LogDebug("Parallel copy: {Folders} folders, {Files} files, {Threads} threads",
            plan.Directories.Count, plan.Files.Count, _options.Threads);

        // Where each folder of the plan landed; null when it was left out, and with it all it holds.
        var landed = new VfsPath?[plan.Items.Count];
        VfsPath? FolderOf(CopyPlanItem item) => item.IsSelected ? _destination : landed[item.Parent];

        foreach (var index in plan.Directories)
        {
            ct.ThrowIfCancellationRequested();
            if (PauseHandle is not null)
            {
                await PauseHandle.WaitAsync(ct);
            }

            var item = plan.Items[index];
            if (FolderOf(item) is not { } parent)
            {
                continue;
            }

            if (await ResolveTargetAsync(item.Entry, parent, progress, null, ct) is { } target
                && await RunGuardedAsync(target.Path, () => _destFs.CreateDirectoryAsync(target.Path, ct), ct))
            {
                landed[index] = target.Path;
            }
        }

        var files = new List<(IFileEntry Entry, VfsPath Folder)>(plan.Files.Count);
        foreach (var index in plan.Files)
        {
            var item = plan.Items[index];
            if (FolderOf(item) is { } folder)
            {
                files.Add((item.Entry, folder));
            }
            else
            {
                progress.AddSkipped(item.Entry.Size, item.Entry.Name);   // its folder was left out
            }
        }

        var claims = new DestinationClaims();
        await Parallel.ForEachAsync(files,
            new ParallelOptions { MaxDegreeOfParallelism = _options.Threads, CancellationToken = ct },
            async (file, innerCt) =>
            {
                innerCt.ThrowIfCancellationRequested();
                if (PauseHandle is not null)
                {
                    await PauseHandle.WaitAsync(innerCt);
                }

                using var scope = claims.OpenScope();
                await scope.ClaimAsync(file.Folder.Combine(NameAt(file.Entry)), innerCt);
                if (await ResolveTargetAsync(file.Entry, file.Folder, progress, scope, innerCt) is { } target)
                {
                    await CopyFileAsync(file.Entry, target, progress, innerCt);
                }
            });

        // A folder's own times go last, deepest first: every file written into it moved them.
        if (PreservesTimestamps)
        {
            for (var i = plan.Directories.Count - 1; i >= 0; i--)
            {
                var index = plan.Directories[i];
                if (landed[index] is { } path)
                {
                    CopyDirTimestamps(plan.Items[index].Entry.Path, path);
                }
            }
        }
    }

    private async Task ProcessEntryAsync(
        IFileEntry entry, VfsPath destDir, AggregateProgress progress, CancellationToken ct)
    {
        if (await ResolveTargetAsync(entry, destDir, progress, null, ct) is not { } target)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            // Folder onto folder merges (as in Total Commander and Explorer): the conflicts that
            // matter are the files inside, and they are resolved one by one below.
            if (!await RunGuardedAsync(target.Path, () => _destFs.CreateDirectoryAsync(target.Path, ct), ct))
            {
                return;
            }

            var children = await ListSourceAsync(_sourceFs, entry.Path, ct);
            await CopySequentialAsync(children, target.Path, progress, ct);

            if (PreservesTimestamps)
            {
                CopyDirTimestamps(entry.Path, target.Path);
            }
        }
        else
        {
            await CopyFileAsync(entry, target, progress, ct);
        }
    }

    /// <summary>Where an item goes at the destination, and whether it replaces a file there.</summary>
    private readonly record struct Target(VfsPath Path, bool Overwriting);

    /// <summary>
    /// Settles where <paramref name="entry"/> lands in <paramref name="destDir"/>: junctions, the item
    /// itself, "only if newer" and conflicts. Null when it is left out, which is counted here.
    /// </summary>
    /// <param name="claims">The paths this file holds in a parallel copy; null when nothing runs beside it.</param>
    private async Task<Target?> ResolveTargetAsync(
        IFileEntry entry, VfsPath destDir, AggregateProgress progress,
        DestinationClaims.Scope? claims, CancellationToken ct)
    {
        var destPath = destDir.Combine(NameAt(entry));

        if (OperationPaths.IsReparseDirectory(entry))
        {
            SkipReparse(entry);
            return null;
        }

        // A file copied onto itself would be truncated by its own writer. Leave it alone — or, when
        // the user copied into the folder the item is already in, make a copy beside it.
        if (FileSystemIdentity.Same(_sourceFs, _destFs) && entry.Path.Equals(destPath))
        {
            if (!DuplicateInPlace)
            {
                _logger.LogDebug("Skipped (source is the destination): {Source}", entry.Path);
                CountSkipped();
                AddWarning(string.Format(LocalizationManager.Get("Op_SameItem"), entry.Name));
                return null;
            }

            destPath = destDir.Combine(await FreeNameAsync(entry, destDir, claims, ct));
        }

        var existing = await _destFs.GetEntryAsync(destPath, ct);
        var overwriting = false;

        // The same item under another spelling (a subst drive, a mapped drive and its UNC path, a
        // junction): "overwriting" it would only replace it with itself. Nothing to do.
        if (existing is not null && BothLocal && SameItemOnDisk(entry.Path, destPath))
        {
            CountSkipped();
            AddWarning(string.Format(LocalizationManager.Get("Op_SameItem"), entry.Name));
            progress.AddSkipped(entry.IsDirectory ? 0 : entry.Size, entry.Name);
            return null;
        }

        if (existing is not null && !(entry.IsDirectory && existing.IsDirectory))
        {
            // "Only if newer" is a sync: older or equal is left alone, newer replaces without asking.
            var resolution = await ConflictPolicy.ResolveAsync(
                new ConflictInfo(entry, existing, destPath), _options.OnlyIfNewer, AskAboutConflictAsync, ct);

            switch (resolution)
            {
                case ConflictResolution.Skip:
                    _logger.LogDebug("Skipped (name taken): {Source}", entry.Path);
                    CountSkipped();
                    progress.AddSkipped(entry.IsDirectory ? 0 : entry.Size, entry.Name);
                    return null;
                case ConflictResolution.Cancel:
                    State = OperationState.Cancelled;
                    throw new OperationCanceledException("Cancelled by user at conflict.");
                case ConflictResolution.RenameAuto:
                    destPath = destDir.Combine(await FreeNameAsync(entry, destDir, claims, ct));
                    break;
                default:
                    // Overwrite: a file replaces the file; anything else is a type clash the
                    // copy below reports as an IO error the user can skip.
                    overwriting = !entry.IsDirectory && !existing.IsDirectory;
                    break;
            }
        }

        return new Target(destPath, overwriting);
    }

    private readonly SemaphoreSlim _conflictGate = new(1, 1);

    /// <summary>
    /// One conflict question at a time, whatever the strategy: the files of a parallel copy meet their
    /// conflicts on several threads, and only asked one by one does an "apply to all" the strategy
    /// remembers reach the files already waiting for an answer.
    /// </summary>
    private async Task<ConflictResolution> AskAboutConflictAsync(ConflictInfo conflict, CancellationToken ct)
    {
        await _conflictGate.WaitAsync(ct);
        try
        {
            return await _conflictStrategy.ResolveAsync(conflict, ct);
        }
        finally
        {
            _conflictGate.Release();
        }
    }

    /// <summary>
    /// A name in <paramref name="destDir"/> nothing occupies: not on the destination, and in a
    /// parallel copy not taken by a file still on its way either (the name found is then held).
    /// </summary>
    private Task<string> FreeNameAsync(
        IFileEntry entry, VfsPath destDir, DestinationClaims.Scope? claims, CancellationToken ct) =>
        RenameAutoStrategy.GenerateNewNameAsync(entry.Name, async n =>
        {
            var candidate = destDir.Combine(n);
            if (claims is not null && !claims.TryClaim(candidate))
            {
                return true;
            }
            if (await _destFs.GetEntryAsync(candidate, ct) is null)
            {
                return false;
            }

            claims?.Release(candidate);
            return true;
        }, entry.IsDirectory);

    private async Task CopyFileAsync(
        IFileEntry entry, Target target, AggregateProgress progress, CancellationToken ct)
    {
        var relay = progress.CreateFileRelay(entry.Name);
        var copied = await RunGuardedAsync(target.Path, async () =>
        {
            await StreamFileAsync(_sourceFs, _destFs, entry.Path, target.Path, entry.Size, relay, ct, target.Overwriting);
            _logger.LogDebug("Copied {Source} → {Dest}", entry.Path, target.Path);

            if (PreservesTimestamps)
            {
                CopyFileTimestamps(entry.Path, target.Path);
            }

            if (BothLocal)
            {
                CopyFileAttributes(entry.Path, target.Path);
            }
        }, ct);

        if (!copied)
        {
            relay.SkipRemaining(entry.Size);
        }
    }

    private bool BothLocal => IsLocalDisk(_sourceFs) && IsLocalDisk(_destFs);

    private static bool SameItemOnDisk(VfsPath a, VfsPath b) =>
        TCV.App.FileSystem.Native.FileIdentity.IsSameItem(
            TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(a),
            TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(b));

    /// <summary>
    /// Timestamps are stamped through the BCL on real Windows paths, which only exist when
    /// both sides are the local disk. Across file systems the option is dropped rather than
    /// applied to a fabricated path — that used to fail the whole copy after the bytes landed.
    /// </summary>
    private bool PreservesTimestamps => _options.PreserveTimestamps && BothLocal;

    // -------------------------------------------------------------------------
    // Timestamp and attribute helpers
    // -------------------------------------------------------------------------

    private static void CopyFileTimestamps(VfsPath src, VfsPath dst)
    {
        var s = TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(src);
        var d = TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(dst);
        var fi = new FileInfo(s);
        File.SetCreationTime(d, fi.CreationTime);
        File.SetLastWriteTime(d, fi.LastWriteTime);
        File.SetLastAccessTime(d, fi.LastAccessTime);
    }

    private static void CopyDirTimestamps(VfsPath src, VfsPath dst)
    {
        var s = TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(src);
        var d = TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(dst);
        var di = new DirectoryInfo(s);
        Directory.SetCreationTime(d, di.CreationTime);
        Directory.SetLastWriteTime(d, di.LastWriteTime);
    }

    /// <summary>
    /// Read-only, hidden, system and archive flags travel with the file (Explorer and TC do the
    /// same). Applied last, so a read-only source does not block the timestamps above.
    /// </summary>
    private static void CopyFileAttributes(VfsPath src, VfsPath dst)
    {
        const FileAttributes Carried = FileAttributes.ReadOnly | FileAttributes.Hidden
            | FileAttributes.System | FileAttributes.Archive;

        var s = TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(src);
        var d = TCV.App.FileSystem.WindowsFileSystem.ToWindowsPath(dst);
        var wanted = File.GetAttributes(s) & Carried;
        var current = File.GetAttributes(d);
        var next = (current & ~Carried) | wanted;
        if (next != current) File.SetAttributes(d, next);
    }
}
