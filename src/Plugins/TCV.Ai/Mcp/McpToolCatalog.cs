using System.Text.Json.Nodes;

namespace TCV.Ai.Mcp;

/// <summary>
/// The tools this server advertises, with their JSON schemas.
/// <para>
/// The set is deliberately narrow and ordered by the value it adds over what an agent can already
/// do. <c>tcv_selection</c> is first because panel context is information the agent cannot obtain at
/// all; <c>tcv_list</c>/<c>tcv_read</c> earn their place only through VFS paths (inside archives, over
/// SFTP/WebDAV), which is the actual differentiator; the mutating three exist to be
/// <em>supervised</em>, not to be faster than the agent's own file tools. We do not pretend local
/// file operations are a feature here, and this catalog does not advertise them as one.
/// </para>
/// </summary>
public static class McpToolCatalog
{
    public const string Selection = "tcv_selection";
    public const string List = "tcv_list";
    public const string Read = "tcv_read";
    public const string Search = "tcv_search";
    public const string Operations = "tcv_operations";
    public const string Copy = "tcv_copy";
    public const string Move = "tcv_move";
    public const string Delete = "tcv_delete";

    /// <summary>Default cap on <c>tcv_read</c>, so one call cannot swallow an agent's context.</summary>
    public const int DefaultReadBytes = 65_536;

    /// <summary>Hard cap regardless of what the caller asks for.</summary>
    public const int MaxReadBytes = 1_048_576;

    /// <summary>The names that mutate the file system and therefore need the write gate.</summary>
    public static IReadOnlyList<string> MutatingTools { get; } = [Copy, Move, Delete];

    /// <summary>
    /// How every path argument is described. Exactly the three forms <c>AgentPathRef.Parse</c>
    /// accepts — advertising <c>sftp://</c> or <c>dav://</c> here would send agents at an address the
    /// parser refuses unconditionally, and leave the one that works (<c>panel://</c>) unmentioned.
    /// </summary>
    private const string PathArgument =
        "A Windows path (D:\\repo\\src\\a.cs), an archive path (zip://D:\\dist.zip!/logs/app.log), or "
        + "a panel path (panel://active/… or panel://opposite/…) for whatever the panel has mounted — "
        + "that is how an SFTP/FTP/WebDAV connection is reached; those schemes are not addressed directly. "
        + "A panel's archive is readable when its file is inside an approved directory, a panel's "
        + "server connection only when the user allowed it.";

    /// <summary>
    /// Tool descriptors for <c>tools/list</c>. When <paramref name="writesEnabled"/> is false the
    /// mutating tools are left out entirely rather than advertised and then refused — an agent that
    /// cannot see a tool does not spend turns trying it.
    /// </summary>
    public static JsonArray Describe(bool writesEnabled)
    {
        var tools = new JsonArray
        {
            Tool(Selection,
                "Files selected in the active panel, both panel directories, the active workspace and "
                + "git branch. Use this to resolve what the user means by \"these files\" or \"here\". "
                + "A directory or selected file outside the approved directories comes back empty or "
                + "left out.",
                Object(new JsonObject(), [])),

            Tool(List,
                "List a directory. Accepts a Windows path, an archive path (zip://C:\\a.zip!/dir) or a "
                + "panel path (panel://active/dir), so archives and the remote connection the user "
                + "already has open can be listed without downloading them first.",
                Object(new JsonObject
                {
                    ["path"] = Prop("string", PathArgument + " Defaults to the active panel's directory."),
                }, [])),

            Tool(Read,
                "Read a file as text. Works for archive and panel paths too, so a file inside an "
                + "archive or on a mounted remote connection can be read directly.",
                Object(new JsonObject
                {
                    ["path"] = Prop("string", PathArgument),
                    ["max_bytes"] = Prop("integer",
                        $"Byte cap; default {DefaultReadBytes}, maximum {MaxReadBytes}. The reply says whether it was truncated."),
                }, ["path"])),

            Tool(Search,
                "Search the project by content or by name, using the manager's gitignore-aware index "
                + "instead of walking the tree. Set semantic=true for meaning-based search over the "
                + "local embedding index.",
                Object(new JsonObject
                {
                    ["query"] = Prop("string", "Text to look for, or a natural-language query when semantic=true."),
                    ["path"] = Prop("string", "Directory to search under (Windows path). Defaults to the active panel's directory."),
                    ["semantic"] = Prop("boolean", "Use the local embedding index rather than exact matching."),
                    ["names_only"] = Prop("boolean", "Match file names instead of file contents."),
                    ["limit"] = Prop("integer", "Maximum hits to return (default 50)."),
                }, ["query"])),

            Tool(Operations,
                "Recent entries from the manager's operation log, newest first — what was copied, "
                + "moved or deleted, how it ended, and whether an agent asked for it.",
                Object(new JsonObject
                {
                    ["limit"] = Prop("integer", "How many entries (default 20)."),
                }, [])),
        };

        if (!writesEnabled) return tools;

        tools.Add(Tool(Copy,
            "Propose copying files. The user is shown the operation and approves or refuses it; "
            + "on approval it runs through the manager's queue and is recorded in the operation log, "
            + "so it stays visible. There is no undo.",
            Object(new JsonObject
            {
                ["sources"] = Array("string", "Files or directories to copy. " + PathArgument),
                ["destination"] = Prop("string", "Target directory. " + PathArgument),
            }, ["sources", "destination"])));

        tools.Add(Tool(Move,
            "Propose moving files. Approved by the user, then executed through the manager's queue.",
            Object(new JsonObject
            {
                ["sources"] = Array("string", "Files or directories to move. " + PathArgument),
                ["destination"] = Prop("string", "Target directory. " + PathArgument),
            }, ["sources", "destination"])));

        tools.Add(Tool(Delete,
            "Propose deleting files. Always confirmed by the user, and routed to the recycle bin when "
            + "the manager is configured that way.",
            Object(new JsonObject
            {
                ["paths"] = Array("string", "Files or directories to delete. " + PathArgument),
            }, ["paths"])));

        return tools;
    }

    private static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    private static JsonObject Object(JsonObject properties, string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
        };

        if (required.Length > 0)
        {
            var list = new JsonArray();
            foreach (var name in required) list.Add(name);
            schema["required"] = list;
        }

        return schema;
    }

    private static JsonObject Prop(string type, string description) => new()
    {
        ["type"] = type,
        ["description"] = description,
    };

    private static JsonObject Array(string itemType, string description) => new()
    {
        ["type"] = "array",
        ["description"] = description,
        ["items"] = new JsonObject { ["type"] = itemType },
    };
}
