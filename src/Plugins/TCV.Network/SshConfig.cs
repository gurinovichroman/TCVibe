using System.Text;

namespace TCV.Network;

/// <summary>
/// One concrete host of an OpenSSH client configuration (<c>~/.ssh/config</c>) with what applies to
/// it, every <c>Host</c> block that matches its alias taken in file order, first value wins — as
/// <c>ssh alias</c> would see it. The values are raw: tokens (<c>%h</c>, <c>~</c>) in
/// <see cref="IdentityFiles"/> are expanded by <see cref="SshConfigImport"/>.
/// </summary>
/// <param name="Alias">The name after <c>Host</c>: what the user types after <c>ssh</c>.</param>
/// <param name="HostName">The real host (<c>%h</c> already replaced by the alias), or null when the alias is the host.</param>
/// <param name="User">The login, or null when ssh would use the local user name.</param>
/// <param name="Port">The port, or null for 22.</param>
/// <param name="IdentityFiles">Every <c>IdentityFile</c> that applies, in order.</param>
/// <param name="ProxyJump">A jump host, which SSH.NET cannot go through; null when there is none.</param>
/// <param name="HasProxyCommand">A <c>ProxyCommand</c> applies (also not supported).</param>
public sealed record SshConfigHost(
    string Alias,
    string? HostName,
    string? User,
    int? Port,
    IReadOnlyList<string> IdentityFiles,
    string? ProxyJump,
    bool HasProxyCommand);

/// <summary>A file named by an <c>Include</c>: where it is and what it says.</summary>
public sealed record SshConfigInclude(string Path, string Text);

/// <summary>What part of a configuration could not be taken into account.</summary>
public enum SshConfigWarningKind
{
    /// <summary>A <c>Match</c> other than <c>Match all</c>: its conditions cannot be evaluated, its settings were skipped.</summary>
    MatchSkipped,

    /// <summary>An <c>Include</c> names a file that is not there.</summary>
    IncludeMissing,

    /// <summary>An <c>Include</c> could not be read (wildcards in a folder name, no access…).</summary>
    IncludeFailed,

    /// <summary>Includes nested deeper than OpenSSH allows.</summary>
    IncludeTooDeep,
}

/// <summary>Something the import could not take into account, and what it was about (the include, the error).</summary>
public sealed record SshConfigWarning(SshConfigWarningKind Kind, string Detail);

/// <summary>What a configuration yielded: the hosts, and what could not be taken into account.</summary>
public sealed record SshConfigParseResult(IReadOnlyList<SshConfigHost> Hosts, IReadOnlyList<SshConfigWarning> Warnings);

/// <summary>
/// Reads the OpenSSH client configuration format (ssh_config(5)) far enough to import hosts (6.82):
/// <c>Host</c> blocks with negated and wildcard patterns, the global section before the first
/// <c>Host</c>, <c>Include</c> (through a resolver, so this stays pure), <c>Match all</c>, quoted
/// arguments, <c>keyword=value</c> and trailing comments. Wildcard-only hosts (<c>*</c>,
/// <c>*.example.com</c>) are not hosts to connect to, but their settings apply to the concrete ones.
/// Any other <c>Match</c> cannot be evaluated here; its settings are skipped and a warning says so.
/// </summary>
public static class SshConfigParser
{
    /// <summary>OpenSSH's own limit for nested <c>Include</c>.</summary>
    public const int MaxIncludeDepth = 16;

    /// <param name="text">The configuration.</param>
    /// <param name="resolveInclude">
    /// The files one argument of <c>Include</c> names (a path or a pattern, as written), in order;
    /// empty when there are none. Null: <c>Include</c> lines are skipped with a warning.
    /// </param>
    public static SshConfigParseResult Parse(string text, Func<string, IReadOnlyList<SshConfigInclude>>? resolveInclude = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var state = new ParseState(resolveInclude);
        var global = new Section(["*"], Active: true);
        state.Read(text, global, depth: 0);

        var hosts = state.Aliases
            .Select(alias => Resolve(alias, state.Settings))
            .ToList();
        return new SshConfigParseResult(hosts, state.Warnings);
    }

