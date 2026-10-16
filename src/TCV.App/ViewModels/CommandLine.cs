using System.IO;

namespace TCV.App.ViewModels;

/// <summary>
/// The parts of the bottom command line that are not «run it in a terminal»: <c>cd</c> moves the panel
/// instead of a terminal that would close at once, as the command line of Total Commander does.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// <c>cd path</c>, <c>cd /d path</c>, <c>chdir path</c> or a bare drive (<c>D:</c>) → the path. A
    /// plain <c>cd</c> (which prints the folder in cmd) is not a move.
    /// </summary>
    public static bool TryParseChangeDirectory(string text, out string target)
    {
        target = string.Empty;
        var line = text.Trim();

        if (line.Length == 2 && char.IsAsciiLetter(line[0]) && line[1] == ':')
        {
            target = line + "\\";
            return true;
        }

        string rest;
        if (StartsWithWord(line, "cd"))
        {
            rest = line[2..];
        }
        else if (StartsWithWord(line, "chdir"))
        {
            rest = line[5..];
        }
        else
        {
            return false;
        }

        rest = rest.Trim();
        if (rest.StartsWith("/d", StringComparison.OrdinalIgnoreCase) && (rest.Length == 2 || char.IsWhiteSpace(rest[2])))
        {
            rest = rest[2..].Trim();
        }

        // "cd src && npm test" is a command line for cmd, not a move: & | < > outside quotes chain or
        // redirect, and so does anything after a closing quote.
        if (rest.StartsWith('"'))
        {
            var close = rest.IndexOf('"', 1);
            if (close > 0 && rest[(close + 1)..].Trim().Length > 0)
            {
                return false;
            }
        }
        else if (rest.IndexOfAny(['&', '|', '<', '>']) >= 0)
        {
            return false;
        }

        rest = rest.Trim('"').Trim();
        if (rest.Length == 0)
        {
            return false;
        }

        target = rest;
        return true;
    }

    /// <summary>The typed path against the panel's folder: absolute stays, relative (<c>..</c>, <c>src</c>) is joined.</summary>
    public static string ResolveAgainst(string? folder, string target)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return target;
        }

        // cmd reads "/" as "\": without this "cd /" would resolve against the process's drive.
        target = target.Replace('/', '\\');

        // "\x" is rooted on the current drive: the panel's, not the process's.
        if (target.StartsWith('\\') && !target.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (Path.GetPathRoot(folder) ?? string.Empty).TrimEnd('\\') + target;
        }

        if (Path.IsPathFullyQualified(target))
        {
            return target;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(folder, target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return target;
        }
    }

    // "cd.." is a move too, as cmd reads it.
    private static bool StartsWithWord(string line, string word) =>
        line.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (line.Length == word.Length || char.IsWhiteSpace(line[word.Length]) || line[word.Length] is '.' or '\\' or '/');
}
