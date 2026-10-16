using System.Text;
using System.Text.RegularExpressions;

namespace TCV.Core.Domain;

/// <summary>
/// One parsed <c>.gitignore</c> file: an ordered list of patterns compiled to regexes. Pure logic
/// (no file system, no WPF), so the whole matcher is unit-testable.
/// </summary>
/// <remarks>
/// Lives in the core rather than in the app because two independent walkers need it: the project
/// file index behind Ctrl+P (<c>TCV.App.Navigation.FileIndexWalker</c>) and the content search
/// engine (<c>TCV.Search.SearchEngine</c>), which is a plugin and cannot see the app assembly.
/// <para>
/// Implements the subset of gitignore syntax that matters for indexing a working copy: comments,
/// negation (<c>!</c>), directory-only patterns (trailing <c>/</c>), anchoring (a slash at the start
/// or in the middle anchors to the directory holding the file), <c>*</c>, <c>?</c>, <c>**</c> and
/// character classes. Within one file the <b>last</b> matching pattern wins, as git specifies.
/// Matching is case-insensitive: git on Windows defaults to <c>core.ignorecase=true</c>.
/// </para>
/// <para>Not implemented (deliberately): <c>core.excludesFile</c>, <c>.git/info/exclude</c> and
/// tracked-file overrides — an index of the working copy does not need them.</para>
/// </remarks>
public sealed class GitignoreRules
{
    /// <summary>A rule set that never matches — used for directories without a <c>.gitignore</c>.</summary>
    public static GitignoreRules Empty { get; } = new(Array.Empty<Pattern>());

    private readonly IReadOnlyList<Pattern> _patterns;

    private GitignoreRules(IReadOnlyList<Pattern> patterns) => _patterns = patterns;

    public bool IsEmpty => _patterns.Count == 0;

    /// <summary>Parses the text of a single <c>.gitignore</c> file.</summary>
    public static GitignoreRules Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Empty;

        var patterns = new List<Pattern>();
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');

            // Trailing spaces are ignored unless escaped; leading ones are significant.
            line = TrimUnescapedTrailingSpaces(line);
            if (line.Length == 0) continue;
            if (line[0] == '#') continue;                       // comment ("\#" is a literal '#')

            var negated = line[0] == '!';
            if (negated) line = line[1..];
            else if (line.StartsWith("\\!", StringComparison.Ordinal) ||
                     line.StartsWith("\\#", StringComparison.Ordinal)) line = line[1..];
            if (line.Length == 0) continue;

            var directoryOnly = line[^1] == '/';
            if (directoryOnly) line = line[..^1];
            if (line.Length == 0) continue;

            // A slash at the start or in the middle anchors the pattern to this directory;
            // the trailing one stripped above does not count.
            var anchored = line.Contains('/', StringComparison.Ordinal);
            if (line[0] == '/') line = line[1..];
            if (line.Length == 0) continue;

            patterns.Add(new Pattern(Compile(line, anchored), negated, directoryOnly));
        }

        return patterns.Count == 0 ? Empty : new GitignoreRules(patterns);
    }

    /// <summary>
    /// Decides the fate of <paramref name="relativePath"/> (slash-separated, relative to the
    /// directory holding this <c>.gitignore</c>): <c>true</c> ignored, <c>false</c> explicitly
    /// re-included by a <c>!</c> rule, <c>null</c> when no pattern applies — which lets a caller
    /// fall back to the rules of a parent directory.
    /// </summary>
    /// <remarks>
    /// Everything under an ignored directory is ignored, and nothing can re-include it, as git
    /// specifies. A re-included directory does <i>not</i> re-include its contents: with <c>*</c> and
    /// <c>!keep/</c>, <c>keep/a.txt</c> is still ignored, because <c>*</c> matches <c>a.txt</c>.
    /// </remarks>
    public bool? Match(string relativePath, bool isDirectory)
    {
        var path = Normalize(relativePath);
        if (path.Length == 0) return null;

        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (MatchEntry(path[..slash], isDirectory: true) == true) return true;
        }

        return MatchEntry(path, isDirectory);
    }

    /// <summary>
    /// As <see cref="Match"/>, for the path itself only: the verdict of the patterns that match this
    /// very entry, without looking at its parent directories. What a walk that never enters an
    /// ignored directory needs — it has already cleared every ancestor on the way down.
    /// </summary>
    public bool? MatchEntry(string relativePath, bool isDirectory)
    {
        var path = Normalize(relativePath);
        if (path.Length == 0) return null;

        // Last match wins, so walk backwards and stop at the first hit.
        for (var i = _patterns.Count - 1; i >= 0; i--)
        {
            var p = _patterns[i];

            // "build/" must not swallow a *file* called "build".
            if (p.DirectoryOnly && !isDirectory) continue;
            if (!p.Regex.IsMatch(path)) continue;

            return !p.Negated;
        }

        return null;
    }

    private static string Normalize(string? relativePath) =>
        string.IsNullOrEmpty(relativePath) ? string.Empty : relativePath.Replace('\\', '/').Trim('/');

    // ── Compilation ───────────────────────────────────────────────────────────

    private sealed record Pattern(Regex Regex, bool Negated, bool DirectoryOnly);

    private static Regex Compile(string glob, bool anchored)
    {
        var prefix = anchored ? "^" : "^(?:.*/)?";
        var pattern = prefix + GlobToRegex(glob) + "$";
        return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string GlobToRegex(string glob)
    {
        var sb = new StringBuilder();
        var segments = glob.Split('/');

        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;

            if (segments[i] == "**")
            {
                // "a/**" → everything below a; "a/**/b" → b at any depth below a (including none).
                sb.Append(last ? ".*" : "(?:[^/]+/)*");
                continue;
            }

            sb.Append(SegmentToRegex(segments[i]));
            if (!last) sb.Append('/');
        }

        return sb.ToString();
    }

    private static string SegmentToRegex(string segment)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            switch (c)
            {
                case '*':
                    sb.Append("[^/]*");
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                case '[':
                    var close = segment.IndexOf(']', i + 1);
                    if (close < 0)
                    {
                        sb.Append("\\[");           // unterminated class — treat as a literal
                        break;
                    }
                    var cls = segment[(i + 1)..close];
                    if (cls.StartsWith('!')) cls = '^' + cls[1..];
                    sb.Append('[').Append(cls).Append(']');
                    i = close;
                    break;
                case '\\':
                    if (i + 1 < segment.Length)
                    {
                        sb.Append(Regex.Escape(segment[i + 1].ToString()));
                        i++;
                    }
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string TrimUnescapedTrailingSpaces(string line)
    {
        var end = line.Length;
        while (end > 0 && line[end - 1] == ' ')
        {
            // "foo\ " keeps its space: count the backslashes guarding it.
            var backslashes = 0;
            var j = end - 2;
            while (j >= 0 && line[j] == '\\') { backslashes++; j--; }
            if (backslashes % 2 == 1) break;
            end--;
        }
        return line[..end];
    }
}

