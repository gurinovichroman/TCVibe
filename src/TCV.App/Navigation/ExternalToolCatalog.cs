using System.IO;
using TCV.App.Settings;
using TCV.Core.Domain;

namespace TCV.App.Navigation;

/// <summary>
/// The editors and agent CLIs the app knows how to launch, in preference order, plus the pure
/// argument templating behind them. Data only — no process launching, no file system probing (that
/// is <see cref="Services.ExternalToolService"/>), which keeps both halves testable.
/// </summary>
/// <remarks>
/// Argument templates follow the macro dialect of user tools (<c>%P %N %E</c>, see
/// <c>UserToolsMacroExpander</c>) plus <c>%F</c> — the selected paths — because an editor is
/// launched against a selection, not against a "focused name". A template that opens the directory
/// *and* the file (<c>"%P" %F</c>) is deliberate: every editor of that family then shows the file
/// inside its project window rather than as a lone buffer.
/// </remarks>
public static class ExternalToolCatalog
{
    /// <summary>Editors, best-known first — the order «open in editor» picks a default from.</summary>
    public static IReadOnlyList<ExternalToolTemplate> Editors { get; } =
    [
        new("VS Code",          "code",           "\"%P\" %F",
            [@"%LOCALAPPDATA%\Programs\Microsoft VS Code\bin\code.cmd",
             @"%ProgramFiles%\Microsoft VS Code\bin\code.cmd"]),
        new("VS Code Insiders", "code-insiders",  "\"%P\" %F",
            [@"%LOCALAPPDATA%\Programs\Microsoft VS Code Insiders\bin\code-insiders.cmd"]),
        new("Cursor",           "cursor",         "\"%P\" %F",
            [@"%LOCALAPPDATA%\Programs\cursor\resources\app\bin\cursor.cmd"]),
        new("Windsurf",         "windsurf",       "\"%P\" %F",
            [@"%LOCALAPPDATA%\Programs\Windsurf\bin\windsurf.cmd"]),
        new("Zed",              "zed",            "\"%P\" %F",
            [@"%LOCALAPPDATA%\Programs\Zed\bin\zed.exe"]),
        new("Rider",            "rider",          "\"%P\" %F",
            [@"%LOCALAPPDATA%\JetBrains\Toolbox\scripts\rider.cmd"]),
        new("Sublime Text",     "subl",           "\"%P\" %F",
            [@"%ProgramFiles%\Sublime Text\subl.exe"]),
        new("Notepad++",        "notepad++",      "%F",
            [@"%ProgramFiles%\Notepad++\notepad++.exe",
             @"%ProgramFiles(x86)%\Notepad++\notepad++.exe"]),
        new("Neovim",           "nvim",           "%F", [], InTerminal: true),
    ];

    /// <summary>Coding-agent CLIs. All of them are terminal UIs started in the panel's directory.</summary>
    public static IReadOnlyList<ExternalToolTemplate> Agents { get; } =
    [
        new("Claude Code",   "claude",       string.Empty, [], InTerminal: true),
        new("Codex CLI",     "codex",        string.Empty, [], InTerminal: true),
        new("Gemini CLI",    "gemini",       string.Empty, [], InTerminal: true),
        new("Cursor Agent",  "cursor-agent", string.Empty, [], InTerminal: true),
        new("Copilot CLI",   "copilot",      string.Empty, [], InTerminal: true),
        new("Aider",         "aider",        string.Empty, [], InTerminal: true),
    ];

    /// <summary>
    /// Fills the argument template in one pass. <paramref name="directory"/> is the panel path;
    /// <paramref name="selected"/> are full paths of the selected entries. Every value goes through
    /// <see cref="ShellQuoting"/>: quoted when it holds a blank or a <c>cmd.exe</c> operator (a file
    /// called <c>a&amp;calc.txt</c> is legal), and escaped when it sits inside the template's own
    /// quotes (a drive root <c>D:\</c> must not escape the closing quote). With nothing selected
    /// <c>%F</c> disappears together with the blank in front of it, so a template like
    /// <c>"%P" %F</c> degrades to "just open this folder".
    /// </summary>
    /// <param name="forCmd">The program may run through <c>cmd.exe</c> (a <c>.cmd</c> shim or
    /// <c>cmd /k</c>): values holding <c>%</c> or <c>!</c> are then refused with
    /// <see cref="UnsafeMacroValueException"/> instead of being expanded by the shell.</param>
    public static string ExpandArguments(
        string template, string directory, IReadOnlyList<string> selected, bool forCmd = false)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var first = selected.Count > 0 ? selected[0] : string.Empty;
        var name = Path.GetFileNameWithoutExtension(first);
        var extension = Path.GetExtension(first).TrimStart('.');

        IReadOnlyList<string>? Resolve(char macro) => macro switch
        {
            'P' => [directory],
            'F' => selected,
            'N' => name.Length == 0 ? [] : [name],
            'E' => extension.Length == 0 ? [] : [extension],
            _ => null,
        };

        return MacroTemplate.Expand(
            template, Resolve, eatSpaceBeforeEmpty: true,
            validate: forCmd ? ShellQuoting.IsSafeForCmd : null).Trim();
    }
}

/// <summary>A known tool: what to run, how to call it, and where else to look for it.</summary>
/// <param name="ProbePaths">Extra locations checked when the command is not on <c>PATH</c>;
/// environment variables in them are expanded before use.</param>
public sealed record ExternalToolTemplate(
    string Name,
    string Command,
    string Arguments,
    IReadOnlyList<string> ProbePaths,
    bool InTerminal = false)
{
    /// <summary>Turns the catalog entry into the settings shape stored in <c>settings.json</c>.</summary>
    public ExternalTool ToSettings() => new()
    {
        Name = Name,
        Command = Command,
        Arguments = Arguments,
        InTerminal = InTerminal,
    };
}
