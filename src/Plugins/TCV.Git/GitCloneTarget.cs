namespace TCV.Git;

/// <summary>What the clone dialog may do with a URL (see <see cref="GitCloneTarget.Resolve"/>).</summary>
public enum GitCloneUrlStatus
{
    /// <summary>Not something git could clone.</summary>
    Invalid,
    /// <summary>Clonable as typed: https, http, git://, file:// or a local path.</summary>
    Ready,
    /// <summary>An SSH link to a well-known host; <see cref="GitCloneUrl.Url"/> is its https twin.</summary>
    RewrittenToHttps,
    /// <summary>An SSH link to a host whose https address cannot be derived.</summary>
    SshUnsupported,
}

/// <summary>A clone URL as it will actually be used.</summary>
/// <param name="Status">Whether it can be cloned, and whether it was rewritten to get there.</param>
/// <param name="Url">The URL to hand to git: the https one for <see cref="GitCloneUrlStatus.RewrittenToHttps"/>,
/// the trimmed input otherwise.</param>
/// <param name="Host">The https host of a rewritten link, the SSH host of a refused one; empty otherwise.</param>
public sealed record GitCloneUrl(GitCloneUrlStatus Status, string Url, string Host)
{
    public bool CanClone => Status is GitCloneUrlStatus.Ready or GitCloneUrlStatus.RewrittenToHttps;
}

/// <summary>
/// Reads a clone URL well enough to pre-fill the target folder. Pure: pasting a link should name
/// the destination before anything touches the network, and the four URL shapes git accepts
/// (https, ssh://, the scp-like <c>git@host:owner/repo</c>, and a local path) do not parse the same
/// way — <see cref="Uri"/> alone rejects the scp form outright.
/// </summary>
public static class GitCloneTarget
{
    /// <summary>
    /// Folder name a clone of <paramref name="url"/> should land in — the last path segment with any
    /// <c>.git</c> suffix removed — or an empty string when the URL carries no usable name.
    /// </summary>
    public static string RepositoryName(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return string.Empty;

        // Trailing slashes are common when the URL is copied out of a browser address bar.
        trimmed = trimmed.TrimEnd('/', '\\');
        if (trimmed.Length == 0) return string.Empty;

        // The scp-like form has no scheme: everything after the colon is the path.
        var scpColon = ScpPathStart(trimmed);
        var path = scpColon >= 0 ? trimmed[scpColon..] : StripScheme(trimmed);

        var lastSeparator = path.LastIndexOfAny(new[] { '/', '\\' });
        var name = lastSeparator >= 0 ? path[(lastSeparator + 1)..] : path;

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        // Query strings and fragments are not part of a repo name but do survive a copy-paste.
        name = name.Split('?', '#')[0].Trim();

        return IsUsableFolderName(name) ? name : string.Empty;
    }

    /// <summary>
    /// Whether the text looks like something git could clone. Cheap shape check, not a reachability
    /// test: the dialog uses it to keep the Clone button honest, and the real verdict comes from git.
    /// </summary>
    public static bool LooksLikeRepositoryUrl(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (trimmed.Length == 0) return false;

        // A local path (a drive path or a UNC share) clones fine and is worth supporting: it is how
        // you branch off a colleague's checkout without a remote. It is checked first because it is
        // the one form where a space is legitimate (a folder like 'my repos').
        if (trimmed.Length > 2 && trimmed[1] == ':' && (trimmed[2] == '\\' || trimmed[2] == '/')
            || trimmed.StartsWith(@"\\", StringComparison.Ordinal))
            return RepositoryName(trimmed).Length > 0;

        if (trimmed.Contains(' ')) return false;

        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("git://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            return RepositoryName(trimmed).Length > 0;

        return ScpPathStart(trimmed) > 0 && RepositoryName(trimmed).Length > 0;
    }

    /// <summary>
    /// SSH host → the host serving the same repositories over https, for the hosts where the two
    /// paths are known to match. The <c>ssh.</c>/<c>altssh.</c> names are the port-443 SSH
    /// endpoints the same services publish for firewalled networks.
    /// </summary>
    private static readonly Dictionary<string, string> HttpsTwins = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github.com"] = "github.com",
        ["ssh.github.com"] = "github.com",
        ["gitlab.com"] = "gitlab.com",
        ["altssh.gitlab.com"] = "gitlab.com",
        ["bitbucket.org"] = "bitbucket.org",
        ["altssh.bitbucket.org"] = "bitbucket.org",
        ["codeberg.org"] = "codeberg.org",
        ["gitea.com"] = "gitea.com",
        ["git.sr.ht"] = "git.sr.ht",
    };

