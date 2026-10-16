using System.Diagnostics.CodeAnalysis;

namespace TCV.Ai.Mcp;

/// <summary>Why a path was refused, or that it was accepted.</summary>
public enum ScopeVerdict
{
    Allowed,

    /// <summary>Nothing is approved, so nothing is reachable — the server's default state.</summary>
    NoScope,

    /// <summary>The path resolves outside every approved root.</summary>
    OutsideScope,

    /// <summary>A symlink or junction on the path leaves the approved roots.</summary>
    SymlinkEscape,

    /// <summary>Not a usable path at all (empty, malformed, a link cycle).</summary>
    Invalid,
}

/// <summary>Outcome of a scope check: the verdict plus the fully resolved path when allowed.</summary>
public sealed record ScopeResult(ScopeVerdict Verdict, string ResolvedPath, string Detail)
{
    public bool IsAllowed => Verdict == ScopeVerdict.Allowed;

    public static ScopeResult Allow(string resolved) => new(ScopeVerdict.Allowed, resolved, string.Empty);
    public static ScopeResult Deny(ScopeVerdict verdict, string detail) => new(verdict, string.Empty, detail);
}

/// <summary>
/// The security boundary of the MCP server: which real directories an agent may touch.
/// <para>
/// The rule that matters is the one in <see cref="ResolveFinal"/>. A naive scope check compares the
/// requested string against an allowed prefix, which is exactly the mistake behind the EscapeRoute
/// class of vulnerabilities: a symlink inside the approved root points anywhere, the prefix test
/// still passes, and the whole disk is readable. So every existing segment of the path is resolved
/// to its final target <em>before</em> containment is tested, and a link that leads out is refused
/// rather than followed (the EscapeRoute class of bugs).
/// </para>
/// <para>
/// Containment itself compares whole path segments: <c>C:\repo-old</c> is not inside <c>C:\repo</c>,
/// even though one string starts with the other.
/// </para>
/// </summary>
public sealed class PathScope
{
    /// <summary>Bound on link hops, so a symlink cycle is refused instead of hanging the server.</summary>
    private const int MaxLinkHops = 40;

    private readonly List<string> _roots;

    /// <param name="roots">Approved directories. Each is resolved once here, so a root that is
    /// itself a link is compared in its resolved form and cannot be sidestepped.</param>
    public PathScope(IEnumerable<string> roots)
    {
        _roots = new List<string>();
        foreach (var root in roots ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!TryResolveFinal(NormalizeConfiguredRoot(root), out var resolved)) continue;

            // A drive root keeps its separator («C:» alone would read as drive-relative downstream).
            var trimmed = NormalizeConfiguredRoot(resolved);
            if (trimmed.TrimEnd('\\', '/').Length == 0) continue;
            if (!_roots.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                _roots.Add(trimmed);
        }
    }

    /// <summary>The approved roots, resolved — what the "what is exposed?" view shows (§ 3.6.4).</summary>
    public IReadOnlyList<string> Roots => _roots;

    public bool IsEmpty => _roots.Count == 0;

    /// <summary>
    /// Decides whether <paramref name="path"/> may be reached. Non-existent paths are allowed when
    /// their resolved location is inside a root — a write needs to name a file that is not there yet.
    /// </summary>
    public ScopeResult Check(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ScopeResult.Deny(ScopeVerdict.Invalid, "The path is empty.");

        if (_roots.Count == 0)
            return ScopeResult.Deny(
                ScopeVerdict.NoScope,
                "No directory has been approved for agent access yet.");

        if (!TryResolveFinal(path, out var resolved))
            return ScopeResult.Deny(
                ScopeVerdict.Invalid,
                "The path could not be resolved (malformed, or a symlink cycle).");

        // Two different refusals on purpose: "outside" is a user mistake the agent can correct by
        // asking for a path in scope, while an escaping link is a trust failure worth naming.
        if (!_roots.Any(root => IsInside(root, resolved)))
        {
            return WasRedirected(path, resolved)
                ? ScopeResult.Deny(
                    ScopeVerdict.SymlinkEscape,
                    "A symlink or junction on this path leads outside the approved directories.")
                : ScopeResult.Deny(
                    ScopeVerdict.OutsideScope,
                    "The path is outside the approved directories.");
        }

        return ScopeResult.Allow(resolved);
    }

    /// <summary>
    /// The form an approved directory is stored in: trimmed, without a trailing separator — except a
    /// drive root, which keeps it. <c>C:</c> on its own is drive-relative (the current directory on
    /// that drive), so trimming <c>C:\</c> to it approved a different directory than the one picked.
    /// </summary>
    public static string NormalizeConfiguredRoot(string root)
    {
        var trimmed = (root ?? string.Empty).Trim();
        var withoutSeparator = trimmed.TrimEnd('\\', '/');

        if (withoutSeparator.Length == 2 && withoutSeparator[1] == ':')
            return withoutSeparator + '\\';

        return withoutSeparator.Length == 0 ? trimmed : withoutSeparator;
    }

