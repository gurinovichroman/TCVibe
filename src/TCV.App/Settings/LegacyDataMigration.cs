using System.IO;

namespace TCV.App.Settings;

/// <summary>
/// Brings the state of a pre-Velopack copy into <c>data\</c> (ADR-0031). Those builds kept it next to
/// <c>TCV.App.exe</c>; a portable package unpacked over such a folder finds it in its root. Copied,
/// never moved and never overwritten: the old files stay where they were until the user deletes them.
/// </summary>
public static class LegacyDataMigration
{
    /// <summary>
    /// What the builds before the portable layout wrote beside the exe. Frozen on purpose: a store added later writes into
    /// <c>data\</c> from its first day, so it never needs carrying over. Each file's <c>.bak</c> comes too.
    /// </summary>
    public static IReadOnlyList<string> Files { get; } =
    [
        "settings.json", "workspaces.json", "connections.json", "known-hosts.json",
        "search-presets.json", "saved-operations.json", "multirename-profiles.json",
        "operation-log.json", "agent-sessions.json", "file-usage.json", "name-index.json",
        "semantic-index.db",
    ];

    /// <summary>Folders of the same builds: workspace notes, plugin settings, external plugins, revert backups.</summary>
    public static IReadOnlyList<string> Folders { get; } =
        ["workspaces", "plugin-settings", "plugins", "agent-revert-backups"];

    /// <summary>
    /// Copies the old state from <paramref name="legacyDirectory"/> into <paramref name="dataDirectory"/>
    /// when the latter has neither settings nor workspaces yet — the first start in the new layout.
    /// Returns how many files and folders were copied. Best effort: an entry that cannot be read is
    /// skipped, the start goes on.
    /// </summary>
    public static int Run(string legacyDirectory, string dataDirectory)
    {
        if (File.Exists(Path.Combine(dataDirectory, "settings.json"))
            || File.Exists(Path.Combine(dataDirectory, "workspaces.json")))
        {
            return 0;
        }

        var copied = 0;
        foreach (var name in Files.SelectMany(f => new[] { f, f + ".bak" }))
        {
            if (TryCopyFile(Path.Combine(legacyDirectory, name), Path.Combine(dataDirectory, name)))
            {
                copied++;
            }
        }

        foreach (var name in Folders)
        {
            var source = Path.Combine(legacyDirectory, name);
            if (Directory.Exists(source) && CopyTree(source, Path.Combine(dataDirectory, name)) > 0)
            {
                copied++;
            }
        }

        return copied;
    }

    private static bool TryCopyFile(string source, string target)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(target))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int CopyTree(string source, string target)
    {
        var copied = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (TryCopyFile(file, Path.Combine(target, Path.GetRelativePath(source, file))))
                {
                    copied++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An unreadable subfolder ends the walk; what was copied stays.
        }

        return copied;
    }
}
