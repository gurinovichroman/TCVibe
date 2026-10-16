using TCV.App.Commands;

namespace TCV.App.Help;

/// <summary>One command as the help window shows it: what it is called, where it lives and what it does.</summary>
/// <param name="CategoryKey">The language-neutral category, so a section survives a language switch.</param>
/// <param name="Location">Where to find it besides the palette ("Files › Copy"), or empty.</param>
public sealed record HelpCommand(
    string Id, string Title, string CategoryKey, string Category, string Gesture, string Description, string Location)
{
    /// <summary>The line under the description: the category, then where the command lives.</summary>
    public string Footer => Location.Length == 0 ? Category : $"{Category} · {Location}";
}

public enum HelpBlockKind
{
    Paragraph,
    Heading,
    Bullet,
}

/// <summary>One line of a guide article.</summary>
public sealed record HelpBlock(HelpBlockKind Kind, string Text);

/// <summary>A guide article: how a part of the program works, beyond what one command does.</summary>
public sealed record HelpTopic(string Id, string Title, IReadOnlyList<HelpBlock> Blocks);

/// <summary>
/// Builds the help from the command registry rather than from a hand-kept list, so it cannot fall
/// behind the program: a command added to <see cref="CommandCatalog"/> shows up here with its live
/// shortcut, and a test fails until it has a description (<c>Help_Cmd_&lt;Id&gt;</c> in both string
/// dictionaries). WPF-free: strings, gestures and menu locations come in as functions.
/// </summary>
public static class HelpCatalog
{
    public const string DescriptionPrefix = "Help_Cmd_";
    public const string TopicTitlePrefix = "Help_Topic_";

    /// <summary>The guide, in reading order. Each has <c>Help_Topic_&lt;Id&gt;</c> (title) and
    /// <c>Help_Topic_&lt;Id&gt;_Body</c> in both dictionaries.</summary>
    public static IReadOnlyList<string> TopicIds { get; } =
    [
        "Start", "Panels", "Files", "Workspaces", "Git", "Agents", "Remote", "Customize",
    ];

    /// <summary>
    /// Workspace2..9 are one idea — "the N-th workspace" — and nine near-identical rows would bury
    /// the rest of the section. They are shown once, on Workspace1's row.
    /// </summary>
    public static bool IsFolded(string commandId) =>
        commandId.Length == "Workspace2".Length
        && commandId.StartsWith("Workspace", StringComparison.Ordinal)
        && commandId[^1] is >= '2' and <= '9';

    /// <param name="items">Palette rows (localised title and category, live gesture), in catalogue order.</param>
    /// <param name="text">Localised string by key, or null when the key is missing.</param>
    /// <param name="location">Where the command sits in the menus, or empty.</param>
    public static IReadOnlyList<HelpCommand> BuildCommands(
        IEnumerable<CommandPaletteItem> items,
        Func<string, string?> text,
        Func<string, string> location)
    {
        var categoryKeys = CommandCatalog.All.ToDictionary(c => c.Id, c => c.CategoryEn, StringComparer.Ordinal);
        var rows = items.ToList();
        var result = new List<HelpCommand>(rows.Count);

        foreach (var item in rows)
        {
            if (IsFolded(item.Id))
            {
                continue;
            }

            var title = item.Title;
            var gesture = item.Gesture;
            if (item.Id == "Workspace1")
            {
                title = text("Help_Title_WorkspaceN") ?? title;
                var last = rows.FirstOrDefault(r => r.Id == "Workspace9")?.Gesture;
                if (gesture.Length > 0 && !string.IsNullOrEmpty(last))
                {
                    gesture = $"{gesture} … {last}";
                }
            }

            result.Add(new HelpCommand(
                item.Id,
                title,
                categoryKeys.GetValueOrDefault(item.Id, item.Category),
                item.Category,
                gesture,
                text(DescriptionPrefix + item.Id) ?? string.Empty,
                location(item.Id)));
        }

        return result;
    }

    public static IReadOnlyList<HelpTopic> BuildTopics(Func<string, string?> text) =>
        TopicIds
            .Select(id => new HelpTopic(
                id,
                text(TopicTitlePrefix + id) ?? id,
                HelpText.Parse(text(TopicTitlePrefix + id + "_Body") ?? string.Empty)))
            .ToList();
}

/// <summary>
/// The markup of a guide article, kept to what a string dictionary can carry: one block per line,
/// <c>## </c> starts a heading, <c>• </c> (or <c>- </c>) a bullet, anything else is a paragraph.
/// </summary>
public static class HelpText
{
    public static IReadOnlyList<HelpBlock> Parse(string body)
    {
        var blocks = new List<HelpBlock>();
        foreach (var raw in body.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                blocks.Add(new HelpBlock(HelpBlockKind.Heading, line[3..].Trim()));
            }
            else if (line.StartsWith("• ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal))
            {
                blocks.Add(new HelpBlock(HelpBlockKind.Bullet, line[2..].Trim()));
            }
            else
            {
                blocks.Add(new HelpBlock(HelpBlockKind.Paragraph, line));
            }
        }

        return blocks;
    }
}

/// <summary>
/// Search over the help. Every word of the query has to start a word somewhere in the entry —
/// title, description, category, shortcut or menu path — so "copy path" finds "Copy full path",
/// "ctrl+k" lists every chord and a stem ("переимен") finds every form of the word. Word starts,
/// not any substring: "pin" must find pinning, not "wrapping" and "typing".
/// </summary>
public static class HelpSearch
{
    public static IReadOnlyList<string> Words(string? query) =>
        (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool Matches(HelpCommand command, IReadOnlyList<string> words) =>
        AllIn(words, command.Title, command.Description, command.Category, command.Gesture, command.Location, command.Id);

    public static bool Matches(HelpTopic topic, IReadOnlyList<string> words) =>
        AllIn(words, [topic.Title, .. topic.Blocks.Select(b => b.Text)]);

    private static bool AllIn(IReadOnlyList<string> words, params string[] fields)
    {
        if (words.Count == 0)
        {
            return true;
        }

        return words.All(w => fields.Any(f => StartsAWord(f, w)));
    }

    private static bool StartsAWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.CurrentCultureIgnoreCase);
             at >= 0;
             at = text.IndexOf(word, at + 1, StringComparison.CurrentCultureIgnoreCase))
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1]))
            {
                return true;
            }
        }

        return false;
    }
}
