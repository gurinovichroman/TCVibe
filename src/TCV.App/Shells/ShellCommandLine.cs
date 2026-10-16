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
    public static string ForWindowsTerminal(ShellLaunch launch, string folder)
    {
        var sb = new StringBuilder("-d ").Append(ShellQuoting.QuoteArgument(folder));
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
