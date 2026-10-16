namespace TCV.Git;

/// <summary>What a line of a unified diff is, so the viewer can colour it without parsing text itself.</summary>
public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    /// <summary>The <c>@@ -a,b +c,d @@</c> marker.</summary>
    Hunk,
    /// <summary><c>diff --git</c>, <c>index</c>, <c>---</c>, <c>+++</c> and the other preamble lines.</summary>
    Header,
}

/// <summary>One classified line of a diff.</summary>
public sealed record DiffLine(DiffLineKind Kind, string Text);

/// <summary>Splits a unified diff into classified lines. Pure, so the colouring rules are testable.</summary>
public static class GitDiffLines
{
    public static IReadOnlyList<DiffLine> Parse(string? diff)
    {
        var lines = new List<DiffLine>();
        if (string.IsNullOrEmpty(diff)) return lines;

        // Inside a hunk a line starting with "---" or "+++" is content (a removed "-- comment"), not a
        // file header, so the header prefixes only count before the first "@@" of each file.
        var inHunk = false;

        foreach (var raw in diff.Split('\n'))
        {
            var text = raw.TrimEnd('\r');

            DiffLineKind kind;
            if (text.StartsWith("diff --git", StringComparison.Ordinal))
            {
                inHunk = false;
                kind = DiffLineKind.Header;
            }
            else if (text.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
                kind = DiffLineKind.Hunk;
            }
            else if (!inHunk)
            {
                kind = DiffLineKind.Header;
            }
            else if (text.StartsWith('+'))
            {
                kind = DiffLineKind.Added;
            }
            else if (text.StartsWith('-'))
            {
                kind = DiffLineKind.Removed;
            }
            else
            {
                kind = DiffLineKind.Context;
            }

            lines.Add(new DiffLine(kind, text));
        }

        // The final split element is the empty tail after the last newline.
        if (lines.Count > 0 && lines[^1].Text.Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
