namespace TCV.Tools;

/// <summary>Represents a user-defined external tool / menu entry.</summary>
public sealed class UserTool
{
    /// <summary>Display name shown in the Tools menu and dialog list.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Path to the executable (may contain macros like %P, %N, %E, %S, %T).</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Command-line arguments passed to the executable.
    /// Supports macros: %P (active path), %N (file name without extension),
    /// %E (extension), %S (space-separated selected files), %T (opposite/target path).
    /// </summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>Working directory. Empty means use the active panel directory.</summary>
    public string WorkingDir { get; set; } = string.Empty;

    /// <summary>Optional hotkey string, e.g. "Ctrl+F1".</summary>
    public string? Hotkey { get; set; }

    /// <summary>An independent copy: the manager dialog edits copies and only writes them back on OK.</summary>
    public UserTool Clone() => new()
    {
        Name = Name,
        Command = Command,
        Arguments = Arguments,
        WorkingDir = WorkingDir,
        Hotkey = Hotkey,
    };
}
