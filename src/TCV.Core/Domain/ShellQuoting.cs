using System.Text;

namespace TCV.Core.Domain;

/// <summary>
/// The one place that decides how a value becomes part of a Windows command line. Everything that
/// builds arguments for an external program (editors, agent CLIs, user tools, gadgets, the terminal)
/// goes through here, because the rules are subtle and the cost of a mistake is code execution:
/// a file name like <c>a&amp;calc.txt</c> is legal on NTFS, and <c>code.cmd</c>, <c>cursor.cmd</c> and
/// <c>claude.cmd</c> are batch shims, so their arguments are parsed by <c>cmd.exe</c>.
/// </summary>
/// <remarks>
/// Pure and BCL-only (INV-01, ADR-0016: the app and the Tools plugin both use it). Two layers are
/// handled separately. <see cref="QuoteArgument"/> and <see cref="EscapeInsideQuotes"/> implement the
/// <c>CommandLineToArgvW</c> rules (backslashes before a quote, a trailing backslash on a drive
/// root). <see cref="IsSafeForCmd"/> is the gate for values that will meet <c>cmd.exe</c>: inside
/// double quotes <c>cmd</c> still expands <c>%VAR%</c> and, with delayed expansion, <c>!VAR!</c>,
/// and neither can be escaped reliably, so a value that contains them is refused instead of
/// guessed at.
/// </remarks>
public static class ShellQuoting
{
    /// <summary>Characters that force quoting: separators for the argv parser plus cmd.exe operators.</summary>
    private static readonly char[] NeedsQuoting =
        [' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', '%', '!', ',', ';', '=', '\''];

    /// <summary>Always wraps <paramref name="value"/> in quotes, escaping it for the argv parser.</summary>
    public static string QuoteArgument(string value) => "\"" + EscapeInsideQuotes(value) + "\"";

    /// <summary>Quotes only when the value has a character that would otherwise split or be interpreted.</summary>
    public static string QuoteIfNeeded(string value) =>
        value.Length > 0 && value.IndexOfAny(NeedsQuoting) < 0 ? value : QuoteArgument(value);

    /// <summary>
    /// Escapes a value for a spot that is already inside a double-quoted region: each quote is
    /// preceded by a backslash, the backslashes in front of it are doubled, and so is a trailing
    /// run — otherwise <c>"C:\"</c> would escape its own closing quote.
    /// </summary>
    public static string EscapeInsideQuotes(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        var backslashes = 0;
        foreach (var c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
            }
            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2);
        return sb.ToString();
    }

    /// <summary>
    /// True when the value can be handed to <c>cmd.exe</c> (a <c>.cmd</c>/<c>.bat</c> shim, or
    /// <c>cmd /k</c>) inside quotes without being expanded or terminated early. Refuses <c>%</c>,
    /// <c>!</c>, quotes and control characters.
    /// </summary>
    public static bool IsSafeForCmd(string value)
    {
        foreach (var c in value)
        {
            if (c is '%' or '!' or '"' || c < ' ')
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>True for the extensions that make Windows start <c>cmd.exe</c> for the file.</summary>
    public static bool IsCmdScript(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path.Trim().Trim('"'));
        return ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when launching <paramref name="command"/> may end up in <c>cmd.exe</c>: a batch file, a
    /// bare name (<c>code</c>) that PATH/PATHEXT resolves to a <c>.cmd</c> shim, or <c>cmd.exe</c>
    /// itself (<c>cmd.exe /k type %S</c>), however it is spelled or wherever it lives.
    /// </summary>
    public static bool MayRunThroughCmd(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        var trimmed = command.Trim().Trim('"');
        var extension = Path.GetExtension(trimmed);
        var isCmdExe = Path.GetFileNameWithoutExtension(trimmed).Equals("cmd", StringComparison.OrdinalIgnoreCase)
                       && (extension.Length == 0 || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase));
        return isCmdExe || IsCmdScript(trimmed) || extension.Length == 0;
    }
}

