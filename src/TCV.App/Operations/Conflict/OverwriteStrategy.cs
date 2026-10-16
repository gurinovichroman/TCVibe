namespace TCV.App.Operations.Conflict;

public sealed class OverwriteStrategy : IConflictStrategy
{
    public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct) =>
        Task.FromResult(ConflictResolution.Overwrite);
}
