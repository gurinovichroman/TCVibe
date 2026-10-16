using TCV.Core.Interfaces;

namespace TCV.App.Operations;

/// <summary>
/// Runs an arbitrary piece of work as a queued, logged operation. For mutations whose engine is not
/// an <c>IVirtualFileSystem</c> (adding to or deleting from an archive, extracting one) but which
/// must still show up in the log and never run beside another operation (INV-10).
/// </summary>
public sealed class DelegateOperation : FileOperationBase
{
    private readonly OperationLogDescriptor _descriptor;
    private readonly Func<DelegateOperation, CancellationToken, Task> _work;

    public DelegateOperation(
        OperationLogDescriptor descriptor, Func<DelegateOperation, CancellationToken, Task> work)
    {
        _descriptor = descriptor;
        _work = work;
    }

    public override OperationLogDescriptor LogDescriptor => _descriptor;

    /// <summary>Progress sink for the work to report through; set by the caller before enqueueing.</summary>
    public IProgress<TCV.Core.Domain.FileProgress>? Progress { get; set; }

    /// <summary>The work counts an item it left alone, so a caller can tell a whole result from a partial one.</summary>
    public void ReportSkipped(string reason)
    {
        CountSkipped();
        AddWarning(reason);
    }

    /// <summary>Counts an item the user chose to leave out. Unlike <see cref="ReportSkipped"/> it adds no warning: the user already knows.</summary>
    public void NoteSkipped() => CountSkipped();

    /// <summary>Asks the operation's error handler what to do; used by work that runs its own loops.</summary>
    public Task<bool> GuardAsync(TCV.Core.Domain.VfsPath item, Func<Task> action, CancellationToken ct) =>
        RunGuardedAsync(item, action, ct);

    public override async Task ExecuteAsync(CancellationToken ct)
    {
        State = OperationState.Running;
        try
        {
            await _work(this, ct);
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
}
