namespace TCV.Core.Interfaces;

public interface IOperationQueue
{
    Task EnqueueAsync(IFileOperation op, CancellationToken ct = default);
    void PauseAll();
    void ResumeAll();
    void CancelAll();
    IReadOnlyList<IFileOperation> Running { get; }
    IReadOnlyList<IFileOperation> Pending { get; }
}
