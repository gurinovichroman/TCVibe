namespace TCV.Ai.Mcp;

/// <summary>Which family of location a path names.</summary>
public enum AgentPathKind
{
    /// <summary>An ordinary Windows path. Subject to the approved-roots scope.</summary>
    Local,

    /// <summary>Inside an archive file on disk: <c>zip://C:\a.zip!/dir/file</c>.</summary>
    Archive,

    /// <summary>Inside whatever a panel currently has mounted: <c>panel://active/dir/file</c>.</summary>
    Panel,
}

/// <summary>
/// A location as an agent is allowed to name it. This is the addressing contract behind
/// ADR-0012 — the differentiator being that a path can point inside an archive or a remote
/// mount, which no ordinary filesystem MCP server can reach.
/// <para>
/// Three forms, and the reason there are exactly three:
/// </para>
/// <list type="bullet">
/// <item><b>Local</b> — <c>D:\repo\src\file.cs</c>. Checked against the approved roots.</item>
/// <item><b>Archive</b> — <c>zip://D:\dist.zip!/logs/app.log</c>. The archive <em>file</em> is checked
/// against the approved roots; what is inside it needs no separate permission, and archives open
/// without credentials so the server can open one on demand.</item>
/// <item><b>Panel</b> — <c>panel://active/pub/app.log</c>. Deliberately not <c>sftp://user@host/…</c>:
/// reconstructing a remote connection from a path would mean the server holding credentials and
/// opening connections of its own. Addressing the panel instead means an agent reaches exactly what
/// the user already has open — the mount <em>is</em> the approval — and the server never authenticates
/// anything.</item>
/// </list>
/// </summary>
/// <param name="Container">The archive file for <see cref="AgentPathKind.Archive"/>,
/// <c>active</c>/<c>opposite</c> for <see cref="AgentPathKind.Panel"/>, the whole path for local.</param>
/// <param name="Inner">Path within the container; always starts with <c>/</c>. Empty for local.</param>
public sealed record AgentPathRef(AgentPathKind Kind, string Scheme, string Container, string Inner)
{
    /// <summary>Separator between a container and a path inside it, as in JAR URLs and 7-Zip.</summary>
    public const string InnerSeparator = "!/";

    public const string PanelScheme = "panel";
    public const string ActivePanel = "active";
    public const string OppositePanel = "opposite";

