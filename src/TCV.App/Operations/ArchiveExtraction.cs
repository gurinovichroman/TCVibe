using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using TCV.App.Localization;
using TCV.App.Operations.Conflict;
using TCV.Archives;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.Operations;

/// <summary>
/// One archive entry queued for extraction. SourcePath is the path *inside* the archive; DisplayName is
/// the name it gets in the destination folder (another one than its own for F5 «D:\x\new.txt»).
/// </summary>
public sealed record ArchiveExtractItem(
    string SourcePath, string DisplayName, bool IsDirectory, long Size, DateTime LastModified);

/// <summary>
/// Extracts archive entries into a folder on disk — files, and folders merged into what is already
/// there — as the work of a <see cref="DelegateOperation"/>, whose error prompts, skip count, pause
/// and progress it reports through.
/// </summary>
/// <remarks>
/// Two steps. First every name is settled: a taken one by the copy dialog's rules (its choice, or the
/// conflict dialog; "only if newer") and folders are created. Then the archive is read once for all the files
/// (<see cref="IArchiveEngine.ReadEntries"/>). Going entry by entry, a solid 7z or RAR is decoded from
/// the start for every file, which made extracting a folder quadratic. Whatever that pass does not
/// deliver is extracted on its own afterwards, the old way, and meets its own error there: an entry
/// wanted under a second name, a file that replaces another one this extraction writes, and the rest
/// when the engine gave up halfway.
/// </remarks>
public sealed class ArchiveExtraction
{
    private const int BufferSize = 81920;

    private readonly IArchiveEngine _engine;
    private readonly string _archivePath;
    private readonly IReadOnlyList<ArchiveEntry> _entries;
    private readonly DelegateOperation _op;
    private readonly IConflictStrategy _conflicts;
    private readonly CopyOptions _options;

    /// <param name="entries">The archive's listing: what a folder item holds is looked up here.</param>
    /// <param name="conflicts">
    /// Answers for a name that is taken: the copy dialog's choice, or the conflict dialog with its
    /// "apply to all".
    /// </param>
    /// <param name="options">
    /// The copy dialog's options, read the way the copy between file systems reads them: "only if
    /// newer" (<see cref="ConflictPolicy"/>) and the timestamps. Threads mean nothing here: the
    /// archive is read in one pass, which is what makes extracting a folder linear.
    /// </param>
    public ArchiveExtraction(
        IArchiveEngine engine, string archivePath, IReadOnlyList<ArchiveEntry> entries, DelegateOperation op,
        IConflictStrategy conflicts, CopyOptions? options = null)
    {
        _engine = engine;
        _archivePath = archivePath;
        _entries = entries;
        _op = op;
        _conflicts = conflicts;
        _options = options ?? new CopyOptions();
    }

    /// <summary>
    /// Extracts <paramref name="items"/> into <paramref name="destDir"/> and returns the ones that
    /// landed whole — so a move deletes exactly those from the archive and nothing more.
    /// </summary>
    public async Task<IReadOnlyList<ArchiveExtractItem>> RunAsync(
        IReadOnlyList<ArchiveExtractItem> items, string destDir, CancellationToken ct)
    {
        var plan = new Plan(new AggregateProgress(_op.Progress, TotalBytes(items, _entries), Stopwatch.StartNew()));

        Directory.CreateDirectory(destDir);
        var owners = new List<Owner>();
        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            var owner = new Owner(item);
            owners.Add(owner);
            await PlanItemAsync(plan, owner, ArchivePathSafety.ResolveDestination(destDir, item.DisplayName), ct);
        }

        await ExtractInOnePassAsync(plan, ct);

        // In plan order, so a file that replaces another one this extraction writes comes after it.
        var rest = plan.Steps.Where(s => !s.Started).ToList();
        if (rest.Count > 0)
        {
            await Task.Run(() =>
            {
                foreach (var step in rest)
                {
                    ct.ThrowIfCancellationRequested();
                    Extract(plan, step, content: null, ct);
                }
            }, ct);
        }

