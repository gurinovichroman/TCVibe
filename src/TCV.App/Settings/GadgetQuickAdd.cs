using TCV.App.Navigation;

namespace TCV.App.Settings;

/// <summary>
/// Reads the one-line gadget description Ctrl+F2 asks for when there are none yet:
/// <c>name = command args</c>. Pure: the existence check is injected, so the split is testable
/// without the disk (the caller runs it off the UI thread, INV-06).
/// </summary>
/// <remarks>
/// The command is split off as it is written, never by re-joining tokens: the stored
/// <see cref="Gadget.Command"/> is the bare program and <see cref="Gadget.Arguments"/> the template
/// as typed, and the launch quotes them itself (<c>ShellQuoting</c>/<c>MacroTemplate</c>, TRAP-41).
/// Cutting at the first space turned <c>C:\Program Files\app.exe</c> into the command
/// <c>C:\Program</c> with the argument <c>Files\app.exe</c>.
/// </remarks>
public static class GadgetQuickAdd
{
    /// <summary>
    /// Parses <paramref name="line"/>. Returns null when it is not <c>name = command [args]</c>
    /// with a non-empty name and command.
    /// </summary>
    /// <param name="fileExists">Whether a file exists; asked only about prefixes of an unquoted
    /// command that look like a path.</param>
    /// <param name="extensions">What a path may omit (<c>PATHEXT</c>): <c>C:\Program Files\nodejs\npm</c>
    /// is <c>npm.cmd</c>. Null uses <see cref="CommandResolver.DefaultExtensions"/>.</param>
    public static Gadget? Parse(string? line, Func<string, bool> fileExists, IEnumerable<string>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            return null;
        }

        var name = line[..separator].Trim();
        var (command, arguments) = SplitCommand(line[(separator + 1)..], fileExists, extensions);
        if (name.Length == 0 || command.Length == 0)
        {
            return null;
        }

        return new Gadget
        {
            Name = name,
            Command = command,
            Arguments = arguments,
            InTerminal = true,
        };
    }

    /// <summary>
    /// Splits a command line into the program and the rest. A quoted program is taken up to its
    /// closing quote. An unquoted one that names a path is the longest prefix ending before a space
    /// that exists as a file (so a pasted <c>C:\Program Files\…\app.exe</c> needs no quotes);
    /// anything else ends at the first space.
    /// </summary>
    public static (string Command, string Arguments) SplitCommand(
        string? commandLine, Func<string, bool> fileExists, IEnumerable<string>? extensions = null)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        var text = (commandLine ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return (string.Empty, string.Empty);
        }

        if (text[0] == '"')
        {
            var close = text.IndexOf('"', 1);
            return close < 0
                ? (text[1..].Trim(), string.Empty)   // no closing quote: the whole rest is the program
                : (text[1..close].Trim(), text[(close + 1)..].Trim());
        }

        var firstSpace = text.IndexOf(' ');
        if (firstSpace < 0)
        {
            return (text, string.Empty);
        }

        // Only a path can contain a space; a bare name (dotnet, npm) is resolved through PATH and
        // ends at the first one. Longest first: "C:\Program Files\app.exe" must win over a shorter
        // prefix that happens to exist as well.
        if (LooksLikePath(text[..firstSpace]))
        {
            var exts = (extensions ?? CommandResolver.DefaultExtensions).ToList();
            for (var end = text.Length; end > firstSpace; end = text.LastIndexOf(' ', end - 1))
            {
                var candidate = text[..end].TrimEnd();
                if (CommandResolver.Resolve(candidate, [], exts, fileExists) is not null)
                {
                    return (candidate, text[end..].Trim());
                }
            }
        }

        return (text[..firstSpace], text[(firstSpace + 1)..].Trim());
    }

    private static bool LooksLikePath(string firstToken) =>
        firstToken.Contains('\\', StringComparison.Ordinal)
        || firstToken.Contains('/', StringComparison.Ordinal)
        || (firstToken.Length >= 2 && firstToken[1] == ':');
}
