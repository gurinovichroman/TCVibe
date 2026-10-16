namespace TCV.App.Operations.Conflict;

/// <summary>
/// Asks the user how to resolve a conflict via a callback.
/// The callback is typically wired to the progress dialog.
/// </summary>
public sealed class AskUserStrategy : IConflictStrategy
{
    private readonly Func<ConflictInfo, CancellationToken, Task<ConflictResolution>> _ask;

    public AskUserStrategy(Func<ConflictInfo, CancellationToken, Task<ConflictResolution>> ask)
    {
        _ask = ask;
    }

    public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct) =>
        _ask(conflict, ct);
}
