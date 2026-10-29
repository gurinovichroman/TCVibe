using System.IO;
using System.Text;
using TCV.Core.Domain;

namespace TCV.App.Shells;

/// <summary>
/// Turns a shell's argument template and a typed command into the process to start, and that process
/// into a command line for the shell itself or for <c>wt.exe</c>. Pure: every quoting rule here is
/// tested without starting anything.
/// </summary>
/// <remarks>
/// The template is split into arguments before anything is put into it, by the rules argv programs
/// read their command line with (<c>CommandLineToArgvW</c>), so <c>-c "%C; exec bash"</c> stays two
/// arguments whatever the command holds. Each argument is then written out again for its reader:
/// <list type="bullet">
/// <item>cmd.exe does not split its line into argv: after <c>/s /k</c> it runs the rest as written. The
/// typed command is the user's own cmd syntax (TRAP-41), so it goes in as typed, in one pair of quotes
/// that <c>/s</c> strips.</item>
/// <item>Every other shell (PowerShell, bash, wsl) reads argv: a value is quoted and escaped by those
/// rules (<see cref="ShellQuoting"/>), so quotes and backslashes in the command reach the shell as
/// typed.</item>
/// </list>
/// </remarks>
public static class ShellCommandLine
{
    /// <summary><c>%C</c>: the typed command, as one argument.</summary>
    public const char CommandMacro = 'C';

    /// <summary><c>%P</c>: the folder the command runs in.</summary>
    public const char FolderMacro = 'P';

    /// <summary>
    /// The process for <paramref name="command"/> typed in <paramref name="folder"/>. A template without
    /// <c>%C</c> gets the command as its last argument, so a shell that only needs a flag in front of
    /// the command (<c>nu -e</c>) works as written.
    /// </summary>
    /// <param name="program">The shell's program, environment variables already expanded.</param>
    public static ShellLaunch Build(string program, string template, string command, string folder)
    {
        var usedCommand = false;
        IReadOnlyList<string>? Resolve(char macro)
        {
            switch (macro)
            {
                case CommandMacro:
                    usedCommand = true;
                    return [command];
                case FolderMacro:
                    return [folder];
                default:
                    return null;
            }
        }

        var arguments = SplitArguments(template ?? string.Empty)
            .Select(argument => MacroTemplate.Expand(argument, Resolve, raw: true))
            .ToList();
        if (!usedCommand)
        {
            arguments.Add(command);
        }

        return new ShellLaunch(program, arguments, IsCmd(program));
    }

    /// <summary>
    /// The process that opens the shell itself in <paramref name="folder"/>, with no command: Enter on an
    /// empty command line. The template is written for a command, so the argument that holds <c>%C</c>
    /// goes, and with it the option right before it that hands the shell a command (<c>/k</c>,
    /// <c>-Command</c>, <c>-c</c>). Without <c>%C</c> the command would come last, so a last option goes
    /// (<c>nu -e</c>). The rest stays: <c>--login -i</c>, <c>-NoExit</c>, <c>-d Ubuntu</c>, <c>%P</c>.
    /// </summary>
    /// <param name="program">The shell's program, environment variables already expanded.</param>
    public static ShellLaunch BuildInteractive(string program, string template, string folder)
    {
        var arguments = SplitArguments(template ?? string.Empty).ToList();
        var commandAt = arguments.FindIndex(HoldsCommand);
        if (commandAt >= 0)
        {
            arguments.RemoveAt(commandAt);
        }
        else
        {
            commandAt = arguments.Count;
        }

        // A cmd switch is one letter (/k, /c); a longer "/…" is a Unix path (/bin/zsh) and stays.
        if (commandAt > 0 && arguments[commandAt - 1] is ['-', ..] or ['/', _])
        {
            arguments.RemoveAt(commandAt - 1);
        }

        var expanded = arguments
            .Select(argument => MacroTemplate.Expand(argument, m => m == FolderMacro ? [folder] : null, raw: true))
            .ToList();
        return new ShellLaunch(program, expanded, IsCmd(program));
    }

    // Read as Build reads it: "%Cx" and "%%C"-like text that is not the macro do not count.
    private static bool HoldsCommand(string argument)
    {
        var found = false;
        MacroTemplate.Expand(argument, m =>
        {
            found |= m == CommandMacro;
            return m == CommandMacro ? [] : null;
        }, raw: true);
        return found;
    }

    /// <summary>The arguments as the shell reads them when it is started directly.</summary>
    public static string Arguments(ShellLaunch launch) =>
        string.Join(' ', launch.Arguments.Select(a => launch.ReadsRawLine ? RawJoined(a) : ShellQuoting.QuoteIfNeeded(a)));

