using System.IO;
using TCV.Core.Domain;

namespace TCV.App.Operations.Conflict;

public sealed class RenameAutoStrategy : IConflictStrategy
{
    public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct) =>
        Task.FromResult(ConflictResolution.RenameAuto);

    /// <summary>
    /// Splits a name into the part that gets the " (n)" suffix and the extension that follows it.
    /// A folder has no extension ("v1.2" stays whole), and neither has a dotfile: ".gitignore" is a
    /// name, not an empty name with an extension.
    /// </summary>
    public static (string Stem, string Extension) SplitName(string name, bool isDirectory = false)
    {
        if (isDirectory) return (name, string.Empty);

        var ext = Path.GetExtension(name);
        return ext.Length == 0 || ext.Length == name.Length
            ? (name, string.Empty)
            : (name[..^ext.Length], ext);
    }

    /// <summary>Generates a non-conflicting name by appending (1), (2), ... to the filename.</summary>
    public static string GenerateNewName(string originalName, Func<string, bool> exists, bool isDirectory = false)
    {
        var (stem, ext) = SplitName(originalName, isDirectory);

        for (int i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Same naming scheme, but the occupancy probe may hit the network. File operations must
    /// use this one: the destination can be a remote file system, where a local-disk probe
    /// finds nothing and the "unique" name silently overwrites an existing remote file.
    /// </summary>
    public static async Task<string> GenerateNewNameAsync(
        string originalName, Func<string, Task<bool>> existsAsync, bool isDirectory = false)
    {
        var (stem, ext) = SplitName(originalName, isDirectory);

        for (int i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!await existsAsync(candidate)) return candidate;
        }
    }
}
