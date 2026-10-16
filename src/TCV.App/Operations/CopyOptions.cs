namespace TCV.App.Operations;

public enum ConflictMode
{
    Ask,
    Overwrite,
    Skip,
    RenameAuto,
}

/// <param name="PreserveTimestamps">
/// On by default: a copy that carries "now" as its modification time looks like fresh work, which
/// misleads sorting, build tools and the agent-attribution window alike.
/// </param>
public record CopyOptions(
    ConflictMode ConflictMode = ConflictMode.Ask,
    bool PreserveTimestamps = true,
    bool OnlyIfNewer = false,
    int Threads = 1);

/// <summary>
/// The options of the copy dialog a transfer can honour; a box that would be ignored is not shown.
/// An archive is read in one pass and rewritten in one call, so threads mean nothing to it, and it
/// stores every file's time by itself, so adding to one has no "keep the dates" to choose (TRAP-58).
/// </summary>
[Flags]
public enum CopyOptionSet
{
    None = 0,
    Timestamps = 1,
    OnlyIfNewer = 2,
    Parallel = 4,
    All = Timestamps | OnlyIfNewer | Parallel,
}