/// <summary>A macro value was refused because it cannot be passed safely to <c>cmd.exe</c>.</summary>
public sealed class UnsafeMacroValueException(string value)
    : InvalidOperationException($"The value cannot be passed safely to a batch file: {value}")
{
    /// <summary>The offending value (a path or a name).</summary>
    public string Value { get; } = value;
}

/// <summary>
/// Single-pass <c>%X</c> template expansion shared by user tools, gadgets and the editor/agent
/// launcher. Single pass matters: expanding macro by macro lets a value that happens to contain
/// <c>%S</c> (a file called <c>a%Sb.txt</c>) be expanded again by the next replacement.
/// </summary>
public static class MacroTemplate
{
    /// <summary>
    /// Expands <paramref name="template"/>. <paramref name="resolve"/> maps a macro letter to its
    /// values, or null when the letter is not a macro (the text is then kept as written). An empty
    /// list expands to nothing. Values are quoted for the argv parser according to whether the
    /// macro sits inside a quoted region of the template; <paramref name="raw"/> skips quoting
    /// (for a program path or a working directory).
    /// </summary>
    /// <param name="eatSpaceBeforeEmpty">An empty macro also removes the blanks in front of it,
    /// so <c>"%P" %F</c> with no selection does not end in a stray space.</param>
    /// <param name="validate">Called for every non-empty value about to be inserted; returning false
    /// aborts with <see cref="UnsafeMacroValueException"/>.</param>
    public static string Expand(
        string template,
        Func<char, IReadOnlyList<string>?> resolve,
        bool raw = false,
        bool eatSpaceBeforeEmpty = false,
        Func<string, bool>? validate = null)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template ?? string.Empty;
        }

        var sb = new StringBuilder(template.Length + 32);
        var inQuotes = false;

        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];

            if (c == '"' && !(i > 0 && template[i - 1] == '\\'))
            {
                inQuotes = !inQuotes;
            }

            if (c != '%' || i + 1 >= template.Length)
            {
                sb.Append(c);
                continue;
            }

            // An environment variable (%PATH%, %USERPROFILE%) is the shell's business, not a macro
            // followed by text: kept verbatim, closing '%' included, so "%PATH%" does not turn into
            // the panel path followed by "ATH%", nor its closing '%' into the start of the next macro.
            if (TryReadVariable(template, i, out var end))
            {
                sb.Append(template, i, end - i + 1);
                i = end;
                continue;
            }

            // A macro is one letter standing alone: "%Pfoo" is not %P followed by "foo".
            var values = i + 2 < template.Length && IsWordChar(template[i + 2]) ? null : resolve(template[i + 1]);
            if (values is null)
            {
                sb.Append(c);
                continue;
            }

            i++;   // consume the macro letter

            if (values.Count == 0)
            {
                if (eatSpaceBeforeEmpty && !inQuotes)
                {
                    while (sb.Length > 0 && sb[^1] is ' ' or '\t')
                    {
                        sb.Length--;
                    }
                }
                continue;
            }

            for (var v = 0; v < values.Count; v++)
            {
                var value = values[v];
                if (validate is not null && value.Length > 0 && !validate(value))
                {
                    throw new UnsafeMacroValueException(value);
                }

                if (v > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(raw ? value : inQuotes ? ShellQuoting.EscapeInsideQuotes(value) : ShellQuoting.QuoteIfNeeded(value));
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// True when <paramref name="template"/> holds a <c>%NAME%</c> reference at <paramref name="start"/>
    /// with a name of at least two characters; <paramref name="end"/> is the closing <c>%</c>.
    /// </summary>
    private static bool TryReadVariable(string template, int start, out int end)
    {
        end = -1;
        var j = start + 1;
        while (j < template.Length && (IsWordChar(template[j]) || template[j] is '(' or ')'))
        {
            j++;
        }

        if (j - start - 1 < 2 || j >= template.Length || template[j] != '%')
        {
            return false;
        }

        end = j;
        return true;
    }

    private static bool IsWordChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';
}
