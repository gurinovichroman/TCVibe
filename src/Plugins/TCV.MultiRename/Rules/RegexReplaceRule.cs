using System.Text.RegularExpressions;

namespace TCV.MultiRename.Rules;

/// <summary>Replaces all occurrences of a regex pattern with a replacement string.</summary>
public sealed class RegexReplaceRule : IRenameRule
{
    /// <summary>
    /// Longest a single match may take. The pattern is typed live and runs against every file on
    /// each keystroke on the UI thread, so a catastrophic one (<c>(a+)+$</c>) must fail fast
    /// instead of freezing the window.
    /// </summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private readonly Regex _regex;
    private readonly string _replacement;

    public RegexReplaceRule(string pattern, string replacement, bool caseSensitive = false)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            throw new ArgumentException("Pattern must not be empty.", nameof(pattern));
        }

        // Interpreted, not RegexOptions.Compiled: compiling costs more than a preview ever saves.
        var options = RegexOptions.None;
        if (!caseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        _regex = new Regex(pattern, options, MatchTimeout);
        _replacement = replacement ?? string.Empty;
    }

    public string Description => $"Replace /{_regex}/ → \"{_replacement}\"";

    /// <exception cref="RegexMatchTimeoutException">The pattern is too expensive for this name.</exception>
    public string Apply(string name) => _regex.Replace(name, _replacement);
}
