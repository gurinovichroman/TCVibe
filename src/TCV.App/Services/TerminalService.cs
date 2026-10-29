using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace TCV.App.Services;

/// <summary>
/// Starts terminal windows: a shell in a Windows Terminal tab when it is installed, else in a console
/// window of its own. Which shell, and how, is <see cref="ShellService"/>'s.
/// </summary>
public sealed class TerminalService
{
    private readonly ILogger<TerminalService> _logger;

    public TerminalService(ILogger<TerminalService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Opens Windows Terminal's default profile in <paramref name="directory"/>. False when Windows
    /// Terminal is not installed.
    /// </summary>
    public bool OpenWindowsTerminal(string directory)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Terminal: directory does not exist: {Dir}", directory);
            return false;
        }

        return TryLaunch("wt.exe", Shells.ShellCommandLine.ForWindowsTerminalProfile(null, directory), directory);
    }

    /// <summary>
    /// Opens a terminal in <paramref name="directory"/> running <paramref name="commandLine"/> — the
    /// way an interactive CLI (an agent, nvim) has to be started. The shell is left open with
    /// <c>cmd /k</c> on purpose: when the program exits or fails to start, its output has to stay
    /// readable instead of vanishing with the window.
    /// </summary>
    public bool OpenWithCommand(string directory, string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return false;

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Terminal: directory does not exist: {Dir}", directory);
            return false;
        }

        // /s makes cmd strip exactly the outer pair of quotes and keep the ones inside, so a quoted
        // program path followed by quoted arguments survives; without it cmd guesses (and often
        // strips the wrong ones).
        //
        // wt.exe splits its command line at every ';' into separate tabs/panes, so a setup command
        // like "npm ci; npm run build" would run only its first half. Windows Terminal takes a
        // literal semicolon as "\;"; plain cmd.exe (the fallback below) needs no such treatment.
        var quotedCommand = TCV.Core.Domain.ShellQuoting.EscapeInsideQuotes(commandLine);
        if (TryLaunch("wt.exe", $"-d {QuoteArgument(directory)} cmd /s /k \"{EscapeForWindowsTerminal(quotedCommand)}\"", directory))
        {
            return true;
        }
        return TryLaunch("cmd.exe", $"/s /k \"{commandLine}\"", directory);
    }

    /// <summary>
    /// Starts a shell in <paramref name="directory"/>: Windows Terminal with
    /// <paramref name="windowsTerminalArguments"/> when it is installed, else <paramref name="direct"/> in
    /// a console window of its own (<see cref="Shells.ShellCommandLine"/> builds both).
    /// </summary>
    public bool OpenShell(string windowsTerminalArguments, Shells.ShellLaunch direct, string directory)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Terminal: directory does not exist: {Dir}", directory);
            return false;
        }

        return TryLaunch("wt.exe", windowsTerminalArguments, directory)
            || TryLaunch(direct.Program, Shells.ShellCommandLine.Arguments(direct), directory);
    }

    /// <summary>Escapes <c>;</c> so Windows Terminal passes it through instead of treating it as a
    /// subcommand separator. Already-escaped semicolons are left alone.</summary>
    public static string EscapeForWindowsTerminal(string commandLine)
    {
        var sb = new System.Text.StringBuilder(commandLine.Length + 4);
        for (var i = 0; i < commandLine.Length; i++)
        {
            var ch = commandLine[i];
            if (ch == ';' && (i == 0 || commandLine[i - 1] != '\\')) sb.Append('\\');
            sb.Append(ch);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes a path as one command-line argument. The trailing backslash matters: at a drive root
    /// (<c>D:\</c>) a naive <c>"D:\"</c> escapes its own closing quote, and the receiving program
    /// parses the rest of the line as part of that argument — <c>wt.exe</c> then opens somewhere
    /// else while <see cref="Process.Start"/> still reports success. Doubling the trailing run is
    /// what <c>CommandLineToArgvW</c> expects.
    /// </summary>
    public static string QuoteArgument(string path) => TCV.Core.Domain.ShellQuoting.QuoteArgument(path);

    private bool TryLaunch(string exe, string? arguments, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = workingDir,
                UseShellExecute = true,
            };
            Process.Start(psi);
            _logger.LogInformation("Opened terminal: {Exe} in {Dir}", exe, workingDir);
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            _logger.LogDebug("{Exe} not found: {Msg}", exe, ex.Message);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to launch {Exe}", exe);
            return false;
        }
    }
}
