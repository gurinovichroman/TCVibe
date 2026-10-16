using System.IO;
namespace TCV.App.Agents;

/// <summary>
/// Keeps a copy of a file before the dashboard's «Revert» throws its changes away. <c>git restore</c>
/// discards uncommitted work for good, and the dashboard's attribution is only ever a guess, so the
/// row being reverted may hold the user's own edits. A copy under the application folder (INV-03)
/// turns an irreversible click into a recoverable one.
/// </summary>
public static class RevertBackup
{
    public const string FolderName = "agent-revert-backups";

    /// <summary>Backup sets kept; the oldest are deleted beyond this.</summary>
    public const int MaxSets = 50;

    /// <summary>
    /// Copies each existing file to <c>&lt;baseDirectory&gt;/agent-revert-backups/&lt;stamp&gt;/&lt;relative path&gt;</c>.
    /// A file that is gone (a deletion being reverted) has nothing to keep and is skipped.
    /// </summary>
    /// <returns>The backup folder, or null when nothing was copied or copying failed.</returns>
    public static string? Save(
        string baseDirectory, IEnumerable<(string RelativePath, string FullPath)> files, DateTime now)
    {
        try
        {
            var root = Path.Combine(baseDirectory, FolderName);
            var set = Path.Combine(root, now.ToString("yyyyMMdd-HHmmss-fff"));
            var copied = 0;

            foreach (var (relative, full) in files)
            {
                if (!File.Exists(full)) continue;

                // The relative path came from git, but it is joined onto a folder here, so a segment
                // that climbs out is refused rather than trusted.
                var target = Path.GetFullPath(Path.Combine(set, relative.Replace('/', '\\')));
                if (!target.StartsWith(Path.GetFullPath(set) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(full, target, overwrite: true);
                copied++;
            }

            if (copied == 0) return null;

            Prune(root);
            return set;
        }
        catch
        {
            // A backup that could not be made must not be silent: the caller sees null and says so.
            return null;
        }
    }

    private static void Prune(string root)
    {
        try
        {
            var old = Directory.GetDirectories(root)
                .OrderByDescending(d => d, StringComparer.Ordinal)
                .Skip(MaxSets);

            foreach (var directory in old) Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Housekeeping only.
        }
    }
}
