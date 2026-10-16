using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.FileSystem;
using TCV.App.FileSystem.Native;
using TCV.App.Localization;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

public sealed class MoveOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _sourceFs;
    private readonly IVirtualFileSystem _destFs;
    private readonly IReadOnlyList<IFileEntry> _entries;
    private readonly VfsPath _destination;
    private readonly IConflictStrategy _conflictStrategy;
    private readonly ILogger<MoveOperation> _logger;

    public IProgress<FileProgress>? Progress { get; set; }

    /// <summary>
    /// The name the single entry gets at the destination: F6 to a path that is not a folder renames,
    /// as in Total Commander. Null keeps its name. Only the top-level entry is renamed.
    /// </summary>
    public string? TargetName { get; init; }

    /// <summary>Creates the destination folder when it is not there yet (a folder typed in the dialog).</summary>
    public bool CreateDestination { get; init; }

    private string NameAt(IFileEntry entry) =>
        TargetName is not null && _entries.Count == 1 && ReferenceEquals(entry, _entries[0])
            ? TargetName
            : entry.Name;

    // A move honours no copy options: it renames, or it streams and then deletes. The parameter
    // stays so callers keep one shape for both operations, but nothing reads it.
    public MoveOperation(
        IVirtualFileSystem sourceFs,
        IVirtualFileSystem destFs,
        IReadOnlyList<IFileEntry> entries,
        VfsPath destination,
        IConflictStrategy conflictStrategy,
        CopyOptions options,
        ILogger<MoveOperation> logger)
        : this(sourceFs, destFs, entries, destination, conflictStrategy, logger)
    {
    }

    public MoveOperation(
        IVirtualFileSystem sourceFs,
        IVirtualFileSystem destFs,
        IReadOnlyList<IFileEntry> entries,
        VfsPath destination,
        IConflictStrategy conflictStrategy,
        ILogger<MoveOperation> logger)
    {
        _sourceFs = sourceFs;
        _destFs = destFs;
        _entries = entries;
        _destination = destination;
        _conflictStrategy = conflictStrategy;
        _logger = logger;
    }

    public override OperationLogDescriptor LogDescriptor => new(
        OperationKind.Move,
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

            // Only a move that has to stream bytes (across file systems, or across volumes of the
            // disk) can report progress; inside one volume it is an atomic rename.
            long totalBytes = 0;
            foreach (var entry in _entries)
                if (NeedsStreaming(entry.Path, _destination.Combine(NameAt(entry))))
                    totalBytes += await SumFileBytesAsync(_sourceFs, [entry], ct);

            var sw = Stopwatch.StartNew();
            var progress = new AggregateProgress(Progress, totalBytes, sw);

            foreach (var entry in _entries)
                await MoveOneAsync(entry, _destination, progress, ct);

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

    private void EnsureDestinationOutsideSources()
    {
        if (!FileSystemIdentity.Same(_sourceFs, _destFs)) return;

        foreach (var entry in _entries)
            if (entry.IsDirectory && OperationPaths.IsSameOrInside(_destination, entry.Path)
                && !entry.Path.Equals(_destination.Combine(NameAt(entry))))
                throw new DestinationInsideSourceException(entry.Name);
    }

    /// <summary>A rename cannot cross file systems, nor volumes of the local disk.</summary>
    private bool NeedsStreaming(VfsPath src, VfsPath dst)
    {
        if (!FileSystemIdentity.Same(_sourceFs, _destFs)) return true;
        if (!IsLocalDisk(_sourceFs)) return false;
        return !OperationPaths.SameVolume(
            WindowsFileSystem.ToWindowsPath(src), WindowsFileSystem.ToWindowsPath(dst));
    }

    private async Task MoveOneAsync(
        IFileEntry entry, VfsPath destDir, AggregateProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (PauseHandle is not null)
            await PauseHandle.WaitAsync(ct);

        var destPath = destDir.Combine(NameAt(entry));

        // Moving an item onto itself is a no-op. Without this the Overwrite branch below
        // would clear the "destination" — which is the source — before the move fails.
        if (FileSystemIdentity.Same(_sourceFs, _destFs) && entry.Path.Equals(destPath))
        {
            // Only the case differs ("readme" → "README"): on the disk that is a rename, not a no-op.
            // (Not on a server: there the two may be different files.)
            if (IsLocalDisk(_sourceFs) && !string.Equals(entry.Name, destPath.FileName, StringComparison.Ordinal))
            {
                await RunGuardedAsync(entry.Path, () => _sourceFs.RenameAsync(entry.Path, destPath.FileName, ct), ct);
                return;
            }

            // Not a skip: the item already is where it was asked to be, so a cut-and-paste or the drop
            // stack may treat it as arrived. The user still hears that nothing happened.
            AddWarning(string.Format(LocalizationManager.Get("Op_SameItem"), entry.Name));
            return;
        }

        var streaming = NeedsStreaming(entry.Path, destPath);
        var existing = await _destFs.GetEntryAsync(destPath, ct);
        var overwriting = false;

        // The same item under another spelling: a subst drive and its folder, a mapped drive and its
        // UNC path, a junction. They look like different volumes, so the move would stream the file
        // over itself and then delete the "source" — the only copy. Nothing to do.
        if (existing is not null && IsLocalDisk(_sourceFs) && IsLocalDisk(_destFs)
            && FileIdentity.IsSameItem(WindowsFileSystem.ToWindowsPath(entry.Path), WindowsFileSystem.ToWindowsPath(destPath)))
        {
            AddWarning(string.Format(LocalizationManager.Get("Op_SameItem"), entry.Name));
            progress.AddSkipped(entry.IsDirectory ? 0 : entry.Size, entry.Name);
            return;
        }

        if (OperationPaths.IsReparseDirectory(entry) && (streaming || existing is not null))
        {
            SkipReparse(entry);
            return;
        }

        if (existing is not null)
        {
            // Folder onto folder merges, as in Total Commander and Explorer: what was in the
            // destination and not in the source must survive a move.
            if (entry.IsDirectory && existing.IsDirectory)
            {
                await MergeDirectoryAsync(entry, destPath, progress, ct);
                return;
            }

            // "Overwrite if newer" is settled by the one rule every transfer shares: times compared
            // as moments, a server's UTC against the disk's local time (TRAP-48, TRAP-58).
            var resolution = await ConflictPolicy.ResolveAsync(
                new ConflictInfo(entry, existing, destPath), onlyIfNewer: false, _conflictStrategy.ResolveAsync, ct);

            switch (resolution)
            {
                case ConflictResolution.Skip:
                    CountSkipped();
                    progress.AddSkipped(entry.IsDirectory ? 0 : entry.Size, entry.Name);
                    return;
                case ConflictResolution.Cancel:
                    State = OperationState.Cancelled;
                    throw new OperationCanceledException("Cancelled by user at conflict.");
                case ConflictResolution.RenameAuto:
                    var newName = await RenameAutoStrategy.GenerateNewNameAsync(entry.Name,
                        async n => await _destFs.GetEntryAsync(destDir.Combine(n), ct) is not null,
                        entry.IsDirectory);
                    destPath = destDir.Combine(newName);
                    break;
                default:
                    if (entry.IsDirectory != existing.IsDirectory)
                    {
                        await RunGuardedAsync(destPath, () => Task.FromException(new IOException(
                            $"Cannot replace a {(existing.IsDirectory ? "folder" : "file")} with a " +
                            $"{(entry.IsDirectory ? "folder" : "file")}: {entry.Name}")), ct);
                        progress.AddSkipped(entry.IsDirectory ? 0 : entry.Size, entry.Name);
                        return;
                    }
                    overwriting = true;
                    break;
            }
        }

        if (entry.IsDirectory)
        {
            if (!streaming)
            {
                await RunGuardedAsync(entry.Path, () => _sourceFs.MoveAsync(entry.Path, destPath, ct), ct);
            }
            else
            {
                // The subtree is fully across, or the source is left alone: a failure midway must
                // not delete data that never landed.
                var skippedBefore = SkippedCount;
                await CopyTreeAcrossAsync(entry, destPath, progress, ct);
                if (SkippedCount == skippedBefore)
                {
                    // One recursive delete unlinks it in a single server-side walk instead of a
                    // round trip per child.
                    await RunGuardedAsync(entry.Path, () => DeleteSourceAsync(entry.Path, true, ct), ct);
                }
            }
        }
        else
        {
            var relay = progress.CreateFileRelay(entry.Name);
            var moved = await RunGuardedAsync(entry.Path, async () =>
            {
                if (!streaming)
                {
                    if (overwriting && IsLocalDisk(_destFs)) ClearReadOnly(destPath);
                    await _sourceFs.MoveAsync(entry.Path, destPath, ct);
                }
                else
                {
                    await StreamFileAsync(_sourceFs, _destFs, entry.Path, destPath, entry.Size, relay, ct, overwriting);
                    await DeleteSourceAsync(entry.Path, false, ct);
                }
            }, ct);

            if (!moved)
                relay.SkipRemaining(entry.Size);
        }

        _logger.LogDebug("Moved {Source} → {Dest}", entry.Path, destPath);
    }

    private async Task DeleteSourceAsync(VfsPath path, bool recursive, CancellationToken ct)
    {
        if (IsLocalDisk(_sourceFs) && !recursive) ClearReadOnly(path);
        await _sourceFs.DeleteAsync(path, recursive, ct);
    }

    /// <summary>
    /// Moves the children of <paramref name="entry"/> into the existing folder
    /// <paramref name="destPath"/> one by one, then removes the source folder if nothing is left in it.
    /// </summary>
    private async Task MergeDirectoryAsync(
        IFileEntry entry, VfsPath destPath, AggregateProgress progress, CancellationToken ct)
    {
        var children = await _sourceFs.ListAsync(entry.Path, ct);
        foreach (var child in children)
            await MoveOneAsync(child, destPath, progress, ct);

        var left = await _sourceFs.ListAsync(entry.Path, ct);
        if (left.Count == 0)
            await RunGuardedAsync(entry.Path, () => _sourceFs.DeleteAsync(entry.Path, false, ct), ct);
    }

    /// <summary>
    /// Streams a subtree to a file system where a rename is not possible. The source is left
    /// untouched here: the caller removes it only once everything arrived.
    /// </summary>
    private async Task CopyTreeAcrossAsync(
        IFileEntry entry, VfsPath destPath, AggregateProgress progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (OperationPaths.IsReparseDirectory(entry))
        {
            SkipReparse(entry);
            return;
        }

        if (entry.IsDirectory)
        {
            if (!await RunGuardedAsync(destPath, () => _destFs.CreateDirectoryAsync(destPath, ct), ct))
                return;

            foreach (var child in await ListSourceAsync(_sourceFs, entry.Path, ct))
                await CopyTreeAcrossAsync(child, destPath.Combine(child.Name), progress, ct);
            return;
        }

        if (PauseHandle is not null)
            await PauseHandle.WaitAsync(ct);

        var relay = progress.CreateFileRelay(entry.Name);
        var copied = await RunGuardedAsync(destPath, () =>
            StreamFileAsync(_sourceFs, _destFs, entry.Path, destPath, entry.Size, relay, ct), ct);

        if (!copied)
            relay.SkipRemaining(entry.Size);
    }
}
