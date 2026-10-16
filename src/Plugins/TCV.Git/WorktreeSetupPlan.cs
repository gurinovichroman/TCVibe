namespace TCV.Git;

/// <summary>
/// Decides which of the source checkout's untracked files a fresh worktree needs to be usable.
/// This is the first of the two things that make worktrees annoying in practice: a new worktree is
/// a clean tree, so <c>.env</c>, local settings and credentials — everything deliberately gitignored
/// — are simply absent, and the project does not run until they are copied by hand.
/// Pure: it matches names, the caller does the I/O.
/// </summary>
public static class WorktreeSetupPlan
{
    /// <summary>
    /// What a new worktree usually needs. Kept small and boring on purpose: the point is to carry
    /// configuration, not to clone <c>node_modules</c> — that is what the setup command is for.
    /// </summary>
    public static IReadOnlyList<string> DefaultCarryPatterns { get; } = new[]
    {
        ".env",
        ".env.*",
        "*.local.json",
        "appsettings.Development.json",
        "*.user",
    };

    /// <summary>
    /// Picks the paths worth copying. Each pattern is matched against both the full relative path
    /// and the bare file name, so <c>.env</c> catches <c>src/api/.env</c> while <c>config/*.json</c>
    /// stays anchored where it was written. Results keep the input order and are de-duplicated —
    /// two patterns matching one file must not copy it twice.
    /// </summary>
    /// <param name="relativePaths">Untracked/ignored paths, relative to the source checkout.</param>
    /// <param name="patterns">Globs (<c>*</c> and <c>?</c>); null or empty selects nothing.</param>
    public static IReadOnlyList<string> SelectCarryFiles(
        IEnumerable<string>? relativePaths, IReadOnlyList<string>? patterns)
    {
        var result = new List<string>();
        if (relativePaths is null || patterns is null || patterns.Count == 0) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var path = Normalize(raw);
            if (path.Length == 0) continue;

            var name = path[(path.LastIndexOf('/') + 1)..];

            foreach (var pattern in patterns)
            {
                var glob = Normalize(pattern);
                if (glob.Length == 0) continue;

                // A pattern with a separator is a path pattern; a bare one names a file anywhere.
                var matches = glob.Contains('/')
                    ? Matches(path, glob)
                    : Matches(name, glob);

                if (!matches) continue;
                if (seen.Add(path)) result.Add(path);
                break;
            }
        }

        return result;
    }

    private static string Normalize(string value) =>
        value.Trim().Replace('\\', '/').TrimStart('/');

    /// <summary>
    /// Glob match for <c>*</c> (any run, separators included) and <c>?</c> (one character), case
    /// insensitive. Iterative with backtracking rather than a regex: these patterns run over every
    /// untracked path in the checkout, and building a regex per pattern per call is the kind of cost
    /// that only shows up on a big repository.
    /// </summary>
    private static bool Matches(string text, string pattern)
    {
        int t = 0, p = 0, starP = -1, starT = 0;

        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || EqualsIgnoreCase(pattern[p], text[t])))
            {
                t++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starT = t;
            }
            else if (starP >= 0)
            {
                // Give the last '*' one more character and retry from there.
                p = starP + 1;
                t = ++starT;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    private static bool EqualsIgnoreCase(char a, char b) =>
        a == b || char.ToLowerInvariant(a) == char.ToLowerInvariant(b);
}
