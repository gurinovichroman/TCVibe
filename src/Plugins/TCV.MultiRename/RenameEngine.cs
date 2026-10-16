using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.MultiRename;

/// <summary>One rename that did not happen, and the file system's reason.</summary>
public sealed record RenameFailure(string OldName, string NewName, string Error);

/// <summary>
/// What a rename batch did. A batch is never all-or-nothing: whatever was renamed stays renamed,
/// is listed in <see cref="Performed"/> and can be undone, and every file that could not be
/// renamed is listed in <see cref="Failures"/> with its reason.
/// </summary>
public sealed record RenameBatchResult(
    IReadOnlyList<RenameAction> Performed,
    IReadOnlyList<RenameFailure> Failures,
    bool Cancelled)
{
    public bool Succeeded => Failures.Count == 0 && !Cancelled;
}

/// <summary>Thrown by <see cref="RenameEngine.ExecuteAsync"/> when part of a batch failed; carries what did succeed.</summary>
public sealed class RenameBatchException : Exception
{
    public RenameBatchException(RenameBatchResult result)
        : base($"{result.Failures.Count} of {result.Performed.Count + result.Failures.Count} renames failed: {result.Failures[0].Error}")
    {
        Result = result;
    }

    public RenameBatchResult Result { get; }
}

/// <summary>
/// Orchestrates preview generation and actual file rename execution.
/// </summary>
public sealed class RenameEngine
{
    private const int MaxNameLength = 255;

    private static readonly char[] _invalidChars = System.IO.Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> _reservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly IVirtualFileSystem _fileSystem;

    public RenameEngine(IVirtualFileSystem fileSystem)
    {
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    }

    // ── Preview ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a preview of what each file will be renamed to.
    /// </summary>
    /// <param name="files">Files to rename.</param>
    /// <param name="template">Template to apply to each file.</param>
    /// <param name="rules">Additional post-processing rules applied in order.</param>
    /// <param name="counterStart">Starting value for the [C] counter (default 1).</param>
    /// <param name="counterStep">Increment step for the [C] counter (default 1).</param>
    /// <param name="referenceDate">Date used for [D]/[T] placeholders; defaults to now.</param>
    /// <param name="otherNames">
    /// Names already in the folder that are <em>not</em> part of the batch. A new name that lands on
    /// one of them would fail (or, on some file systems, overwrite), so it is reported as a conflict.
    /// </param>
    /// <param name="counterWidth">Digits a counter is zero-padded to (0 = none).</param>
    /// <param name="nameTaken">
    /// Per-folder form of <paramref name="otherNames"/> for a batch that spans several folders: true
    /// when the new name is already held, in the file's own folder, by an entry outside the batch.
    /// </param>
    public IReadOnlyList<RenamePreviewItem> Preview(
        IReadOnlyList<IFileEntry> files,
        RenameTemplate template,
        IReadOnlyList<IRenameRule>? rules = null,
        int counterStart = 1,
        int counterStep = 1,
        DateTime? referenceDate = null,
        IReadOnlySet<string>? otherNames = null,
        int counterWidth = 0,
        Func<IFileEntry, string, bool>? nameTaken = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(template);

        var date = referenceDate ?? DateTime.Now;
        var newNames = new string[files.Count];

        for (var i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var counter = counterStart + i * counterStep;
            var name = template.Evaluate(new RenameContext(file, counter, date, i, counterWidth));

            // Apply post-processing rules
            if (rules is not null)
            {
                foreach (var rule in rules)
                {
                    name = rule is IEntryAwareRenameRule aware ? aware.Apply(name, file.IsDirectory) : rule.Apply(name);
                }
            }

            newNames[i] = name;
        }

        // A file that keeps its name still occupies it: another file may not be renamed onto it.
        // Names are only taken within one folder: a batch fed from search results spans several
        // (TRAP-28), and two README.md in different folders may both become README.old.md.
        var occupied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < files.Count; i++)
        {
            if (string.Equals(newNames[i], files[i].Name, StringComparison.Ordinal))
            {
                occupied.Add(Key(files[i].Path.Parent, newNames[i]));
            }
        }

