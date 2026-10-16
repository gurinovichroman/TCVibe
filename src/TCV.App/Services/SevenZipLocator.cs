using System.IO;

namespace TCV.App.Services;

/// <summary>
/// Finds <c>7z.exe</c>. A portable app has to look next to itself first — dropping 7z.exe (and
/// 7z.dll) beside the executable must be enough — before it trusts whatever is installed on the
/// machine. Pure: every input is a parameter, so the order is testable without touching the
/// registry or the real PATH.
/// </summary>
public static class SevenZipLocator
{
    private const string ExeName = "7z.exe";

    /// <param name="appDirectory">Directory of the application (<c>AppContext.BaseDirectory</c>).</param>
    /// <param name="installedDirectories">Install directories from the registry, in probing order.</param>
    /// <param name="pathVariable">The PATH environment variable, or null.</param>
    /// <param name="fileExists">File-existence check (a seam for tests).</param>
    public static string? Find(
        string appDirectory,
        IEnumerable<string> installedDirectories,
        string? pathVariable,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;

        // 1. Beside the exe, in the folders a user would naturally unpack it to.
        string[] portable =
        [
            appDirectory,
            Path.Combine(appDirectory, "7-Zip"),
            Path.Combine(appDirectory, "7zip"),
            Path.Combine(appDirectory, "tools"),
        ];
        foreach (var directory in portable)
        {
            var candidate = Path.Combine(directory, ExeName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        // 2. An installed copy.
        foreach (var directory in installedDirectories)
        {
            var candidate = Path.Combine(directory, ExeName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        // 3. Anything on PATH.
        foreach (var directory in (pathVariable ?? string.Empty).Split(Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory, ExeName);
                if (fileExists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry: skip it.
            }
        }

        return null;
    }
}
