using System.IO;

namespace TCV.App.Services;

/// <summary>
/// Scratch copies of files that live somewhere the shell cannot reach (inside an archive, on a
/// server): a copy is made so the associated program can open it. Each copy goes in a folder of its
/// own — two files with the same name from different archives no longer overwrite each other, and
/// one that is still open in an editor is never clobbered — and the whole area is swept at startup
/// and cleaned on exit, so decrypted or remote content does not pile up in the temp folder.
/// </summary>
/// <remarks>
/// The copies live in the system temp folder, not beside the exe (INV-03 is about *state*): they are
/// transient, the exe folder may be a read-only stick, and a decrypted archive member has no
/// business on removable media.
/// </remarks>
public static class TemporaryFiles
{
    private static readonly object Gate = new();
    private static readonly List<string> CreatedThisRun = [];

    /// <summary>Where scratch copies are made.</summary>
    public static string Root { get; set; } = Path.Combine(Path.GetTempPath(), "TCV");

    /// <summary>
    /// A fresh path for a scratch copy of <paramref name="fileName"/>: <c>Root\&lt;unique&gt;\name</c>.
    /// The folder is created; the file is not.
    /// </summary>
    public static string PathFor(string fileName)
    {
        var safe = string.Concat(fileName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (string.IsNullOrWhiteSpace(safe))
        {
            safe = "file";
        }

        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(directory);

        lock (Gate)
        {
            CreatedThisRun.Add(directory);
        }

        return Path.Combine(directory, safe);
    }

    /// <summary>
    /// True for a path inside <see cref="Root"/>: a scratch copy made here, not a file the user keeps
    /// anywhere. An archive opened from inside another archive or from a server is such a copy.
    /// </summary>
    public static bool Holds(string path)
    {
        try
        {
            var root = Path.GetFullPath(Root).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// Removes scratch folders older than <paramref name="age"/> — leftovers of an earlier run that
    /// crashed or was killed. Younger folders are kept: another window (a separate process) may be
    /// showing them. Files still open somewhere are skipped.
    /// </summary>
    public static int SweepOlderThan(TimeSpan age)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(Root))
            {
                return 0;
            }

            var cutoff = DateTime.UtcNow - age;
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                if (Directory.GetLastWriteTimeUtc(directory) <= cutoff && TryDelete(directory))
                {
                    removed++;
                }
            }

            // Loose files from versions that wrote straight into the root.
            foreach (var file in Directory.EnumerateFiles(Root))
            {
                if (File.GetLastWriteTimeUtc(file) <= cutoff)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch (IOException)
                    {
                        // In use.
                    }
                }
            }
        }
        catch (IOException)
        {
            // The temp folder is best effort by nature.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }

        return removed;
    }

    /// <summary>Deletes what this run created. A copy an editor still has open stays (and is swept next time).</summary>
    public static void DeleteCreatedThisRun()
    {
        string[] mine;
        lock (Gate)
        {
            mine = [.. CreatedThisRun];
            CreatedThisRun.Clear();
        }

        foreach (var directory in mine)
        {
            TryDelete(directory);
        }
    }

    private static bool TryDelete(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