    /// <summary>
    /// The arguments of <c>wt.exe</c> that start <paramref name="launch"/> in a new tab in
    /// <paramref name="folder"/>, so that the shell reads exactly what <see cref="Arguments"/> gives it.
    /// </summary>
    /// <remarks>
    /// TRAP-88. Windows Terminal does not pass its command line on: it splits it into argv, cuts it into
    /// commands at every <c>;</c> that has no <c>\</c> in front, removes that <c>\</c>, and joins each
    /// argument back on its own terms — in quotes when it holds a space, inner quotes untouched
    /// (<c>AppCommandlineArgs</c>, <c>Commandline::AddArg</c>). So every argument is first written the
    /// way that join has to put it out (for cmd: as is, since a quoted pair is all cmd wants; for an argv
    /// shell: escaped, or already quoted when the join would not quote it), its semicolons are escaped,
    /// and only then is it quoted for wt itself. After the program, wt takes everything as the command,
    /// even arguments that look like its own options (<c>-NoExit</c>, <c>--login</c>).
    /// </remarks>
    public static string ForWindowsTerminal(ShellLaunch launch, string folder, string? profile = null)
    {
        var sb = new StringBuilder(ForWindowsTerminalProfile(profile, folder));
        foreach (var value in launch.Arguments.Prepend(launch.Program))
        {
            var joined = launch.ReadsRawLine
                ? value.Length == 0 ? "\"\"" : value
                : value.Contains(' ') ? ShellQuoting.EscapeInsideQuotes(value) : ShellQuoting.QuoteIfNeeded(value);
            sb.Append(' ').Append(ShellQuoting.QuoteArgument(joined.Replace(";", "\\;")));
        }

        return sb.ToString();
    }

    /// <summary>
    /// The arguments of <c>wt.exe</c> that open <paramref name="profile"/> (a guid or a name; null — the
    /// default profile) in <paramref name="folder"/> as the profile itself starts. Followed by a command
    /// line, the tab runs that instead, in the profile's looks (<see cref="ForWindowsTerminal"/>).
    /// </summary>
    public static string ForWindowsTerminalProfile(string? profile, string folder)
    {
        // A ';' in a name or a folder would cut the line into two tabs (TRAP-88).
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(profile))
        {
            sb.Append("-p ").Append(ShellQuoting.QuoteArgument(profile.Replace(";", "\\;"))).Append(' ');
        }

        return sb.Append("-d ").Append(ShellQuoting.QuoteArgument(folder.Replace(";", "\\;"))).ToString();
    }

    /// <summary>
    /// Splits <paramref name="text"/> into arguments as <c>CommandLineToArgvW</c> does after the program
    /// name: blanks separate outside quotes; 2n backslashes before a quote are n and the quote opens or
    /// closes, 2n+1 are n and a literal quote; other backslashes are literal; <c>""</c> inside quotes is
    /// a literal quote.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var started = false;
        var quoted = false;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (!quoted && (c is ' ' or '\t'))
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }

                i++;
                continue;
            }

            started = true;
            if (c == '\\')
            {
                var run = 0;
                while (i < text.Length && text[i] == '\\')
                {
                    run++;
                    i++;
                }

                if (i < text.Length && text[i] == '"')
                {
                    current.Append('\\', run / 2);
                    if (run % 2 == 1)
                    {
                        current.Append('"');
                        i++;
                    }
                }
                else
                {
                    current.Append('\\', run);
                }

                continue;
            }

            if (c == '"')
            {
                if (quoted && i + 1 < text.Length && text[i + 1] == '"')
                {
                    current.Append('"');
                    i += 2;
                    continue;
                }

                quoted = !quoted;
                i++;
                continue;
            }

            current.Append(c);
            i++;
        }

        if (started)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }

    /// <summary>True for <c>cmd</c> and <c>cmd.exe</c>, wherever it lives, and for a <c>.cmd</c>/<c>.bat</c>
    /// script used as a shell: cmd parses its arguments too (TRAP-41).</summary>
    public static bool IsCmd(string program)
    {
        var name = Path.GetFileName(program.Trim().Trim('"'));
        return name.Equals("cmd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)
            || ShellQuoting.IsCmdScript(name);
    }

    // cmd's own line: a value with a blank goes in one pair of quotes, nothing inside is escaped.
    private static string RawJoined(string value) =>
        value.Length == 0 ? "\"\"" : value.Contains(' ') ? "\"" + value + "\"" : value;
}

/// <summary>A shell process to start: the program, its arguments as values, and how it reads them.</summary>
/// <param name="ReadsRawLine">cmd.exe: it reads the line after its switches as written, not as argv.</param>
public sealed record ShellLaunch(string Program, IReadOnlyList<string> Arguments, bool ReadsRawLine);
