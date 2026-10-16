using System.IO;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// What Windows will not accept as a single file or folder name. Pure, so the new-folder and
/// rename dialogs can say why OK is disabled instead of just graying it out.
/// </summary>
public static class FileNameRules
{
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// The localisation key of the reason <paramref name="name"/> cannot be used, or null when it can.
    /// An empty name is not an error message (the field is simply not filled yet) and returns null.
    /// </summary>
    public static string? GetErrorKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Name_InvalidChars";
        if (name is "." or "..") return "Name_Dots";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "Name_TrailingDot";

        // "CON" and "CON.txt" both open the console device, whatever the extension.
        var stem = name.Split('.')[0].TrimEnd();
        if (Reserved.Contains(stem)) return "Name_Reserved";

        return null;
    }

    public static bool IsValid(string? name) =>
        !string.IsNullOrWhiteSpace(name) && GetErrorKey(name) is null;
}
