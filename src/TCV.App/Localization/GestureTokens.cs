using System.Text.RegularExpressions;

namespace TCV.App.Localization;

/// <summary>
/// Shortcuts inside UI text (6.40). A string never types a key such as "Ctrl+W": it names the
/// command, <c>{gesture:CloseTab}</c>, and the key is filled in from the live bindings whenever the
/// string is read. A rebind in the hotkey dialog then shows up in the text as well, instead of the
/// text advertising a key that no longer does that (INV-05).
/// </summary>
/// <remarks>
/// A token in parentheses, <c>"Close tab ({gesture:CloseTab})"</c>, is a hint: with no gesture it
/// disappears together with the parentheses. A bare token is part of the sentence ("delete them
/// with {gesture:Delete}"), so with no gesture it falls back to the command's palette title.
/// </remarks>
public static partial class GestureTokens
{
    private const string Marker = "{gesture:";

    [GeneratedRegex(@"(?<hint> \()?\{gesture:(?<id>[A-Za-z0-9_]+)\}(?(hint)\))")]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Replaces every token in <paramref name="text"/>. <paramref name="gestureOf"/> returns the
    /// gesture as printed on the keys, or null when the command has none; <paramref name="titleOf"/>
    /// returns the palette title, or null for an unknown id.
    /// </summary>
    public static string Expand(string text, Func<string, string?> gestureOf, Func<string, string?> titleOf)
    {
        if (!text.Contains(Marker, StringComparison.Ordinal))
        {
            return text;
        }

        return TokenPattern().Replace(text, match =>
        {
            var id = match.Groups["id"].Value;
            var gesture = gestureOf(id);
            if (match.Groups["hint"].Success)
            {
                return string.IsNullOrEmpty(gesture) ? string.Empty : $" ({gesture})";
            }

            return string.IsNullOrEmpty(gesture) ? $"«{titleOf(id) ?? id}»" : gesture;
        });
    }

    /// <summary>The command ids the tokens of <paramref name="text"/> name, in order.</summary>
    public static IEnumerable<string> CommandIds(string text) =>
        TokenPattern().Matches(text).Select(m => m.Groups["id"].Value);
}
