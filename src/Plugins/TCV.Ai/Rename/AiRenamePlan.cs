using System.Text;
using System.Text.Json.Nodes;
using TCV.Ai.Llm;

namespace TCV.Ai.Rename;

/// <summary>One row of the rename preview table.</summary>
/// <param name="Proposed">Sanitised target name; equals <paramref name="Original"/> when nothing usable came back.</param>
/// <param name="Note">Why the row is the way it is — a collision resolved, an extension restored, a refusal.</param>
public sealed record AiRenameItem(string Original, string Proposed, string Note)
{
    public bool Changed => !string.Equals(Original, Proposed, StringComparison.Ordinal);

    /// <summary>
    /// True when the model's answer had to be corrected (a path dropped, an extension restored, a
    /// collision suffixed). Such a row is offered but not pre-checked: the name is no longer quite
    /// what the model meant, so it is the user's call.
    /// </summary>
    public bool NeedsReview => Changed && Note.Length > 0;
}

/// <summary>
/// The sentences a preview row's <see cref="AiRenameItem.Note"/> is made of. English, because the
/// note is also what logs and tests read; the application translates each one for the Note column,
/// which is why they are named here rather than written inline.
/// </summary>
public static class AiRenameNotes
{
    public const string FolderDropped = "The proposed folder path was dropped.";
    public const string EmptyName = "The proposed name was empty.";
    public const string ExtensionKeptPrefix = "Extension kept as ";
    public const string ReservedPrefixed = "A reserved device name was prefixed.";
    public const string Shortened = "The name was shortened.";
    public const string CollisionSuffixed = "A numeric suffix resolved a name collision.";
    public const string NoName = "The model proposed no name.";
    public const string Unchanged = "Unchanged.";

    public static string ExtensionKept(string extension) => ExtensionKeptPrefix + extension + ".";
}

/// <summary>Why a name typed into the preview table cannot be applied.</summary>
public enum AiRenameProblem
{
    None,
    Empty,
    InvalidCharacters,
    TrailingDotOrSpace,
    ReservedName,
    TooLong,
    DuplicateInBatch,
    AlreadyExists,
}

/// <summary>
/// Content-aware batch rename (6.15): builds the request, then turns whatever the model says into a
/// preview table that is safe to execute.
/// <para>
/// Preview-first is not a UI preference, it is the contract (ADR-0008, INV-11): nothing here renames
/// anything. <see cref="Parse"/> is the interesting half — a model asked for file names will
/// cheerfully return path separators, invented extensions, duplicate names and Windows device names,
/// and every one of those has to be neutralised before a human is shown a list they might approve.
/// </para>
/// </summary>
public static class AiRenamePlan
{
    /// <summary>Content excerpt per file included in the request.</summary>
    public const int MaxExcerptChars = 1_200;

    /// <summary>Files per request. Enough to give the model naming context, small enough to stay reliable.</summary>
    public const int MaxBatch = 40;

    /// <summary>Windows name-length ceiling with room for a collision suffix.</summary>
    private const int MaxNameLength = 120;

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string Instructions =
        "You rename badly-named files (scans, downloads, camera output) from their content.\n"
        + "Reply with a JSON array only — no prose, no code fences — of objects "
        + "{\"from\": \"<current name>\", \"to\": \"<new name>\"}.\n"
        + "Rules: keep the original extension; use lowercase words separated by hyphens; put a date "
        + "first as YYYY-MM-DD when the content states one; be specific but under 60 characters; "
        + "no folder paths. Omit a file entirely if its content does not tell you what it is — "
        + "a guessed name is worse than the original.";

    /// <param name="files">Name plus a text excerpt of the content; the excerpt may be empty.</param>
    public static IReadOnlyList<ChatMessage> BuildPrompt(IReadOnlyList<(string Name, string Excerpt)> files)
    {
        var request = new StringBuilder();
        request.AppendLine($"Rename these {files.Count} files:").AppendLine();

        foreach (var (name, excerpt) in files.Take(MaxBatch))
        {
            request.Append("### ").AppendLine(name);
            if (!string.IsNullOrWhiteSpace(excerpt))
            {
                request.AppendLine(CommitMessagePrompt.Truncate(excerpt.Trim(), MaxExcerptChars, out var cut));
                if (cut) request.AppendLine("[excerpt truncated]");
            }
            else
            {
                request.AppendLine("[no readable text content]");
            }

            request.AppendLine();
        }

        return
        [
            ChatMessage.System(Instructions),
            ChatMessage.User(request.ToString()),
        ];
    }

