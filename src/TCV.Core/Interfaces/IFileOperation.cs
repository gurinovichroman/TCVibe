namespace TCV.Core.Interfaces;

public enum OperationState
{
    Pending,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled,
}

public interface IFileOperation
{
    Guid Id { get; }
    OperationState State { get; }
    Task ExecuteAsync(CancellationToken ct);
}
