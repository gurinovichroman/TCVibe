using System.IO;

namespace TCV.Git;

/// <summary>Pure path and naming rules the worktree flow needs. No disk access, so they are testable.</summary>
public static class GitWorktreePaths
{
    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or lies below it. Case-insensitive
    /// and separator-agnostic, as Windows paths are; a sibling that merely shares a prefix
    /// (<c>repo-2</c> next to <c>repo</c>) is not inside.
    /// </summary>
    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root)) return false;

        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var top = Path.GetFullPath(root).TrimEnd('\\', '/');
            if (full.Length < top.Length) return false;
            if (!full.StartsWith(top, StringComparison.OrdinalIgnoreCase)) return false;

            return full.Length == top.Length || full[top.Length] is '\\' or '/';
        }
        catch (ArgumentException)
        {
            return false;   // an unparseable path is not inside anything
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// <paramref name="preferred"/> when free, otherwise <c>name (2)</c>, <c>name (3)</c>… Compared
    /// case-insensitively, the way workspace names are looked up.
    /// </summary>
    public static string UniqueName(string preferred, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(preferred)) return preferred;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{preferred} ({suffix})";
            if (!used.Contains(candidate)) return candidate;
        }
    }
}