    /// <summary>
    /// Builds the preview table from a model reply. Every original appears in the result exactly
    /// once — a file the model skipped or mangled comes back unchanged with a note, so the table is
    /// always a complete account of the selection rather than a partial one the user has to diff.
    /// </summary>
    /// <param name="reply">The model's answer.</param>
    /// <param name="originals">Names in the selection.</param>
    /// <param name="existingNames">Every other name in the directory. A proposed name must not land on
    /// one of them either — the selection is only part of what is in the folder.</param>
    public static IReadOnlyList<AiRenameItem> Parse(
        string reply, IReadOnlyList<string> originals, IEnumerable<string>? existingNames = null)
    {
        var suggestions = ExtractPairs(reply);
        var items = new List<AiRenameItem>(originals.Count);

        // Collisions are checked against the whole selection, including names nobody is changing:
        // renaming a.jpg to b.jpg is broken if b.jpg is sitting right there untouched — and against
        // the rest of the folder.
        var taken = new HashSet<string>(originals, StringComparer.OrdinalIgnoreCase);
        if (existingNames is not null) taken.UnionWith(existingNames);

        foreach (var original in originals)
        {
            if (!suggestions.TryGetValue(original, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                items.Add(new AiRenameItem(original, original, AiRenameNotes.NoName));
                continue;
            }

            var (candidate, note) = Sanitize(raw, original);

            if (string.Equals(candidate, original, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(new AiRenameItem(original, original, note.Length > 0 ? note : AiRenameNotes.Unchanged));
                continue;
            }

            // The original leaves the taken set as the rename frees its name; the new one enters it.
            taken.Remove(original);
            var unique = MakeUnique(candidate, taken, ref note);
            taken.Add(unique);

            items.Add(new AiRenameItem(original, unique, note));
        }

        return items;
    }

    /// <summary>
    /// Reads the from/to pairs out of a reply. Tries JSON first (what we asked for) and falls back to
    /// <c>old =&gt; new</c> lines, which is what models produce when they ignore the format.
    /// </summary>
    private static Dictionary<string, string> ExtractPairs(string reply)
    {
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(reply)) return pairs;

        var text = reply.Trim();

        // Fenced or prefixed output: take the outermost array.
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start >= 0 && end > start)
        {
            try
            {
                if (JsonNode.Parse(text[start..(end + 1)]) is JsonArray array)
                {
                    foreach (var node in array)
                    {
                        var from = node?["from"]?.GetValue<string>();
                        var to = node?["to"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
                            pairs[from.Trim()] = to.Trim();
                    }

                    if (pairs.Count > 0) return pairs;
                }
            }
            catch
            {
                // Fall through to the line format below.
            }
        }

        foreach (var line in text.Split('\n'))
        {
            var separator = line.IndexOf("=>", StringComparison.Ordinal);
            if (separator <= 0) continue;

            var from = line[..separator].Trim().TrimStart('-', '*', ' ').Trim('"', '`', ' ');
            var to = line[(separator + 2)..].Trim().Trim('"', '`', ' ');

            if (from.Length > 0 && to.Length > 0) pairs[from] = to;
        }

        return pairs;
    }

    /// <summary>
    /// Makes a model-proposed string into a legal Windows file name that keeps the original extension.
    /// Returns the original name when nothing usable survives — never a half-scrubbed guess.
    /// </summary>
    internal static (string Name, string Note) Sanitize(string proposed, string original)
    {
        var note = string.Empty;

        // Models return "2026/invoices/receipt.pdf" often enough to matter; only the leaf is a name.
        var candidate = proposed.Replace('\\', '/');
        var lastSlash = candidate.LastIndexOf('/');
        if (lastSlash >= 0)
        {
            candidate = candidate[(lastSlash + 1)..];
            note = AiRenameNotes.FolderDropped;
        }

        candidate = candidate.Trim().Trim('"', '\'');
        foreach (var bad in Invalid) candidate = candidate.Replace(bad, '_');

        // Windows silently strips these, which would turn "report." into a name that does not match
        // what the preview promised.
        candidate = candidate.TrimEnd(' ', '.');

        if (candidate.Length == 0) return (original, AiRenameNotes.EmptyName);

        var originalExtension = Path.GetExtension(original);
        if (originalExtension.Length > 0 &&
            !candidate.EndsWith(originalExtension, StringComparison.OrdinalIgnoreCase))
        {
            // Changing the extension changes which program opens the file — that is not a rename.
            var proposedExtension = Path.GetExtension(candidate);
            candidate = proposedExtension.Length > 0
                ? Path.GetFileNameWithoutExtension(candidate) + originalExtension
                : candidate + originalExtension;

            note = Append(note, AiRenameNotes.ExtensionKept(originalExtension));
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(candidate)))
        {
            candidate = "_" + candidate;
            note = Append(note, AiRenameNotes.ReservedPrefixed);
        }

        if (candidate.Length > MaxNameLength)
        {
            var extension = Path.GetExtension(candidate);
            var stem = Path.GetFileNameWithoutExtension(candidate);

            // A dot-prefixed single token (".a-very-long-…") is all extension and no stem, and an
            // extension can be longer than the whole budget: in both cases there is nothing to keep
            // the name around, so the name itself is cut. Trimming the stem to a negative or
            // over-long length is how this used to throw out of an async void Loaded handler.
            candidate = stem.Length == 0 || extension.Length >= MaxNameLength
                ? candidate[..MaxNameLength].TrimEnd(' ', '.')
                : stem[..Math.Min(stem.Length, MaxNameLength - extension.Length)] + extension;

            if (candidate.Length == 0) return (original, AiRenameNotes.EmptyName);

            note = Append(note, AiRenameNotes.Shortened);
        }

        return (candidate, note);
    }