        var results = new List<RenamePreviewItem>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < files.Count; i++)
        {
            var oldName = files[i].Name;
            var newName = newNames[i];

            // Nothing to do is nothing to conflict with — even if the name would not be valid today.
            if (string.Equals(newName, oldName, StringComparison.Ordinal))
            {
                results.Add(new RenamePreviewItem(oldName, newName, false));
                continue;
            }

            var kind = Validate(newName);
            var key = Key(files[i].Path.Parent, newName);
            if (kind == ConflictKind.None && (occupied.Contains(key) || !seen.Add(key)))
            {
                kind = ConflictKind.Duplicate;
            }

            if (kind == ConflictKind.None &&
                ((otherNames is not null && otherNames.Contains(newName)) ||
                 (nameTaken is not null && nameTaken(files[i], newName))))
            {
                kind = ConflictKind.ExistsInFolder;
            }

            results.Add(new RenamePreviewItem(oldName, newName, kind != ConflictKind.None, kind));
        }

        return results;
    }

    /// <summary>The first thing wrong with a proposed file name, or <see cref="ConflictKind.None"/>.</summary>
    public static ConflictKind Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return ConflictKind.Empty;
        }

        if (name.Length > MaxNameLength)
        {
            return ConflictKind.TooLong;
        }

        // Windows silently drops a trailing dot or space, which would make the name something else
        // than what the preview shows (and can make two different names collide).
        if (name.IndexOfAny(_invalidChars) >= 0 || name.EndsWith('.') || name.EndsWith(' ') || name.StartsWith(' '))
        {
            return ConflictKind.InvalidName;
        }

        var dot = name.IndexOf('.');
        if (_reservedNames.Contains(dot < 0 ? name : name[..dot]))
        {
            return ConflictKind.InvalidName;
        }

        return ConflictKind.None;
    }

    // ── Execute ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Executes the renames described in <paramref name="previews"/>, skipping items with conflicts.
    /// Returns the renames actually performed so they can be reverted with <see cref="UndoAsync"/>.
    /// </summary>
    /// <exception cref="RenameBatchException">
    /// Some renames failed. The exception carries the full result, including what <em>was</em> renamed.
    /// </exception>
    public async Task<IReadOnlyList<RenameAction>> ExecuteAsync(
        IReadOnlyList<IFileEntry> files,
        IReadOnlyList<RenamePreviewItem> previews,
        CancellationToken ct = default)
    {
        var result = await ExecuteBatchAsync(files, previews, ct);
        if (result.Cancelled)
        {
            throw new OperationCanceledException(ct);
        }

        if (result.Failures.Count > 0)
        {
            throw new RenameBatchException(result);
        }

        return result.Performed;
    }

    /// <summary>
    /// Executes the batch and reports everything that happened instead of throwing at the first
    /// problem: a rename that fails does not stop the others, and the ones that succeeded are
    /// returned (so they can be undone) whatever happens to the rest.
    /// </summary>
    /// <remarks>
    /// Names that depend on each other are ordered so no rename lands on a name that is still
    /// taken: a→b with b→c runs b→c first, and a swap or longer cycle (a→b, b→a) parks one file
    /// under a temporary name for the moment it takes.
    /// </remarks>
    public async Task<RenameBatchResult> ExecuteBatchAsync(
        IReadOnlyList<IFileEntry> files,
        IReadOnlyList<RenamePreviewItem> previews,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(previews);

        if (files.Count != previews.Count)
        {
            throw new ArgumentException("Files and previews must have the same count.");
        }

        var steps = new List<Step>();
        for (var i = 0; i < files.Count; i++)
        {
            var preview = previews[i];
            if (preview.HasConflict || string.Equals(preview.OldName, preview.NewName, StringComparison.Ordinal))
            {
                continue;
            }

            steps.Add(new Step(files[i].Path, preview.OldName, preview.NewName));
        }

        return await ApplyAsync(steps, ct);
    }

    /// <summary>
    /// Reverts a batch produced by <see cref="ExecuteAsync"/>, renaming each file back to its
    /// original name. Best-effort: returns how many reverts failed — e.g. a name someone else
    /// claimed in the meantime. Undoing a swap works too: it is just another set of renames.
    /// </summary>
    public async Task<int> UndoAsync(IReadOnlyList<RenameAction> actions, CancellationToken ct = default)
    {
        var result = await UndoBatchAsync(actions, ct);
        return actions.Count - result.Performed.Count;
    }

    /// <summary>As <see cref="UndoAsync"/>, but reports everything that happened (for the operation log).</summary>
    public Task<RenameBatchResult> UndoBatchAsync(IReadOnlyList<RenameAction> actions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actions);

        var steps = actions
            .Select(a => new Step(a.CurrentPath, a.CurrentPath.FileName, a.OriginalName))
            .ToList();

        return ApplyAsync(steps, ct);
    }

    // ── The ordering engine ───────────────────────────────────────────────────

    private sealed class Step(VfsPath path, string oldName, string newName)
    {
        public VfsPath CurrentPath { get; set; } = path;
        public string CurrentName { get; set; } = oldName;
        public string OriginalName { get; } = oldName;
        public string NewName { get; } = newName;
        public bool OnTemporaryName { get; set; }
        public VfsPath? Parent => CurrentPath.Parent;
    }

    private static string Key(VfsPath? parent, string name) =>
        $"{parent?.Value ?? string.Empty}|{name}";

    private async Task<RenameBatchResult> ApplyAsync(List<Step> plan, CancellationToken ct)
    {
        if (plan.Count > 0 && _fileSystem is IBatchRenameFileSystem { CanRenameAsBatch: true } batch)
        {
            return await ApplyAsOneBatchAsync(batch, plan, ct);
        }

        var performed = new List<RenameAction>();
        var failures = new List<RenameFailure>();
        var pending = new List<Step>(plan);

        // The names pending files currently hold. A rename waits only on a name that a file which
        // has yet to move is still holding; a name held by a file that failed or finished is not
        // going to be freed by waiting, so the rename is simply tried and the file system decides.
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in pending)
        {
            held.Add(Key(step.Parent, step.CurrentName));
        }

        var cancelled = false;
        try
        {
            while (pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var progressed = false;

                for (var i = 0; i < pending.Count;)
                {
                    var step = pending[i];
                    var selfKey = Key(step.Parent, step.CurrentName);
                    var targetKey = Key(step.Parent, step.NewName);

                    // The target is still some pending file's name: wait until that file has moved
                    // on. (Its own current name is fine — that is a change of case only.)
                    if (held.Contains(targetKey) && !string.Equals(targetKey, selfKey, StringComparison.OrdinalIgnoreCase))
                    {
                        i++;
                        continue;
                    }

                    try
                    {
                        await _fileSystem.RenameAsync(step.CurrentPath, step.NewName, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failures.Add(new RenameFailure(step.OriginalName, step.NewName, ex.Message));
                        held.Remove(selfKey);
                        pending.RemoveAt(i);
                        progressed = true;

                        // Parked under a temporary name and could not move on: put it back.
                        if (step.OnTemporaryName)
                        {
                            await RestoreParkedAsync(step, failures);
                        }

                        continue;
                    }

                    held.Remove(selfKey);
                    performed.Add(new RenameAction(
                        step.Parent is null ? new VfsPath(step.NewName) : step.Parent.Combine(step.NewName),
                        step.OriginalName));
                    pending.RemoveAt(i);
                    progressed = true;
                }

                if (!progressed && pending.Count > 0)
                {
                    await ParkOneAsync(pending, held, failures, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            // Nothing may be left under a temporary name, whatever stopped the batch.
            foreach (var step in pending.Where(s => s.OnTemporaryName))
            {
                await RestoreParkedAsync(step, failures);
            }
        }

        return new RenameBatchResult(performed, failures, cancelled);
    }

    /// <summary>
    /// A file system that renames a batch in one step (an archive, rewritten once instead of once per
    /// file) gets the whole plan at once. The renames apply together, so the ordering and the temporary
    /// names above are not needed, and the outcome is all or nothing: everything renamed, or nothing
    /// and every file reported with the one reason.
    /// </summary>
    private static async Task<RenameBatchResult> ApplyAsOneBatchAsync(
        IBatchRenameFileSystem fileSystem, List<Step> plan, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await fileSystem.RenameBatchAsync(plan.Select(s => (s.CurrentPath, s.NewName)).ToList(), ct);
        }
        catch (OperationCanceledException)
        {
            return new RenameBatchResult([], [], Cancelled: true);
        }
        catch (Exception ex)
        {
            return new RenameBatchResult(
                [], plan.Select(s => new RenameFailure(s.OriginalName, s.NewName, ex.Message)).ToList(), Cancelled: false);
        }

        // Where each entry is now: its new name, under the new name of any folder above it that the batch
        // renamed too (archive search hits can hold a folder and a file inside it). Recorded under the
        // old parent, the undo — one all-or-nothing batch again — would miss that file and undo nothing.
        var newNames = new Dictionary<VfsPath, string>();
        foreach (var step in plan)
        {
            newNames.TryAdd(step.CurrentPath, step.NewName);
        }

        VfsPath Arrived(VfsPath path)
        {
            var renamed = newNames.TryGetValue(path, out var newName);
            if (path.Parent is not { } parent)
            {
                return renamed ? new VfsPath(newName!) : path;
            }

            var parentNow = Arrived(parent);
            return renamed || !ReferenceEquals(parentNow, parent)
                ? parentNow.Combine(renamed ? newName! : path.FileName)
                : path;
        }

        var performed = plan
            .Select(s => new RenameAction(Arrived(s.CurrentPath), s.OriginalName))
            .ToList();
        return new RenameBatchResult(performed, [], Cancelled: false);
    }

    /// <summary>
    /// Every remaining rename waits on another remaining rename, so somewhere there is a cycle
    /// (a→b, b→a). Moving one file <em>of that cycle</em> to a temporary name frees its old name and
    /// lets the cycle unwind. (A file that merely waits on the cycle would gain nothing.)
    /// </summary>
    private async Task ParkOneAsync(List<Step> pending, HashSet<string> held, List<RenameFailure> failures, CancellationToken ct)
    {
        var step = FindCycleMember(pending);
        var temporary = ".tcv-rename-" + Guid.NewGuid().ToString("N")[..8];
        var oldKey = Key(step.Parent, step.CurrentName);

        try
        {
            await _fileSystem.RenameAsync(step.CurrentPath, temporary, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failures.Add(new RenameFailure(step.OriginalName, step.NewName, ex.Message));
            held.Remove(oldKey);
            pending.Remove(step);
            return;
        }

        held.Remove(oldKey);
        step.CurrentPath = step.Parent is null ? new VfsPath(temporary) : step.Parent.Combine(temporary);
        step.CurrentName = temporary;
        step.OnTemporaryName = true;
        held.Add(Key(step.Parent, temporary));
    }

    /// <summary>Follows "waits on the file holding my target name" until it revisits a file — that file is on the cycle.</summary>
    private static Step FindCycleMember(List<Step> pending)
    {
        var current = pending[0];
        var visited = new HashSet<Step>();
        while (visited.Add(current))
        {
            var target = Key(current.Parent, current.NewName);
            var next = pending.FirstOrDefault(s =>
                !ReferenceEquals(s, current) &&
                string.Equals(Key(s.Parent, s.CurrentName), target, StringComparison.OrdinalIgnoreCase));
            if (next is null)
            {
                return current;
            }

            current = next;
        }

        return current;
    }

    private async Task RestoreParkedAsync(Step step, List<RenameFailure> failures)
    {
        try
        {
            await _fileSystem.RenameAsync(step.CurrentPath, step.OriginalName, CancellationToken.None);
        }
        catch (Exception ex)
        {
            failures.Add(new RenameFailure(step.OriginalName, step.NewName,
                $"left under the temporary name \"{step.CurrentName}\": {ex.Message}"));
        }
    }
}
