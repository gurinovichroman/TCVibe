using System.Text;

namespace TCV.Ai.Llm;

/// <summary>
/// Turns a working-tree state into a commit-message request, and the model's answer into something
/// safe to drop into the commit box.
/// <para>
/// The output is <em>always</em> a draft in an editable field — never an automatic commit
/// (ADR-0008). That constraint is why this class only produces and cleans text and has
/// no idea what a commit is.
/// </para>
/// </summary>
public static class CommitMessagePrompt
{
    /// <summary>Diff bytes sent at most. Enough for the model to see the shape of the change without
    /// paying for a whole refactor.</summary>
    public const int MaxDiffChars = 12_000;

    /// <summary>Conventional-commit subjects are meant to fit a terminal; this is the usual limit.</summary>
    public const int MaxSubjectLength = 72;

    private const string Instructions =
        "You write git commit messages. Given a list of changed files and a diff excerpt, reply with "
        + "the commit message only — no preamble, no code fences, no quotes.\n"
        + "Format: a single subject line in the imperative mood, at most 72 characters, optionally "
        + "followed by a blank line and short bullet points explaining why.\n"
        + "Describe what the change accomplishes, not which files were touched. If the diff is "
        + "truncated, describe what you can see and do not speculate about the rest.";

    /// <param name="files">Changed paths with their short status letter, as git reports them.</param>
    /// <param name="diff">Unified diff of the staged/working changes; may be empty.</param>
    public static IReadOnlyList<ChatMessage> Build(
        string branch, IReadOnlyList<(string Path, string Status)> files, string diff)
    {
        var request = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(branch))
            request.Append("Branch: ").AppendLine(branch);

        request.AppendLine($"Changed files ({files.Count}):");
        foreach (var (path, status) in files.Take(80))
            request.Append("  ").Append(string.IsNullOrWhiteSpace(status) ? "M" : status).Append(' ').AppendLine(path);

        if (files.Count > 80)
            request.AppendLine($"  … and {files.Count - 80} more");

        if (!string.IsNullOrWhiteSpace(diff))
        {
            var excerpt = Truncate(diff, MaxDiffChars, out var truncated);
            request.AppendLine().AppendLine("Diff:").AppendLine(excerpt);
            if (truncated) request.AppendLine("[diff truncated]");
        }

        return
        [
            ChatMessage.System(Instructions),
            ChatMessage.User(request.ToString()),
        ];
    }

    /// <summary>
    /// Makes a model reply usable as a commit message: drops the code fence it wraps text in even
    /// when told not to, drops a leading "Commit message:" label, and trims a subject that ran long.
    /// The body is left alone — it is the part a human is about to edit anyway.
    /// </summary>
    public static string Clean(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = raw.Replace("\r\n", "\n").Trim();

        text = StripFence(text);

        // Chat models like announcing the artefact before producing it.
        foreach (var label in (string[])["commit message:", "here is the commit message:", "message:"])
        {
            if (text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                text = text[label.Length..].TrimStart();
                break;
            }
        }

        var lines = text.Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        if (lines.Count == 0) return string.Empty;

        lines[0] = ShortenSubject(lines[0].Trim().Trim('"'));

        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static string StripFence(string text)
    {
        if (!text.StartsWith("```", StringComparison.Ordinal)) return text;

        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0) return text.Trim('`').Trim();

        var body = text[(firstBreak + 1)..];
        var closing = body.LastIndexOf("```", StringComparison.Ordinal);
        return (closing >= 0 ? body[..closing] : body).Trim();
    }

    /// <summary>
    /// Trims an over-long subject at a word boundary rather than mid-word. No ellipsis is added: the
    /// result goes into an editable field, and a stray "…" is one more thing to delete by hand.
    /// </summary>
    private static string ShortenSubject(string subject)
    {
        if (subject.Length <= MaxSubjectLength) return subject;

        var cut = subject.LastIndexOf(' ', MaxSubjectLength);
        return (cut > MaxSubjectLength / 2 ? subject[..cut] : subject[..MaxSubjectLength]).TrimEnd();
    }

    internal static string Truncate(string text, int max, out bool truncated)
    {
        truncated = text.Length > max;
        return truncated ? text[..max] : text;
    }
}