        StampFolders(plan);
        return owners.Where(o => o.Complete).Select(o => o.Item).ToList();
    }

    /// <summary>
    /// A folder's own time goes last, deepest first: every file written into it moved it. As the copy
    /// between file systems does, and only when the timestamps are kept.
    /// </summary>
    private void StampFolders(Plan plan)
    {
        if (!_options.PreserveTimestamps)
        {
            return;
        }

        foreach (var (path, modified) in plan.Folders.OrderByDescending(f => f.Key.Length))
        {
            try
            {
                Directory.SetLastWriteTime(path, modified);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // A folder time is not worth failing the extraction for.
            }
        }
    }

    /// <summary>Remembers a folder this extraction makes or merges into, with its time in the archive.</summary>
    private static void NoteFolder(Plan plan, string path, DateTime modified)
    {
        if (modified != DateTime.MinValue)
        {
            plan.Folders[path] = modified;
        }
    }

    private static long TotalBytes(IReadOnlyList<ArchiveExtractItem> items, IReadOnlyList<ArchiveEntry> entries)
    {
        long total = 0;
        foreach (var item in items)
        {
            if (!item.IsDirectory)
            {
                total += item.Size;
                continue;
            }

            var prefix = item.SourcePath.TrimEnd('/') + "/";
            total += entries
                .Where(e => !e.IsDirectory &&
                            ArchiveFileSystem.NormalizePath(e.FullPath).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Sum(e => e.Size);
        }
        return total;
    }

    // ── Settling the names ────────────────────────────────────────────────────

    /// <summary>Plans one item: a file, or a folder merged into what is already there.</summary>
    private async Task PlanItemAsync(Plan plan, Owner owner, string destPath, CancellationToken ct)
    {
        var item = owner.Item;
        if (!item.IsDirectory)
        {
            await PlanFileAsync(plan, owner, item.SourcePath, item.DisplayName, item.Size, item.LastModified, destPath, ct);
            return;
        }

        // Folder onto folder merges; the conflicts that matter are the files inside.
        if (File.Exists(destPath))
        {
            var clash = new IOException(string.Format(LocalizationManager.Get("Fc_FileInTheWay"), item.DisplayName));
            await _op.GuardAsync(new VfsPath(destPath), () => Task.FromException(clash), ct);
            owner.Complete = false;
            return;
        }
        Directory.CreateDirectory(destPath);
        NoteFolder(plan, destPath, item.LastModified);

        var prefix = item.SourcePath.TrimEnd('/') + "/";
        foreach (var child in _entries)
        {
            ct.ThrowIfCancellationRequested();
            var childPath = ArchiveFileSystem.NormalizePath(child.FullPath);
            if (!childPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var childDest = ArchivePathSafety.ResolveDestination(destPath, childPath[prefix.Length..]);
            if (child.IsDirectory)
            {
                Directory.CreateDirectory(childDest);
                NoteFolder(plan, childDest, child.LastModified);
                continue;
            }

            await PlanFileAsync(plan, owner, child.FullPath, child.Name, child.Size, child.LastModified, childDest, ct);
        }
    }

    private async Task PlanFileAsync(
        Plan plan, Owner owner, string entryPath, string name, long size, DateTime modified, string destPath,
        CancellationToken ct)
    {
        var overwrite = false;
        var afterPass = false;

        // A destination another file of this extraction is headed for is as taken as one on the disk:
        // written one by one, the second file used to find the first one there.
        plan.Claimed.TryGetValue(destPath, out var earlier);
        if (earlier is not null || File.Exists(destPath) || Directory.Exists(destPath))
        {
            var chosen = await ResolveConflictAsync(plan, name, size, modified, destPath, earlier, ct);
            if (chosen is null)
            {
                _op.NoteSkipped();
                plan.Progress.AddSkipped(size, name);
                owner.Complete = false;
                return;
            }
            (destPath, overwrite) = chosen.Value;

            // Replacing a file this extraction writes itself has to wait until that one is there.
            afterPass = overwrite && plan.Claimed.ContainsKey(destPath);
        }

        // The pass hands each entry over once; a second copy of it is written afterwards.
        var key = ArchiveFileSystem.NormalizePath(entryPath);
        if (!afterPass && !plan.PassKeys.Add(key))
        {
            afterPass = true;
        }

        var step = new Step(owner, entryPath, key, name, size, modified, destPath, overwrite, afterPass);
        plan.Steps.Add(step);
        plan.Claimed[destPath] = step;
    }

    /// <summary>
    /// Asks what to do about a name that is taken. Null = leave that file out. Otherwise the path to
    /// write and whether it replaces a file.
    /// </summary>
    private async Task<(string Path, bool Overwrite)?> ResolveConflictAsync(
        Plan plan, string name, long size, DateTime modified, string destPath, Step? earlier, CancellationToken ct)
    {
        if (Directory.Exists(destPath))
        {
            throw new IOException(string.Format(LocalizationManager.Get("Fc_FolderInTheWay"), name));
        }

        // What will be there when this file is written: the file another entry brings, or the one on the disk.
        IFileEntry existing;
        if (earlier is not null)
        {
            existing = new FileEntry(new VfsPath(destPath), Path.GetFileName(destPath), false,
                earlier.Size, earlier.Modified, FileAttributes.Normal);
        }
        else
        {
            var fi = new FileInfo(destPath);
            existing = new FileEntry(new VfsPath(destPath), fi.Name, false, fi.Length, fi.LastWriteTime, fi.Attributes);
        }
        var source = new FileEntry(new VfsPath(name), name, false, size, modified, FileAttributes.Normal);

        // The same rule as the copy between file systems: "only if newer" first, then the strategy.
        var resolution = await ConflictPolicy.ResolveAsync(
            new ConflictInfo(source, existing, new VfsPath(destPath)), _options.OnlyIfNewer,
            _conflicts.ResolveAsync, ct);

        switch (resolution)
        {
            case ConflictResolution.Skip:
                return null;
            case ConflictResolution.Cancel:
                throw new OperationCanceledException();
            case ConflictResolution.RenameAuto:
                var directory = Path.GetDirectoryName(destPath)!;
                var freeName = RenameAutoStrategy.GenerateNewName(Path.GetFileName(destPath), candidate =>
                {
                    var path = Path.Combine(directory, candidate);
                    return File.Exists(path) || Directory.Exists(path) || plan.Claimed.ContainsKey(path);
                });
                return (Path.Combine(directory, freeName), false);
            default:
                return (destPath, true);
        }
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the archive once for every file that can go in a single pass. An error writing one of
    /// them is put to the user right there, while the pass waits. When the engine itself fails, what
    /// it did not deliver is left for the one-by-one extraction that follows.
    /// </summary>
    private async Task ExtractInOnePassAsync(Plan plan, CancellationToken ct)
    {
        var pass = plan.Steps.Where(s => !s.AfterPass).ToDictionary(s => s.Key, StringComparer.Ordinal);
        if (pass.Count == 0)
        {
            return;
        }

        ExceptionDispatchInfo? writeFailure = null;
        try
        {
            await Task.Run(() => _engine.ReadEntries(_archivePath, [.. pass.Keys], (requested, content) =>
            {
                try
                {
                    if (pass.TryGetValue(requested, out var step) && !step.Started)
                    {
                        Extract(plan, step, content, ct);
                    }
                }
                catch (Exception ex)
                {
                    writeFailure = ExceptionDispatchInfo.Capture(ex);
                    throw;
                }
            }, ct), ct);
        }
        catch (Exception) when (writeFailure is not null)
        {
            // An abort, a cancel or a failure while writing ends the operation, whatever the engine made of it.
            writeFailure.Throw();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The engine gave up on its own: a missing entry, a damaged block, a password. Each file it did
            // not deliver is extracted alone next and reports its own error, as it did before the pass.
        }
    }

    /// <summary>
    /// Extracts one file under the operation's retry / skip / abort prompt. Runs on a worker thread:
    /// inside the pass the engine's thread waits for the prompt and for a pause. <paramref name="content"/>
    /// is the pass's stream; without it — and on a retry, since the pass cannot rewind — the entry is
    /// read on its own.
    /// </summary>
    private void Extract(Plan plan, Step step, Stream? content, CancellationToken ct)
    {
        step.Started = true;
        var relay = plan.Progress.CreateFileRelay(step.Name);
        var written = _op.GuardAsync(new VfsPath(step.Dest), () =>
        {
            var source = content;
            content = null;
            using var own = source is null ? _engine.ExtractEntry(_archivePath, step.EntryPath) : null;
            Write(step, source ?? own!, relay, ct);
            return Task.CompletedTask;
        }, ct).GetAwaiter().GetResult();

        if (!written)
        {
            relay.SkipRemaining(step.Size);
            step.Owner.Complete = false;
        }
    }

    /// <summary>
    /// Writes one entry to disk. A file that is being replaced is written beside it and swapped in
    /// at the end, and a half-written new file is removed again: a cancelled extraction leaves the
    /// old content, or nothing, never a truncated file.
    /// </summary>
    private void Write(Step step, Stream content, IProgress<FileProgress> relay, CancellationToken ct)
    {
        var destPath = step.Dest;
        var directory = Path.GetDirectoryName(destPath)!;
        Directory.CreateDirectory(directory);

        var swap = step.Overwrite && File.Exists(destPath);
        var target = swap
            ? Path.Combine(directory, $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.tcvtmp")
            : destPath;
        var created = false;
        var finished = false;

        try
        {
            // CreateNew: the name was settled before the first byte was read, and a file that has
            // turned up there since is not ours to truncate. It becomes an error the user can answer.
            using (var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                created = true;
                var buffer = new byte[BufferSize];
                var sw = Stopwatch.StartNew();
                long copied = 0;
                int read;
                while ((read = content.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    // Blocking on purpose: this thread is the archive reader's, and a pause has to hold it.
                    _op.PauseHandle?.WaitAsync(ct).GetAwaiter().GetResult();
                    file.Write(buffer, 0, read);
                    copied += read;
                    relay.Report(new FileProgress(copied, step.Size, step.Name, sw.Elapsed));
                }
            }
            finished = true;
        }
        finally
        {
            if (created && !finished)
            {
                try { File.Delete(target); }
                catch { /* the error that got us here is the one worth reporting */ }
            }
        }

        if (swap)
        {
            var attrs = File.GetAttributes(destPath);
            try
            {
                if (attrs.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(destPath, attrs & ~FileAttributes.ReadOnly);
                }

                File.Move(target, destPath, overwrite: true);
            }
            catch
            {
                // The old file is still there (another program holds it open, say): it keeps its
                // flags, and the finished copy beside it goes — or every Retry would leave one more
                // .tcvtmp in the user's folder, and Skip would leave it there for good.
                try { File.SetAttributes(destPath, attrs); }
                catch { /* the move's error is the one worth reporting */ }
                try { File.Delete(target); }
                catch { /* same */ }
                throw;
            }
        }

        // "Keep the dates" off: the file keeps the time it was written, as a copy does.
        if (_options.PreserveTimestamps && step.Modified != DateTime.MinValue)
        {
            try { File.SetLastWriteTime(destPath, step.Modified); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
            {
                // A timestamp the file system cannot hold is not worth failing the extraction for.
            }
        }
    }

    // ── State ─────────────────────────────────────────────────────────────────

    /// <summary>A selected item. It landed when every file below it did.</summary>
    private sealed class Owner(ArchiveExtractItem item)
    {
        public ArchiveExtractItem Item { get; } = item;
        public bool Complete { get; set; } = true;
    }

    /// <summary>One file to write, its name settled.</summary>
    private sealed record Step(
        Owner Owner, string EntryPath, string Key, string Name, long Size, DateTime Modified,
        string Dest, bool Overwrite, bool AfterPass)
    {
        /// <summary>Taken on by the pass or by the one-by-one extraction; never by both.</summary>
        public bool Started { get; set; }
    }

    private sealed class Plan(AggregateProgress progress)
    {
        public AggregateProgress Progress { get; } = progress;

        /// <summary>In the order the files were settled.</summary>
        public List<Step> Steps { get; } = [];

        /// <summary>Destinations already headed for, with the latest file headed there.</summary>
        public Dictionary<string, Step> Claimed { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Entries the pass already asks for.</summary>
        public HashSet<string> PassKeys { get; } = new(StringComparer.Ordinal);

        /// <summary>Folders made or merged into, with their time in the archive.</summary>
        public Dictionary<string, DateTime> Folders { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
