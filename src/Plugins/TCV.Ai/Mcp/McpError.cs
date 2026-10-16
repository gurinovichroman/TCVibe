namespace TCV.Ai.Mcp;

/// <summary>
/// A failure shaped so the agent can fix it by itself: a stable machine code, a human message, and a
/// hint naming the next move. An agent given only "access denied" retries the
/// same call; one told «the path is outside the approved directories — call tcv_selection to see what
/// is in scope» does something different.
/// </summary>
public sealed record McpError(string Code, string Message, string Hint)
{
    // Codes are part of the contract with the agent: they may be added to, but not renamed.
    public const string InvalidParams = "invalid_params";
    public const string NoScope = "no_scope";
    public const string OutsideScope = "outside_scope";
    public const string SymlinkEscape = "symlink_escape";
    public const string NotFound = "not_found";
    public const string ReadOnly = "read_only";
    public const string Rejected = "rejected";
    public const string Unsupported = "unsupported";
    public const string Internal = "internal";

    public static McpError Invalid(string message, string hint = "") =>
        new(InvalidParams, message, hint);

    public static McpError NotFoundAt(string path) =>
        new(NotFound, $"Not found: {path}",
            "Check the path with tcv_list on its parent directory.");

    public static McpError WritesDisabled() =>
        new(ReadOnly, "Agent writes are disabled for this server.",
            "The user can enable them in Settings → Agent layer; until then propose the change in text.");

    public static McpError FromScope(ScopeResult scope) => scope.Verdict switch
    {
        ScopeVerdict.NoScope => new(NoScope, scope.Detail,
            "The user must approve a directory first: Settings → Agent layer, or «Approve this folder» in the app."),
        ScopeVerdict.SymlinkEscape => new(SymlinkEscape, scope.Detail,
            "Use the real path inside an approved directory instead of going through the link."),
        ScopeVerdict.OutsideScope => new(OutsideScope, scope.Detail,
            "Call tcv_selection to see the directories and selection currently in scope."),
        _ => new(InvalidParams, scope.Detail, "Pass an absolute path, or a VFS path such as zip://… or sftp://…"),
    };
}

/// <summary>Thrown by a bridge implementation to return a structured tool error to the agent.</summary>
public sealed class McpToolException : Exception
{
    public McpToolException(McpError error) : base(error.Message) => Error = error;

    public McpToolException(string code, string message, string hint = "")
        : this(new McpError(code, message, hint)) { }

    public McpError Error { get; }
}