    /// <summary>Archive schemes the server may open by itself — none of them needs a credential.</summary>
    private static readonly HashSet<string> ArchiveSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "7z", "rar", "tar", "iso", "cab",
    };

    /// <summary>Schemes that only exist as a live mount, so they must be addressed via <c>panel://</c>.</summary>
    private static readonly HashSet<string> RemoteSchemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ftp", "ftps", "sftp", "dav", "davs", "s3", "azblob", "gdrive", "dropbox", "onedrive",
    };

    /// <summary>
    /// Parses an agent-supplied path. Throws <see cref="McpToolException"/> with a hint rather than
    /// returning null, because every caller would turn a null into the same structured refusal.
    /// </summary>
    public static AgentPathRef Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new McpToolException(McpError.Invalid("The path is empty.", "Pass an absolute path."));

        var trimmed = path.Trim();
        var schemeEnd = trimmed.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd <= 0)
            return new AgentPathRef(AgentPathKind.Local, string.Empty, trimmed, string.Empty);

        var scheme = trimmed[..schemeEnd];
        var rest = trimmed[(schemeEnd + 3)..];

        if (string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase))
            return new AgentPathRef(AgentPathKind.Local, "file", rest, string.Empty);

        if (string.Equals(scheme, PanelScheme, StringComparison.OrdinalIgnoreCase))
            return ParsePanel(rest);

        if (ArchiveSchemes.Contains(scheme))
        {
            var (container, inner) = SplitInner(rest);
            if (container.Length == 0)
            {
                throw new McpToolException(McpError.Invalid(
                    $"'{path}' names no archive file.",
                    $"Use {scheme}://<path to the archive>{InnerSeparator}<path inside it>."));
            }

            return new AgentPathRef(AgentPathKind.Archive, scheme.ToLowerInvariant(), container, inner);
        }

        if (RemoteSchemes.Contains(scheme))
        {
            throw new McpToolException(new McpError(
                McpError.Unsupported,
                $"'{scheme}://' paths are not addressed directly.",
                "Open the connection in a panel and use panel://active/… — the server does not "
                + "authenticate connections of its own."));
        }

        throw new McpToolException(McpError.Invalid(
            $"Unknown path scheme '{scheme}'.",
            "Use a Windows path, zip://…!/…, or panel://active/…"));
    }

    private static AgentPathRef ParsePanel(string rest)
    {
        var (which, inner) = SplitFirstSegment(rest);

        // "active" and "opposite" rather than "left"/"right": an agent that says «the other panel»
        // means the one the user is not standing in, and that is what the panel commands mean too.
        if (!string.Equals(which, ActivePanel, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(which, OppositePanel, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpToolException(McpError.Invalid(
                $"'panel://{rest}' names no panel.",
                $"Use panel://{ActivePanel}/… or panel://{OppositePanel}/…"));
        }

        return new AgentPathRef(AgentPathKind.Panel, PanelScheme, which.ToLowerInvariant(), inner);
    }

    /// <summary>True for the panel the user is standing in.</summary>
    public bool IsActivePanel =>
        Kind == AgentPathKind.Panel &&
        string.Equals(Container, ActivePanel, StringComparison.OrdinalIgnoreCase);

    /// <summary>Renders the reference back into the form an agent passed in — used in replies.</summary>
    public override string ToString() => Kind switch
    {
        AgentPathKind.Local => Container,
        AgentPathKind.Archive => $"{Scheme}://{Container}{InnerSeparator}{Inner.TrimStart('/')}",
        _ => $"{PanelScheme}://{Container}{Inner}",
    };

    /// <summary>
    /// The path inside a mount that a <c>panel://</c> reference names. Paths are absolute inside the
    /// mount — <c>panel://active/logs/app.log</c> is <c>/logs/app.log</c> whichever directory the panel
    /// happens to be standing in — because that is the form <c>tcv_selection</c> and <c>tcv_list</c>
    /// hand out, so what the server prints can be passed straight back.
    /// <para>
    /// <c>.</c> and <c>..</c> segments are folded here, and a <c>..</c> at the root stays at the root:
    /// the mount is the boundary an agent was given, and a path that climbs above it is not a path in it.
    /// </para>
    /// </summary>
    public static string MountPath(string inner)
    {
        var segments = new List<string>();
        foreach (var segment in inner.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;

            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return "/" + string.Join('/', segments);
    }

    /// <summary>Same reference pointing at a child — how a listing labels its entries.</summary>
    public AgentPathRef WithInner(string inner) =>
        this with { Inner = NormalizeInner(inner) };

    private static (string Container, string Inner) SplitInner(string rest)
    {
        var separator = rest.IndexOf(InnerSeparator, StringComparison.Ordinal);
        return separator < 0
            ? (rest, "/")
            : (rest[..separator], NormalizeInner(rest[(separator + InnerSeparator.Length)..]));
    }

    /// <summary>Splits "active/dir/file" into the panel name and the path under it.</summary>
    private static (string Panel, string Inner) SplitFirstSegment(string rest)
    {
        var normalized = rest.Replace('\\', '/').TrimStart('/');
        var slash = normalized.IndexOf('/');

        return slash < 0
            ? (normalized, "/")
            : (normalized[..slash], NormalizeInner(normalized[(slash + 1)..]));
    }

    private static string NormalizeInner(string inner)
    {
        var normalized = inner.Replace('\\', '/').Trim();
        if (normalized.Length == 0) return "/";
        if (!normalized.StartsWith('/')) normalized = "/" + normalized;
        return normalized.Length > 1 ? normalized.TrimEnd('/') : normalized;
    }
}
