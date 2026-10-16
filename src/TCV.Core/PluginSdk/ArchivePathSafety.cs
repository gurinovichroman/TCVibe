namespace TCV.Core.PluginSdk;

/// <summary>Maps archive entry names onto the file system without letting them escape the destination.</summary>
public static class ArchivePathSafety
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Resolves an archive entry name to an absolute path under <paramref name="destDir"/>.
    /// Rejects rooted names and "../" segments that would write outside the destination (Zip Slip).
    /// Characters Windows cannot hold in a name (<c>12:00.log</c> from a Linux tar, a would-be
    /// alternate data stream) become <c>_</c> instead of failing the whole archive.
    /// </summary>
    /// <exception cref="InvalidDataException">The entry name is unusable or escapes the destination.</exception>
    public static string ResolveDestination(string destDir, string entryName)
    {
        var relative = (entryName ?? string.Empty).Replace('\\', '/').Trim('/');
        if (relative.Length == 0)
            throw new InvalidDataException("Archive contains an entry with an empty name.");

        // "C:/x" is an absolute name, not a folder called "C:" — still refused.
        if (Path.IsPathRooted(relative) || (relative.Length >= 2 && relative[1] == ':' && char.IsAsciiLetter(relative[0])))
            throw new InvalidDataException($"Archive entry '{entryName}' is not a relative path.");

        var segments = relative.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(s => s != ".")
            .Select(SanitizeSegment)
            .ToArray();
        if (segments.Length == 0)
            throw new InvalidDataException("Archive contains an entry with an empty name.");

        var root = Path.GetFullPath(destDir);
        var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var resolved = Path.GetFullPath(Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));

        if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Archive entry '{entryName}' points outside the destination directory.");

        return resolved;
    }

    /// <summary>
    /// One path segment made writable on Windows. ".." is left alone for the escape check to judge;
    /// invalid characters, a trailing dot or space (Windows would drop it) and device names are replaced.
    /// </summary>
    public static string SanitizeSegment(string segment)
    {
        if (segment == "..")
        {
            return segment;
        }

        var chars = segment.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidNameChars, chars[i]) >= 0)
            {
                chars[i] = '_';
            }
        }

        for (var i = chars.Length - 1; i >= 0 && chars[i] is '.' or ' '; i--)
        {
            chars[i] = '_';
        }

        var name = new string(chars);
        var stem = name.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(stem) ? "_" + name : name;
    }
}
