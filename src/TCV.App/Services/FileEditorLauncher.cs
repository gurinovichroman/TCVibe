using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.Settings;

namespace TCV.App.Services;

/// <summary>
/// F4: opens one file for editing — in the program chosen in the settings (Files → Editor), else the
/// way Windows edits that type (its "edit" verb, Notepad when it has none). A server file's temporary
/// copy goes the same way (<see cref="RemoteEditService"/>), so F4 means one editor everywhere.
/// Never the type's default verb: for a script that is "run", and F4 on a server's <c>deploy.bat</c>
/// once ran it on this machine.
/// </summary>
public sealed class FileEditorLauncher
{
    private readonly AppSettings _settings;
    private readonly ExternalToolService _tools;
    private readonly ILogger<FileEditorLauncher> _logger;

    public FileEditorLauncher(AppSettings settings, ExternalToolService tools, ILogger<FileEditorLauncher> logger)
    {
        _settings = settings;
        _tools = tools;
        _logger = logger;
    }

    /// <summary>The program F4 runs, for messages: its command, or null for the Windows association.</summary>
    public string? ConfiguredEditor => ToolFor(_settings.ExternalTools.FileEditor)?.Command;

    /// <summary>Opens <paramref name="path"/> (a Windows path) for editing; false when nothing could be started.</summary>
    public bool Open(string path) => TryOpen(path, out _);

    /// <summary>
    /// As <see cref="Open"/>, with the value that made a chosen editor refuse the file: a name holding
    /// <c>%</c> or <c>!</c> cannot be handed to a <c>.cmd</c> shim such as <c>code.cmd</c>, and the caller
    /// should blame the file, not the editor. Null when the failure was something else (or none).
    /// </summary>
    public bool TryOpen(string path, out string? refused)
    {
        refused = null;
        var tool = ToolFor(_settings.ExternalTools.FileEditor);
        if (tool is null)
        {
            return FileSystem.Native.Shell32.OpenForEditing(path);
        }

        // The file's folder is the working directory and %P, as if the panel stood there.
        var folder = Path.GetDirectoryName(path) ?? string.Empty;
        if (_tools.Launch(tool, folder, [path], out refused))
        {
            return true;
        }

        _logger.LogWarning("F4 editor {Command} could not open {Path}{Reason}", tool.Command, path,
            refused is null ? string.Empty : $" ({refused} is not safe for cmd.exe)");
        return false;
    }

    /// <summary>
    /// The configured editor as a launchable tool, or null when none is set (the Windows association
    /// edits the file). Arguments without <c>%F</c> get the file at the end, so a bare program name or
    /// a switch like <c>-n</c> still opens the file; empty arguments are just the file.
    /// </summary>
    public static ExternalTool? ToolFor(ExternalTool? configured)
    {
        if (configured is null || string.IsNullOrWhiteSpace(configured.Command))
        {
            return null;
        }

        var arguments = (configured.Arguments ?? string.Empty).Trim();
        if (!arguments.Contains("%F", StringComparison.Ordinal))
        {
            arguments = arguments.Length == 0 ? "%F" : arguments + " %F";
        }

        return new ExternalTool
        {
            Name = "F4",
            Command = configured.Command.Trim(),
            Arguments = arguments,
            InTerminal = configured.InTerminal,
        };
    }
}
