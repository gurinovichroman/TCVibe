using System.IO;

namespace TCV.App.FileSystem;

/// <summary>
/// What the drive selector shows for a panel path: the drive to select, or — when the path is on
/// no listed drive (a UNC share, a drive that is not ready) — no selection and the path's root as
/// text. Keeping the old drive selected there said the panel was somewhere it was not.
/// </summary>
public static class DriveSelection
{
    /// <param name="path">The panel's disk path (for a mounted panel, the disk folder underneath).</param>
    /// <param name="driveNames">Roots of the listed drives, <c>"C:\"</c> style.</param>
    /// <returns>The index of the drive to select (or -1) and the text to show when none is selected.</returns>
    public static (int DriveIndex, string Location) Resolve(string? path, IReadOnlyList<string> driveNames)
    {
        if (string.IsNullOrEmpty(path))
        {
            return (-1, string.Empty);
        }

        if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
        {
            var letter = path[..2];
            for (var i = 0; i < driveNames.Count; i++)
            {
                if (driveNames[i].StartsWith(letter, StringComparison.OrdinalIgnoreCase))
                {
                    return (i, string.Empty);
                }
            }

            return (-1, letter.ToUpperInvariant() + "\\");
        }

        return (-1, UncRoot(path));
    }

    /// <summary><c>\\server\share</c> of a UNC path, or empty for anything else.</summary>
    private static string UncRoot(string path)
    {
        if (!path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        string? root;
        try
        {
            root = Path.GetPathRoot(path);
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }

        return root?.TrimEnd('\\', '/') ?? string.Empty;
    }
}