    /// <summary>
    /// Decides what a clone of <paramref name="url"/> will really use. libgit2 in this build has no
    /// SSH transport (and ADR-0015 keeps credentials in-memory tokens only), so an SSH link always
    /// failed — after a network round trip, with a native message. A link to a well-known host is
    /// rewritten to the https URL of the same repository; any other SSH link is refused up front,
    /// since its https address (if there is one) cannot be guessed.
    /// </summary>
    public static GitCloneUrl Resolve(string? url)
    {
        var trimmed = (url ?? string.Empty).Trim();
        if (!LooksLikeRepositoryUrl(trimmed))
        {
            return new GitCloneUrl(GitCloneUrlStatus.Invalid, trimmed, string.Empty);
        }

        if (!TrySplitSsh(trimmed, out var host, out var path))
        {
            return new GitCloneUrl(GitCloneUrlStatus.Ready, trimmed, string.Empty);
        }

        if (!HttpsTwins.TryGetValue(host, out var httpsHost))
        {
            return new GitCloneUrl(GitCloneUrlStatus.SshUnsupported, trimmed, host);
        }

        return new GitCloneUrl(GitCloneUrlStatus.RewrittenToHttps, $"https://{httpsHost}/{path}", httpsHost);
    }

    /// <summary>
    /// Splits an SSH remote — <c>ssh://[user@]host[:port]/path</c> or the scp-like
    /// <c>[user@]host:path</c> — into its host and its repository path (no leading slash). The user
    /// and port belong to SSH and have no meaning over https, so they are dropped.
    /// </summary>
    private static bool TrySplitSsh(string url, out string host, out string path)
    {
        host = string.Empty;
        path = string.Empty;

        string authority;
        if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = url["ssh://".Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0)
            {
                return false;
            }

            authority = rest[..slash];
            path = rest[(slash + 1)..];

            // ssh://host:22/path — the port, unless the host is a bracketed IPv6 literal.
            var portColon = authority.LastIndexOf(':');
            if (portColon >= 0 && !authority.EndsWith(']'))
            {
                authority = authority[..portColon];
            }
        }
        else
        {
            var colon = ScpPathStart(url);
            if (colon <= 0)
            {
                return false;
            }

            authority = url[..(colon - 1)];
            path = url[colon..];
        }

        var at = authority.LastIndexOf('@');
        host = (at >= 0 ? authority[(at + 1)..] : authority).Trim();
        path = path.TrimStart('/').TrimEnd('/');
        return host.Length > 0 && path.Length > 0;
    }

    /// <summary>
    /// Index just past the colon of a scp-like URL (<c>git@github.com:owner/repo.git</c>), or -1.
    /// A drive letter is not one: <c>D:\repos</c> has its colon at index 1, and treating it as a
    /// host separator would name the folder after the whole path.
    /// </summary>
    private static int ScpPathStart(string url)
    {
        if (url.Contains("://", StringComparison.Ordinal)) return -1;

        var colon = url.IndexOf(':');
        if (colon <= 1) return -1;                       // absent, or a drive letter
        if (url.AsSpan(0, colon).Contains('/')) return -1; // a path that happens to contain a colon

        return colon + 1;
    }

    private static string StripScheme(string url)
    {
        var scheme = url.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0) return url;

        var afterScheme = url[(scheme + 3)..];

        // Drop the authority (user@host:port); what remains is the path the repo name comes from.
        // No path at all means no repository — "https://host" must not be read as a repo called
        // "host".
        var firstSlash = afterScheme.IndexOf('/');
        return firstSlash < 0 ? string.Empty : afterScheme[firstSlash..];
    }

    /// <summary>Rejects the leftovers a bad paste produces — an empty name, a bare host, "." or "..".</summary>
    private static bool IsUsableFolderName(string name)
    {
        if (name.Length == 0 || name == "." || name == "..") return false;
        return name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0;
    }
}