    private static string MakeUnique(string candidate, HashSet<string> taken, ref string note)
    {
        if (!taken.Contains(candidate)) return candidate;

        var stem = Path.GetFileNameWithoutExtension(candidate);
        var extension = Path.GetExtension(candidate);

        for (int index = 2; index < 1000; index++)
        {
            var attempt = $"{stem} ({index}){extension}";
            if (taken.Contains(attempt)) continue;

            note = Append(note, AiRenameNotes.CollisionSuffixed);
            return attempt;
        }

        return candidate;
    }

    /// <summary>
    /// Checks a name the user typed into the preview table. The model's answers pass through
    /// <see cref="Sanitize"/>, but an edited cell used to go to the file system as typed — and a path
    /// separator or a drive letter there moves the file out of its folder.
    /// </summary>
    public static AiRenameProblem Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return AiRenameProblem.Empty;

        // Path separators, drive colons, wildcards and the rest of the reserved set.
        if (name.IndexOfAny(Invalid) >= 0) return AiRenameProblem.InvalidCharacters;

        if (name.EndsWith(' ') || name.EndsWith('.') || name.StartsWith(' '))
            return AiRenameProblem.TrailingDotOrSpace;

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name))) return AiRenameProblem.ReservedName;

        return name.Length > 255 ? AiRenameProblem.TooLong : AiRenameProblem.None;
    }

    /// <summary>
    /// Validates a whole batch about to be applied: every new name on its own, then against each other
    /// and against the folder's other files. Names freed by the batch itself count as free.
    /// </summary>
    /// <returns>The first problem per source name; empty when the batch can go ahead.</returns>
    public static IReadOnlyDictionary<string, AiRenameProblem> ValidateBatch(
        IReadOnlyList<(string From, string To)> pairs, IEnumerable<string> directoryNames)
    {
        var problems = new Dictionary<string, AiRenameProblem>(StringComparer.OrdinalIgnoreCase);
        var vacated = new HashSet<string>(pairs.Select(p => p.From), StringComparer.OrdinalIgnoreCase);
        var others = new HashSet<string>(
            directoryNames.Where(n => !vacated.Contains(n)), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (from, to) in pairs)
        {
            var own = Validate(to);
            if (own != AiRenameProblem.None) problems[from] = own;
            else if (!seen.Add(to)) problems[from] = AiRenameProblem.DuplicateInBatch;
            else if (others.Contains(to)) problems[from] = AiRenameProblem.AlreadyExists;
        }

        return problems;
    }

    private static string Append(string note, string addition) =>
        note.Length == 0 ? addition : note + " " + addition;
}