    /// <summary>
    /// True when a stored root and a root as the dashboard shows it (resolved, see <see cref="Roots"/>)
    /// name the same directory. Revoking compares these two forms, and a plain string test never
    /// matched a root that is a junction or a symlink — the list showed the target, the setting held
    /// the link, and «Revoke» silently left the access in place.
    /// </summary>
    public static bool NamesSameRoot(string configured, string shown)
    {
        if (string.IsNullOrWhiteSpace(configured) || string.IsNullOrWhiteSpace(shown)) return false;

        var left = NormalizeConfiguredRoot(configured);
        var right = NormalizeConfiguredRoot(shown);
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;

        return TryResolveFinal(left, out var resolvedLeft)
               && TryResolveFinal(right, out var resolvedRight)
               && string.Equals(
                   resolvedLeft.TrimEnd('\\', '/'), resolvedRight.TrimEnd('\\', '/'),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the resolved path differs from plain lexical normalisation — i.e. a link was
    /// followed. Only used to pick the better refusal message.
    /// </summary>
    private static bool WasRedirected(string requested, string resolved)
    {
        try
        {
            return !string.Equals(
                Path.GetFullPath(requested).TrimEnd('\\', '/'),
                resolved.TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Whole-segment containment. <paramref name="root"/> and <paramref name="path"/> must already be
    /// resolved — this method knowingly does no link resolution, which is why it is private to the
    /// scope check and not a general-purpose helper.
    /// </summary>
    internal static bool IsInside(string root, string path)
    {
        var normalizedRoot = root.Replace('/', '\\').TrimEnd('\\');
        var normalizedPath = path.Replace('/', '\\').TrimEnd('\\');

        if (normalizedRoot.Length == 0 || normalizedPath.Length == 0) return false;
        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)) return true;

        return normalizedPath.Length > normalizedRoot.Length
            && normalizedPath[normalizedRoot.Length] == '\\'
            && normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves a path to its real location, following every symlink and junction on the way.
    /// <para>
    /// Walks segment by segment from the root instead of resolving only the final component, because
    /// the dangerous case is a link in the <em>middle</em>: with <c>C:\ok\link → D:\secrets</c>, the
    /// path <c>C:\ok\link\file.txt</c> has a perfectly ordinary last segment and resolving just that
    /// would leave the escape invisible.
    /// </para>
    /// Segments below the deepest existing directory cannot be links (they do not exist), so they are
    /// appended as-is.
    /// </summary>
    public static bool TryResolveFinal(string path, [NotNullWhen(true)] out string? resolved)
    {
        resolved = null;
        try
        {
            resolved = ResolveFinal(path);
            return resolved is not null;
        }
        catch
        {
            // An unreadable or malformed path is a refusal, not an exception the server has to survive.
            return false;
        }
    }

    private static string? ResolveFinal(string path)
    {
        var hops = 0;
        return ResolveFinal(path, ref hops);
    }

    private static string? ResolveFinal(string path, ref int hops)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root)) return null;

        var remainder = full[root.Length..]
            .Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        var current = root;

        foreach (var segment in remainder)
        {
            current = Path.Combine(current, segment);

            var target = ResolveOne(current, ref hops);
            if (target is null) return null;      // link cycle or hop budget exhausted

            if (!string.Equals(target, current, StringComparison.Ordinal))
            {
                // The target is a path of its own and can run through further links in its parents
                // (C:\ok\a -> C:\ok\b\c where C:\ok\b -> D:\secret). ResolveLinkTarget is documented to
                // follow only the chain at the last segment — whether it also canonicalises the parents
                // is an OS detail this code should not depend on — so the target is walked again from
                // its root; the shared hop budget is what ends a cycle.
                var walked = ResolveFinal(target, ref hops);
                if (walked is null) return null;
                target = walked;
            }

            current = target;
        }

        return current.TrimEnd('\\', '/') is { Length: > 0 } trimmed
            ? (trimmed.EndsWith(':') ? trimmed + '\\' : trimmed)
            : current;
    }

    /// <summary>
    /// Replaces one existing path with its final link target, if it is a link. Returns the path
    /// unchanged when it is not a link or does not exist; null when the hop budget is spent.
    /// </summary>
    private static string? ResolveOne(string current, ref int hops)
    {
        FileSystemInfo info = Directory.Exists(current)
            ? new DirectoryInfo(current)
            : new FileInfo(current);

        if (!info.Exists) return current;

        // returnFinalTarget walks a chain of links inside the OS, so one call is normally enough;
        // the counter is a backstop against a cycle the OS reports as "another link".
        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        if (target is null) return current;

        if (++hops > MaxLinkHops) return null;
        return target.FullName;
    }
}
