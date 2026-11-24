namespace TCV.Archives;

/// <summary>
/// A rename inside an archive would land on a name that is taken. A type of its own, so that the window
/// says it in the user's language; the message is what is left for a log.
/// </summary>
public sealed class ArchiveNameTakenException(string name)
    : IOException($"The name '{name}' is already taken in the archive.")
{
    /// <summary>The entry's path as it would have been.</summary>
    public string Name { get; } = name;
}