    /// <summary>
    /// The arguments of one line, ssh-style: separated by white space, double quotes keep spaces,
    /// an unquoted <c>#</c> starts a comment. The keyword may be followed by <c>=</c> instead of a space.
    /// </summary>
    public static (string Keyword, IReadOnlyList<string> Arguments)? SplitLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return null;
        }

        var end = 0;
        while (end < trimmed.Length && !char.IsWhiteSpace(trimmed[end]) && trimmed[end] != '=')
        {
            end++;
        }

        var keyword = trimmed[..end];
        var rest = trimmed[end..].TrimStart();
        if (rest.StartsWith('='))
        {
            rest = rest[1..].TrimStart();
        }

        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        foreach (var c in rest)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else if (!inQuotes && c == '#' && !hasToken)
            {
                break;   // a trailing comment
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            args.Add(current.ToString());
        }

        return (keyword, args);
    }

    /// <summary>True when <paramref name="host"/> matches a <c>Host</c> line: some pattern matches and no negated one does.</summary>
    public static bool MatchesPatterns(IReadOnlyList<string> patterns, string host)
    {
        var matched = false;
        foreach (var pattern in patterns)
        {
            if (pattern.StartsWith('!'))
            {
                if (GlobMatch(pattern[1..], host))
                {
                    return false;
                }
            }
            else if (GlobMatch(pattern, host))
            {
                matched = true;
            }
        }

        return matched;
    }

    /// <summary><c>*</c> is any run of characters, <c>?</c> one character; case does not matter (ssh lowercases the host).</summary>
    public static bool GlobMatch(string pattern, string text)
    {
        int p = 0, t = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
            {
                p++;
                t++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = t;
            }
            else if (star >= 0)
            {
                p = star + 1;
                t = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }

        return p == pattern.Length;
    }

    /// <summary>A concrete name: no wildcard, not negated — something <c>ssh name</c> connects to.</summary>
    private static bool IsConcrete(string pattern) =>
        pattern.Length > 0 && !pattern.StartsWith('!') && pattern.IndexOfAny(['*', '?']) < 0;

    private static SshConfigHost Resolve(string alias, IReadOnlyList<Setting> settings)
    {
        var first = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var identities = new List<string>();
        foreach (var setting in settings)
        {
            if (!setting.Section.Active || !MatchesPatterns(setting.Section.Patterns, alias))
            {
                continue;
            }

            if (setting.Keyword.Equals("IdentityFile", StringComparison.OrdinalIgnoreCase))
            {
                // Unlike the rest, every IdentityFile that applies is tried, in order.
                if (setting.Arguments.Count > 0 && !identities.Contains(setting.Arguments[0]))
                {
                    identities.Add(setting.Arguments[0]);
                }

                continue;
            }

            first.TryAdd(setting.Keyword, setting.Arguments);
        }

        string? Value(string keyword) =>
            first.TryGetValue(keyword, out var args) && args.Count > 0 ? args[0] : null;

        var hostName = Value("HostName") is { } named ? ExpandHostToken(named, alias) : null;
        var port = int.TryParse(Value("Port"), out var p) && p is > 0 and <= 65535 ? p : (int?)null;
        var proxyJump = Value("ProxyJump");
        var proxyCommand = Value("ProxyCommand");
        return new SshConfigHost(
            alias,
            hostName,
            Value("User"),
            port,
            identities,
            string.Equals(proxyJump, "none", StringComparison.OrdinalIgnoreCase) ? null : proxyJump,
            proxyCommand is not null && !proxyCommand.Equals("none", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary><c>%h</c> in <c>HostName</c> is the alias (<c>HostName %h.example.com</c>), <c>%%</c> a percent sign.</summary>
    private static string ExpandHostToken(string value, string alias) =>
        value.Replace("%%", "\0", StringComparison.Ordinal)
            .Replace("%h", alias, StringComparison.Ordinal)
            .Replace('\0', '%');

    private sealed record Section(IReadOnlyList<string> Patterns, bool Active);

    private sealed record Setting(Section Section, string Keyword, IReadOnlyList<string> Arguments);

    private sealed class ParseState(Func<string, IReadOnlyList<SshConfigInclude>>? resolveInclude)
    {
        private readonly HashSet<string> _aliasSet = new(StringComparer.OrdinalIgnoreCase);
        private bool _matchWarned;

        public List<string> Aliases { get; } = [];
        public List<Setting> Settings { get; } = [];
        public List<SshConfigWarning> Warnings { get; } = [];

        /// <summary>
        /// Reads one file. An included file starts in the block that included it — its lines before
        /// its own first <c>Host</c> are conditional on that block — and the including file goes on in
        /// its block afterwards, whatever the included one switched to.
        /// </summary>
        public void Read(string text, Section start, int depth)
        {
            var section = start;
            foreach (var raw in text.Split('\n'))
            {
                if (SplitLine(raw.TrimEnd('\r')) is not { } line)
                {
                    continue;
                }

                var (keyword, args) = line;
                if (keyword.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    section = new Section(args, Active: true);
                    foreach (var pattern in args.Where(IsConcrete))
                    {
                        if (_aliasSet.Add(pattern))
                        {
                            Aliases.Add(pattern);
                        }
                    }
                }
                else if (keyword.Equals("Match", StringComparison.OrdinalIgnoreCase))
                {
                    var all = args.Count == 1 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
                    section = new Section(["*"], Active: all);
                    if (!all && !_matchWarned)
                    {
                        _matchWarned = true;
                        Warnings.Add(new SshConfigWarning(SshConfigWarningKind.MatchSkipped, string.Join(' ', args)));
                    }
                }
                else if (keyword.Equals("Include", StringComparison.OrdinalIgnoreCase))
                {
                    Include(args, section, depth);
                }
                else
                {
                    Settings.Add(new Setting(section, keyword, args));
                }
            }
        }

        private void Include(IReadOnlyList<string> args, Section section, int depth)
        {
            if (resolveInclude is null)
            {
                Warnings.Add(new SshConfigWarning(SshConfigWarningKind.IncludeFailed, string.Join(' ', args)));
                return;
            }

            if (depth + 1 > MaxIncludeDepth)
            {
                Warnings.Add(new SshConfigWarning(SshConfigWarningKind.IncludeTooDeep, string.Join(' ', args)));
                return;
            }

            foreach (var arg in args)
            {
                IReadOnlyList<SshConfigInclude> files;
                try
                {
                    files = resolveInclude(arg);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    Warnings.Add(new SshConfigWarning(SshConfigWarningKind.IncludeFailed, $"{arg}: {ex.Message}"));
                    continue;
                }

                if (files.Count == 0 && arg.IndexOfAny(['*', '?']) < 0)
                {
                    Warnings.Add(new SshConfigWarning(SshConfigWarningKind.IncludeMissing, arg));
                }

                foreach (var file in files)
                {
                    Read(file.Text, section, depth + 1);
                }
            }
        }
    }
}

/// <summary>
/// Reads <c>~/.ssh/config</c> and the files it includes from disk. Only reads: nothing under
/// <c>~/.ssh</c> is ever written.
/// </summary>
public static class SshConfigFile
{
    /// <summary>Where OpenSSH (and Windows' own OpenSSH client) keeps the user's configuration.</summary>
    public static string DefaultPath(string homeDirectory) => Path.Combine(homeDirectory, ".ssh", "config");

    /// <summary>The parsed configuration at <paramref name="path"/>, or null when there is no such file.</summary>
    public static SshConfigParseResult? Read(string path, string homeDirectory)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var sshDirectory = Path.Combine(homeDirectory, ".ssh");
        return SshConfigParser.Parse(File.ReadAllText(path), arg => ResolveInclude(arg, homeDirectory, sshDirectory));
    }

    /// <summary>
    /// The files one <c>Include</c> argument names: <c>~</c> is the home folder, a relative path is
    /// under <c>~/.ssh</c>, and wildcards in the file name are expanded in name order. Wildcards in
    /// folder names are not supported.
    /// </summary>
    public static IReadOnlyList<SshConfigInclude> ResolveInclude(string argument, string homeDirectory, string sshDirectory)
    {
        var path = argument.Replace('/', Path.DirectorySeparatorChar);
        if (path.StartsWith('~'))
        {
            path = homeDirectory + path[1..];
        }
        else if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(sshDirectory, path);
        }

        var directory = Path.GetDirectoryName(path);
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(name))
        {
            return [];
        }

        if (directory.IndexOfAny(['*', '?']) >= 0)
        {
            throw new NotSupportedException("wildcards in folder names are not supported");
        }

        if (name.IndexOfAny(['*', '?']) < 0)
        {
            return File.Exists(path) ? [new SshConfigInclude(path, File.ReadAllText(path))] : [];
        }

        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory, name)
            .Where(file => SshConfigParser.GlobMatch(name, Path.GetFileName(file)))   // "*.conf" must not take "a.conf~"
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file => new SshConfigInclude(file, File.ReadAllText(file)))
            .ToList();
    }
}
