using System.Text;

namespace TCV.Git;

/// <summary>
/// Turns a free-text task title ("Fix login redirect (#412)") into names git and Windows both
/// accept: a branch name and the folder its worktree goes in. Pure — no disk, no repository — so
/// the "new worktree for this task" form can show the derived names while the user is still typing,
/// which is what stops the branch from being called <c>new-branch-2</c>.
/// </summary>
public static class GitRefName
{
    /// <summary>Longest slug kept from a title. Branch names have no hard limit, but the worktree
    /// folder derived from one lands inside a path that does, so the cut happens here.</summary>
    private const int MaxSlugLength = 60;

    /// <summary>Characters git refuses outright (git-check-ref-format), plus the path separators
    /// Windows would choke on in the derived folder name.</summary>
    private static readonly char[] Forbidden = { ' ', '~', '^', ':', '?', '*', '[', ']', '\\', '"', '\'', '|', '<', '>' };

    /// <summary>
    /// Derives a branch name from a task title: lower-cased, words joined by dashes, everything git
    /// rejects dropped. <paramref name="prefix"/> is prepended as a namespace (<c>feature</c> →
    /// <c>feature/fix-login-redirect-412</c>) when it is not blank.
    /// </summary>
    public static string FromTitle(string? title, string prefix = "")
    {
        var slug = Slug(title);
        if (slug.Length == 0) return string.Empty;

        var head = Slug(prefix);
        return head.Length == 0 ? slug : $"{head}/{slug}";
    }

    /// <summary>
    /// Whether git would accept <paramref name="name"/> as a branch name. Deliberately stricter than
    /// git in one place — no leading <c>-</c> — because such a name parses as an option in every
    /// command line that later touches it.
    /// </summary>
    public static bool IsValidBranch(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Length != name.Trim().Length) return false;

        if (name.StartsWith('-') || name.StartsWith('/') || name.EndsWith('/')) return false;
        if (name.StartsWith('.') || name.EndsWith('.')) return false;
        if (name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        if (name == "@") return false;

        if (name.Contains("..", StringComparison.Ordinal)) return false;
        if (name.Contains("//", StringComparison.Ordinal)) return false;
        if (name.Contains("@{", StringComparison.Ordinal)) return false;

        foreach (var ch in name)
        {
            if (char.IsControl(ch)) return false;
            if (Array.IndexOf(Forbidden, ch) >= 0) return false;
        }

        // "a/.hidden" and "a/b.lock" are rejected per path component, not just at the ends.
        foreach (var component in name.Split('/'))
        {
            if (component.Length == 0) return false;
            if (component.StartsWith('.')) return false;
            if (component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) return false;
        }

        return true;
    }

    /// <summary>
    /// The existing branch a new <paramref name="branch"/> would collide with, or null. A ref is a
    /// file under <c>refs/heads</c>, so <c>a</c> and <c>a/b</c> cannot both exist — and on Windows
    /// <c>A</c> and <c>a/b</c> cannot either, which is why the comparison ignores case. libgit2
    /// reports the clash as "could not remove directory … parent is not directory" (TRAP-57).
    /// </summary>
    public static string? FindPathConflict(string? branch, IEnumerable<string> existingBranches)
    {
        ArgumentNullException.ThrowIfNull(existingBranches);

        var name = branch?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            return null;
        }

        return existingBranches.FirstOrDefault(existing =>
            existing.Length > 0
            && (name.StartsWith(existing + "/", StringComparison.OrdinalIgnoreCase)
                || existing.StartsWith(name + "/", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// The folder name a worktree for <paramref name="branch"/> should get: the branch with its
    /// namespace flattened (<c>feature/login</c> → <c>feature-login</c>), because a worktree root
    /// holding sibling folders must not sprout a directory tree of its own. Reserved Windows device
    /// names get a suffix — <c>con</c> is not creatable as a folder, and the failure surfaces far
    /// from here.
    /// </summary>
    public static string DirectoryName(string? branch)
    {
        var slug = Slug((branch ?? string.Empty).Replace('/', '-'));
        if (slug.Length == 0) return "worktree";

        return IsReservedDeviceName(slug) ? slug + "-wt" : slug;
    }

    /// <summary>
    /// Lower-cased dash-joined slug: letters and digits survive, every other run collapses to one
    /// dash. ASCII-only output on purpose — a Cyrillic branch name is legal in git but breaks tooling
    /// all the way down the chain (shells, CI, remote hosts) — so Russian and Ukrainian letters are
    /// transliterated and any other non-ASCII letter is dropped.
    /// </summary>
    private static string Slug(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var sb = new StringBuilder(text.Length);
        foreach (var source in text.Trim().ToLowerInvariant())
        {
            // Cyrillic is spelled out rather than dropped: a task titled «Исправить вход» should
            // still produce "ispravit-vkhod", not an empty branch name.
            if (Transliterate(source) is { } spelled)
            {
                sb.Append(spelled);
                if (sb.Length >= MaxSlugLength) break;
                continue;
            }

            var ch = source;
            if (ch is >= 'a' and <= 'z' || ch is >= '0' and <= '9' || ch is '_')
                sb.Append(ch);
            else if (ch == '.')
            {
                // A dot is fine in a branch name, ".." is not — and "release 2..0" is an ordinary
                // way to produce one. Collapse the run instead of emitting an invalid name.
                if (sb.Length > 0 && sb[^1] != '.') sb.Append(ch);
            }
            else if (ch == '-' || sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');

            if (sb.Length >= MaxSlugLength) break;
        }

        // A trailing dot would make the branch invalid and the Windows folder unopenable.
        var slug = sb.ToString().Trim('-', '.', '_');

        // "notes.lock" is a valid title and an invalid ref name (git reserves the suffix for lock files).
        return slug.EndsWith(".lock", StringComparison.Ordinal) ? slug[..^5] + "-lock" : slug;
    }

    /// <summary>Latin spelling of a lower-case Russian or Ukrainian letter (a plain, reversible-enough
    /// scheme — nobody parses a branch name back), or null for any other character.</summary>
    private static string? Transliterate(char ch) => ch switch
    {
        'а' => "a", 'б' => "b", 'в' => "v", 'г' => "g", 'ґ' => "g", 'д' => "d",
        'е' => "e", 'ё' => "yo", 'є' => "ye", 'ж' => "zh", 'з' => "z",
        'и' => "i", 'і' => "i", 'ї' => "yi", 'й' => "y", 'к' => "k", 'л' => "l",
        'м' => "m", 'н' => "n", 'о' => "o", 'п' => "p", 'р' => "r", 'с' => "s",
        'т' => "t", 'у' => "u", 'ф' => "f", 'х' => "kh", 'ц' => "ts", 'ч' => "ch",
        'ш' => "sh", 'щ' => "shch", 'ъ' => string.Empty, 'ы' => "y", 'ь' => string.Empty,
        'э' => "e", 'ю' => "yu", 'я' => "ya",
        _ => null,
    };

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    private static bool IsReservedDeviceName(string name)
    {
        // "con.txt" is reserved too: Windows matches on the stem, extension and all.
        var stem = name.Split('.', 2)[0];
        return ReservedDeviceNames.Contains(stem);
    }
}
