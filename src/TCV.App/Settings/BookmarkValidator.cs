using System.IO;

namespace TCV.App.Settings;

/// <summary>What is wrong with a bookmark, if anything.</summary>
public enum BookmarkIssue
{
    None,
    EmptyName,
    EmptyPath,

    /// <summary>The path is relative ("src", "..\x"): it would resolve against whatever the process
    /// happens to be in, not against a place the user pointed at.</summary>
    NotAbsolute,
}

/// <summary>
/// Pure rules for a bookmark entry, kept out of the dialog so they are testable without a window.
/// Whether the folder exists is deliberately not checked: a bookmark to a USB stick or a share that
/// is asleep is still a valid bookmark.
/// </summary>
public static class BookmarkValidator
{
    public static BookmarkIssue Validate(string? name, string? path)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return BookmarkIssue.EmptyName;
        }

        var trimmed = path?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return BookmarkIssue.EmptyPath;
        }

        return Path.IsPathFullyQualified(trimmed) ? BookmarkIssue.None : BookmarkIssue.NotAbsolute;
    }
}
