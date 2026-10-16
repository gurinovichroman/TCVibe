using System.Text.Json;

namespace TCV.Ai.Mcp;

/// <summary>
/// Where the MCP server is and how to authenticate to it, written next to the executable (INV-03) as
/// <c>mcp-endpoint.json</c> while the server runs and deleted when it stops.
/// <para>
/// This is the discovery answer to the second half of ADR-0018: the port is
/// chosen at run time (a fixed one collides with whatever else the machine runs, and two portable
/// copies would fight over it), so a client needs somewhere to look it up. A file next to the
/// executable is the portable-ethics answer — no registry key, no well-known port, no daemon.
/// </para>
/// </summary>
/// <param name="Url">Full endpoint, e.g. <c>http://127.0.0.1:12955/mcp</c>.</param>
/// <param name="Token">Bearer token for the <c>Authorization</c> header; regenerated per run.</param>
/// <param name="ProcessId">Owning process, so a stale file left by a crash can be recognised.</param>
/// <param name="StartedUtc">When this server came up.</param>
/// <param name="ApprovedRoots">What is exposed — the file doubles as the honest disclosure (§ 3.6.4).</param>
public sealed record McpEndpointInfo(
    string Url,
    string Token,
    int ProcessId,
    DateTime StartedUtc,
    IReadOnlyList<string> ApprovedRoots)
{
    /// <summary>Ready-to-paste registration line for an MCP client that speaks HTTP.</summary>
    public string ClientCommandLine() =>
        $"claude mcp add --transport http tcv {Url} --header \"Authorization: Bearer {Token}\"";
}

/// <summary>Reads and writes <see cref="McpEndpointInfo"/>. Never throws — discovery is best-effort.</summary>
public static class McpEndpointFile
{
    public const string FileName = "mcp-endpoint.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static string DefaultPath => global::TCV.Core.Domain.AppPaths.Data(FileName);

    public static bool Write(McpEndpointInfo info, string? path = null)
    {
        try
        {
            File.WriteAllText(path ?? DefaultPath, JsonSerializer.Serialize(info, JsonOptions));
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static McpEndpointInfo? Read(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<McpEndpointInfo>(File.ReadAllText(file), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Deletes the file only when it was written by process <paramref name="processId"/>. Two windows
    /// started from one folder share the path, and the one closing must not remove the endpoint the
    /// other is still serving.
    /// </summary>
    public static bool DeleteIfOwnedBy(int processId, string? path = null)
    {
        var onDisk = Read(path);

        // Unreadable or missing: nothing of ours to protect, and a stale file is worse than none.
        if (onDisk is not null && onDisk.ProcessId != processId) return false;

        Delete(path);
        return true;
    }

    public static void Delete(string? path = null)
    {
        try
        {
            var file = path ?? DefaultPath;
            if (File.Exists(file)) File.Delete(file);
        }
        catch
        {
            // A leftover file is harmless: the token in it is dead once the process is gone.
        }
    }
}
