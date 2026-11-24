using TCV.App.FileSystem;
using TCV.App.Services;
using TCV.Archives;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.Operations;

/// <summary>
/// Where a change to an archive ends up. An archive opened from inside another archive or from a server
/// is a temporary copy (<see cref="TemporaryFiles"/>), and nothing writes back to where it came from: an
/// edit would reach only the copy and be lost when the panel leaves it, so such an archive is read-only
/// (TRAP-98, ADR-0040). Writing back into the parent archive or to the server is a function of its own,
/// with its own conflicts, and does not exist.
/// </summary>
public static class ArchiveEditing
{
    /// <summary>What to open the archive at <paramref name="archivePath"/> with in a panel.</summary>
    public static IReadOnlyDictionary<string, string>? OpenOptions(string archivePath, string? password)
    {
        var scratch = TemporaryFiles.Holds(archivePath);
        if (password is null && !scratch)
        {
            return null;
        }

        var options = new Dictionary<string, string>();
        if (password is not null)
        {
            options["password"] = password;
        }

        if (scratch)
        {
            options[ArchiveFileSystem.ScratchCopyOption] = "true";
        }

        return options;
    }

    /// <summary>
    /// Whether a change to the file at <paramref name="archivePath"/> reaches the user, for the paths that
    /// know an archive by its file alone (a cut in the clipboard, a drag). With the file system at hand,
    /// <see cref="ArchiveFileSystem.CanEdit"/> says the same.
    /// </summary>
    public static bool CanEditFile(IArchiveEngine engine, string archivePath) =>
        engine.CanWrite && !TemporaryFiles.Holds(archivePath);

    /// <summary>
    /// The archive file a change through <paramref name="fs"/> rewrites, or null when <paramref name="fs"/>
    /// is no archive: browsed, or under the hits of a search in it (TRAP-28). A log line names it, because
    /// the paths inside mean nothing without it (TRAP-62).
    /// </summary>
    public static string? ArchiveBehind(IVirtualFileSystem fs) => fs switch
    {
        ArchiveFileSystem archive => archive.ArchivePath,
        SearchResultsFileSystem { Inner: ArchiveFileSystem archive } => archive.ArchivePath,
        _ => null,
    };
}
