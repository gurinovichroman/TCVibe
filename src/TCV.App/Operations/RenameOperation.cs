using System.IO;
using Microsoft.Extensions.Logging;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// A batch of renames inside one directory, run through <see cref="OperationQueue"/> so it is
/// recorded in the operation log like every other file mutation (INV-10) and can be taken back
/// (<see cref="UndoAsync"/>). Used by the AI rename, whose whole contract is «preview before, undo
/// after» (ADR-0008).
/// <para>
/// Names may depend on each other — <c>a → b</c> and <c>b → c</c> in one batch, or a swap. Doing them in
/// the order given would fail on the first one (its target still exists) or, worse, overwrite; so the
/// renames whose target is another item's source go through a temporary name first.
/// </para>
/// </summary>
public sealed class RenameOperation : FileOperationBase
{
    private readonly IVirtualFileSystem _fs;
    private readonly VfsPath _directory;
    private readonly IReadOnlyList<(string From, string To)> _pairs;
    private readonly ILogger _logger;
    private readonly List<(string From, string To)> _done = new();
    private readonly List<string> _failures = new();

    public RenameOperation(
        IVirtualFileSystem fs,
        VfsPath directory,
        IReadOnlyList<(string From, string To)> pairs,
        ILogger logger)
    {
        _fs = fs;
        _directory = directory;
        _pairs = pairs;
        _logger = logger;
    }

    /// <summary>Renames that actually happened (current name, original name reversed for undo).</summary>
    public IReadOnlyList<(string From, string To)> Performed => _done;

    /// <summary>One line per rename that failed; the rest of the batch still ran.</summary>
    public IReadOnlyList<string> Failures => _failures;

    public override OperationLogDescriptor LogDescriptor =>
        (_pairs.Count == 1
            ? new OperationLogDescriptor(OperationKind.Rename, _directory.Combine(_pairs[0].From).Value, _pairs[0].To, 1)
            : new OperationLogDescriptor(OperationKind.Rename, $"{_directory.Value} ({_pairs.Count})", null, _pairs.Count))
        with
        {
            // What actually happened, not what was asked: the log line is how the batch is taken back
            // once the window that applied it is closed (6.55). Read after the run, by the queue.
            Renames = _done.Count == 0
                ? null
                : new RenameBatchRecord(_directory.Value, _done.Select(d => new RenamedName(d.From, d.To)).ToList()),
        };

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            await ApplyAsync(_pairs, record: true, ct);

            if (_done.Count == 0 && _failures.Count > 0)
            {
                State = OperationState.Failed;
                FailureException = new IOException(_failures[0]);
                return;
            }

            State = OperationState.Completed;
            if (_failures.Count > 0)
                FailureException = new IOException($"{_failures.Count} not renamed. First: {_failures[0]}");
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
    /// The batch that takes this one back, as an operation of its own: an undo changes the folder as
    /// much as the rename did, so it goes through the queue and the log too (INV-10). Otherwise the
    /// «Agent changes» dashboard would find the names changing back with no line to explain it.
    /// </summary>
    public RenameOperation CreateUndo() =>
        new(_fs, _directory, _done.Select(d => (From: d.To, To: d.From)).ToList(), _logger);

    /// <summary>How many of this batch's renames did not happen (failed, or never ran).</summary>
    public int NotPerformedCount => _pairs.Count - _done.Count;

    /// <summary>
    /// Renames everything that was renamed back. Best effort, like the batch itself: returns how many
    /// could not be restored (a name someone took in the meantime).
    /// </summary>
    public async Task<int> UndoAsync(CancellationToken ct = default)
    {
        var inverse = _done.Select(d => (From: d.To, To: d.From)).ToList();
        var before = _failures.Count;

        await ApplyAsync(inverse, record: false, ct);

        var failed = _failures.Count - before;
        _done.Clear();
        return failed;
    }

    private async Task ApplyAsync(
        IReadOnlyList<(string From, string To)> pairs, bool record, CancellationToken ct)
    {
        var sources = new HashSet<string>(pairs.Select(p => p.From), StringComparer.OrdinalIgnoreCase);

        // A case-only change (a.jpg → A.jpg) has its own source as target and is not a dependency.
        bool DependsOnAnother((string From, string To) p) =>
            !string.Equals(p.From, p.To, StringComparison.OrdinalIgnoreCase) && sources.Contains(p.To);

        var direct = pairs.Where(p => !DependsOnAnother(p)).ToList();
        var chained = pairs.Where(DependsOnAnother).ToList();

        // Phase 1 for the chained ones: out of the way, under a name nobody asked for.
        var parked = new List<(string Temp, string To, string From)>();
        foreach (var pair in chained)
        {
            ct.ThrowIfCancellationRequested();
            var temp = $".tcv-rename-{Guid.NewGuid():n}";

            if (await TryRenameAsync(pair.From, temp, ct)) parked.Add((temp, pair.To, pair.From));
        }

        foreach (var pair in direct)
        {
            ct.ThrowIfCancellationRequested();
            if (await TryRenameAsync(pair.From, pair.To, ct) && record) _done.Add((pair.From, pair.To));
        }

        // Phase 2: from the parked name to the final one. If even that fails the file is put back, so
        // a failed batch never leaves a hidden temporary name behind.
        foreach (var (temp, to, from) in parked)
        {
            ct.ThrowIfCancellationRequested();

            if (await TryRenameAsync(temp, to, ct))
            {
                if (record) _done.Add((from, to));
            }
            else
            {
                await TryRenameAsync(temp, from, CancellationToken.None);
            }
        }
    }

    private async Task<bool> TryRenameAsync(string from, string to, CancellationToken ct)
    {
        try
        {
            await _fs.RenameAsync(_directory.Combine(from), to, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _failures.Add($"{from}: {ex.Message}");
            _logger.LogWarning(ex, "Rename of {From} to {To} failed", from, to);
            return false;
        }
    }
}
