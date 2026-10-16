namespace TCV.App.Operations.Conflict;

public sealed class SkipStrategy : IConflictStrategy
{
    public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct) =>
        Task.FromResult(ConflictResolution.Skip);
}