/// <summary>
/// The stack of <c>.gitignore</c> rule sets in force at some point of a directory walk. Immutable:
/// descending into a directory produces a new scope, so a breadth-first walk can keep one scope per
/// queued directory. Rules from a deeper directory override those above it, per gitignore semantics.
/// </summary>
public sealed class GitignoreScope
{
    /// <summary>A scope with no rules at all — nothing is ignored.</summary>
    public static GitignoreScope Empty { get; } = new(null, GitignoreRules.Empty, string.Empty);

    private readonly GitignoreScope? _parent;
    private readonly GitignoreRules _rules;
    private readonly string _basePath;   // slash-separated, relative to the index root ("" = root)

    private GitignoreScope(GitignoreScope? parent, GitignoreRules rules, string basePath)
    {
        _parent = parent;
        _rules = rules;
        _basePath = basePath;
    }

    /// <summary>
    /// Adds the rules of the <c>.gitignore</c> found in <paramref name="basePath"/> (relative to the
    /// index root). An empty rule set changes nothing, so the scope is returned unchanged.
    /// </summary>
    public GitignoreScope Push(string basePath, GitignoreRules rules) =>
        rules.IsEmpty ? this : new GitignoreScope(this, rules, basePath.Replace('\\', '/').Trim('/'));

    /// <summary>
    /// True when <paramref name="relativePath"/> (relative to the index root) is ignored: it lies in an
    /// ignored directory, or its own verdict (<see cref="IsIgnoredEntry"/>) says so. Parent
    /// directories are judged by the whole stack, so a nested <c>.gitignore</c> that re-includes
    /// <c>bin/</c> keeps its contents visible despite a top-level <c>bin/</c>.
    /// </summary>
    public bool IsIgnored(string relativePath, bool isDirectory)
    {
        var path = relativePath.Replace('\\', '/').Trim('/');
        for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
        {
            if (IsIgnoredEntry(path[..slash], isDirectory: true)) return true;
        }

        return IsIgnoredEntry(path, isDirectory);
    }

    /// <summary>
    /// True when the patterns that match <paramref name="relativePath"/> itself ignore it; parent
    /// directories are not examined. The deepest rule set that has an opinion decides; without any
    /// opinion the path is kept. For walks that never enter an ignored directory — every ancestor has
    /// already been cleared, and re-testing them for each entry would cost a regex pass per level.
    /// </summary>
    public bool IsIgnoredEntry(string relativePath, bool isDirectory)
    {
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            var local = scope.ToLocalPath(relativePath);
            if (local is null) continue;

            var decision = scope._rules.MatchEntry(local, isDirectory);
            if (decision.HasValue) return decision.Value;
        }

        return false;
    }

    private string? ToLocalPath(string relativePath)
    {
        if (_basePath.Length == 0) return relativePath;
        if (relativePath.Length <= _basePath.Length ||
            !relativePath.StartsWith(_basePath, StringComparison.OrdinalIgnoreCase) ||
            relativePath[_basePath.Length] != '/')
            return null;
        return relativePath[(_basePath.Length + 1)..];
    }
}
