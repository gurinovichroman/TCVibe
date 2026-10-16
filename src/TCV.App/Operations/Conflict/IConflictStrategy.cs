namespace TCV.App.Operations.Conflict;

public enum ConflictResolution
{
    Overwrite,
    OverwriteIfNewer,
    Skip,
    RenameAuto,
    Cancel,
}

public interface IConflictStrategy
{
    Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct);
}
