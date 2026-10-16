using System.IO;

namespace TCV.App.Navigation;

/// <summary>
/// Answers "is this command actually installed?" the way a shell would: an absolute path as given,
/// otherwise every <c>PATH</c> directory combined with every <c>PATHEXT</c> extension. Pure — the
/// directories, extensions and existence check are all injected, so the whole lookup is testable
/// without touching the machine.
/// </summary>
/// <remarks>
/// The extension walk is what makes agent CLIs work at all: `claude`, `codex` and friends install as
/// npm shims (<c>claude.cmd</c>, <c>claude.ps1</c>), never as a bare <c>claude</c>.
/// </remarks>
public static class CommandResolver
{
    /// <summary>Used when the environment reports no <c>PATHEXT</c>.</summary>
    public static readonly string[] DefaultExtensions = [".com", ".exe", ".bat", ".cmd", ".ps1"];

    /// <summary>
    /// Returns the resolved full path, or null when nothing matches. An input that already carries a
    /// directory separator is treated as a path and only checked as-is (plus extensions).
    /// </summary>
    public static string? Resolve(
        string? command,
        IEnumerable<string> pathDirectories,
        IEnumerable<string> extensions,
        Func<string, bool> fileExists)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var trimmed = command.Trim().Trim('"');
        var exts = Normalize(extensions);

        if (trimmed.Contains('\\', StringComparison.Ordinal) ||
            trimmed.Contains('/', StringComparison.Ordinal))
            return ResolveWithExtensions(trimmed, exts, fileExists);

        foreach (var dir in pathDirectories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            var candidate = Combine(dir, trimmed);
            var hit = ResolveWithExtensions(candidate, exts, fileExists);
            if (hit is not null) return hit;
        }

        return null;
    }

    /// <summary>True when the command resolves to something runnable.</summary>
    public static bool Exists(
        string? command,
        IEnumerable<string> pathDirectories,
        IEnumerable<string> extensions,
        Func<string, bool> fileExists) =>
        Resolve(command, pathDirectories, extensions, fileExists) is not null;

    private static string? ResolveWithExtensions(
        string candidate, IReadOnlyList<string> extensions, Func<string, bool> fileExists)
    {
        // A name that already has an extension is looked up as written first.
        var hasExtension = Path.GetExtension(candidate).Length > 0;
        if (hasExtension && fileExists(candidate))
        {
            return candidate;
        }

        // "code" → "code.cmd"; a name that already ends in a known extension is not doubled up.
        // An extensionless name never resolves to itself: npm and VS Code install a POSIX shell script
        // called plain "code"/"claude" next to code.cmd, and ShellExecute on that opens the
        // "how do you want to open this file?" dialog instead of the program.
        foreach (var ext in extensions)
        {
            if (candidate.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) continue;
            if (fileExists(candidate + ext)) return candidate + ext;
        }

        return null;
    }

    private static IReadOnlyList<string> Normalize(IEnumerable<string> extensions)
    {
        var result = extensions
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().StartsWith('.') ? e.Trim() : "." + e.Trim())
            .ToList();
        return result.Count > 0 ? result : DefaultExtensions;
    }

    private static string Combine(string directory, string name)
    {
        var dir = directory.Trim().Trim('"').TrimEnd('\\', '/');
        return dir.Length == 0 ? name : dir + "\\" + name;
    }
}
