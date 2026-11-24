using TCV.Core.Interfaces;
using TCV.MultiRename;

namespace TCV.App.Operations;

/// <summary>
/// A Multi-Rename run (or its undo) as a queued file operation, so it lands in the operation log
/// like every other change to the user's files (INV-10). The renaming itself — ordering, cycles,
/// partial failure — is <see cref="RenameEngine"/>'s; this only reports the outcome to the queue.
/// </summary>
public sealed class RenameBatchOperation : FileOperationBase
{
    private readonly Func<CancellationToken, Task<RenameBatchResult>> _run;
    private readonly string _source;
    private readonly int _itemCount;
    private readonly IVirtualFileSystem? _fileSystem;

    /// <param name="run">Performs the renames (usually a call into <see cref="RenameEngine"/>).</param>
    /// <param name="source">The folder being renamed in, shown in the log.</param>
    /// <param name="itemCount">How many files were selected.</param>
    /// <param name="fileSystem">Where the files are: a batch on a server waits in the network lane, not behind the disk's work.</param>
    public RenameBatchOperation(
        Func<CancellationToken, Task<RenameBatchResult>> run, string source, int itemCount, IVirtualFileSystem? fileSystem = null)
    {
        _run = run;
        _source = source;
        _itemCount = itemCount;
        _fileSystem = fileSystem;
    }

    /// <summary>The archive file the batch rewrites, when it renames inside one (TRAP-62).</summary>
    public string? ChangedArchive { get; init; }

    /// <summary>What the run did; null until it has finished.</summary>
    public RenameBatchResult? Result { get; private set; }

    /// <summary>The log line can take this batch back: the files are on the disk, on <see cref="Server"/> or in <see cref="ChangedArchive"/>.</summary>
    public bool Undoable { get; init; }

    /// <summary>The server's address when the files are on one (<c>sftp://user@host</c>): a path alone does not say which.</summary>
    public string? Server { get; init; }

    public override OperationLane Lane => _fileSystem is null ? OperationLane.Local : LaneOf(_fileSystem);

    public override OperationLogDescriptor LogDescriptor =>
        new(OperationKind.Rename, _source, null, Result?.Performed.Count ?? _itemCount)
        {
            ChangedArchive = ChangedArchive,

            // What actually happened, not what was asked: with the window gone, this line is the undo.
            Renames = Undoable ? RecordOf(Result) : null,
        };

    private RenameBatchRecord? RecordOf(RenameBatchResult? result)
    {
        if (result is not { Performed.Count: > 0 })
        {
            return null;
        }

        var folders = result.Performed.Select(a => a.CurrentPath.Parent?.Value ?? string.Empty).Distinct().ToList();
        return folders.Count == 1 && folders[0].Length > 0
            ? new RenameBatchRecord(
                folders[0], result.Performed.Select(a => new RenamedName(a.OriginalName, a.CurrentPath.FileName)).ToList())
            {
                Server = Server,
                Archive = ChangedArchive,
            }
            : null;
    }

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            Result = await _run(ct);

            if (Result.Cancelled)
            {
                State = OperationState.Cancelled;
            }
            else if (Result.Failures.Count > 0)
            {
                State = OperationState.Failed;
                FailureException = new RenameBatchException(Result);
            }
            else
            {
                State = OperationState.Completed;
            }
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
}
