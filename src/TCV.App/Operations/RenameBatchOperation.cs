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

    /// <param name="run">Performs the renames (usually a call into <see cref="RenameEngine"/>).</param>
    /// <param name="source">The folder being renamed in, shown in the log.</param>
    /// <param name="itemCount">How many files were selected.</param>
    public RenameBatchOperation(Func<CancellationToken, Task<RenameBatchResult>> run, string source, int itemCount)
    {
        _run = run;
        _source = source;
        _itemCount = itemCount;
    }

    /// <summary>What the run did; null until it has finished.</summary>
    public RenameBatchResult? Result { get; private set; }

    public override OperationLogDescriptor LogDescriptor =>
        new(OperationKind.Rename, _source, null, Result?.Performed.Count ?? _itemCount);

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
