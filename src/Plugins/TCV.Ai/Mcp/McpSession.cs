using System.Globalization;
using System.Text;

namespace TCV.Ai.Mcp;

/// <summary>
/// One MCP client connection, as far as a stateless HTTP transport has one: the id the server handed
/// out on <c>initialize</c> and the name the client gave itself.
/// <para>
/// The name matters because it is shown to the user in the approval prompt and stored as the origin of
/// the resulting operation. It is client-supplied text, so it is never taken raw
/// (<see cref="Sanitize"/>) and never shared: a second client connecting must not be able to rename the
/// first one's pending prompts.
/// </para>
/// </summary>
public sealed class McpSession
{
    /// <summary>Name used until a client introduces itself, and for requests carrying no session id.</summary>
    public const string DefaultClientName = "MCP client";

    /// <summary>Longest client name kept; the prompt has one line for it.</summary>
    public const int MaxClientNameLength = 48;

    public McpSession(string id, string clientName)
    {
        Id = id;
        ClientName = clientName;
        CreatedUtc = DateTime.UtcNow;
    }

    public string Id { get; }

    public string ClientName { get; }

    public DateTime CreatedUtc { get; }

    /// <summary>
    /// Makes a client-supplied name safe to show: one line, no control or formatting characters (a
    /// right-to-left override would let a name reorder the text around it), collapsed whitespace and a
    /// length cap. An empty result falls back to <see cref="DefaultClientName"/>.
    /// </summary>
    public static string Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DefaultClientName;

        var builder = new StringBuilder(Math.Min(raw.Length, MaxClientNameLength));
        var previousWasSpace = false;

        foreach (var ch in raw)
        {
            var category = char.GetUnicodeCategory(ch);
            var isSpace = char.IsWhiteSpace(ch);

            if (!isSpace && (char.IsControl(ch) || category == UnicodeCategory.Format)) continue;

            if (isSpace)
            {
                if (previousWasSpace || builder.Length == 0) continue;
                builder.Append(' ');
                previousWasSpace = true;
            }
            else
            {
                builder.Append(ch);
                previousWasSpace = false;
            }

            if (builder.Length >= MaxClientNameLength) break;
        }

        var text = builder.ToString().Trim();
        return text.Length == 0 ? DefaultClientName : text;
    }
}

/// <summary>
/// Per-request state passed through <see cref="McpProtocol"/>: the session the request arrived on (from
/// the <c>Mcp-Session-Id</c> header), and — when the request was <c>initialize</c> — the session it
/// opened, for the transport to announce in its response header.
/// </summary>
public sealed class McpRequestContext
{
    public McpSession? Session { get; init; }

    public McpSession? Opened { get; set; }
}
