namespace TCV.Ai.Llm;

/// <summary>
/// The F3 viewer's "summarise this" request (ADR-0008: <em>on explicit request</em>).
/// Opt-in is not a detail here — automatically summarising every opened file would send the contents
/// of whatever the user looks at to an endpoint, which is precisely what ADR-0008 forbids.
/// </summary>
public static class SummaryPrompt
{
    /// <summary>
    /// Characters of file content sent. A summary of the first ~24k characters is genuinely useful;
    /// pushing a whole large file through a local model is slow and rarely better.
    /// </summary>
    public const int MaxContentChars = 24_000;

    private const string Instructions =
        "You summarise files for a developer who is deciding whether to read one. Reply with plain "
        + "text: two or three sentences on what the file is and what it does, then a short bullet list "
        + "of the notable parts (public types, entry points, configuration keys, sections). "
        + "No preamble, no code fences. If the content is truncated, say so instead of guessing at the rest.";

    public static IReadOnlyList<ChatMessage> Build(string fileName, string content)
    {
        var excerpt = CommitMessagePrompt.Truncate(content ?? string.Empty, MaxContentChars, out var truncated);

        var request = $"File: {fileName}\n"
                      + (truncated ? $"[showing the first {MaxContentChars} characters]\n" : string.Empty)
                      + "\n" + excerpt;

        return
        [
            ChatMessage.System(Instructions),
            ChatMessage.User(request),
        ];
    }
}
