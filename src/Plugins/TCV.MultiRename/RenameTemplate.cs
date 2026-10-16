using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TCV.MultiRename;

/// <summary>
/// Parses and evaluates rename templates.
///
/// Supported placeholders:
///   [N]                     – file name without extension
///   [E]                     – extension (without leading dot); a dot written just before it
///                             (<c>.[E]</c>) disappears with it for a file that has no extension
///   [C]                     – counter (the dialog's start and step)
///   [C{start}+{step}]       – counter with its own start and step, e.g. [C10+2]
///   [C:{width}]             – zero-padded counter, e.g. [C:3] gives 001, 002, …
///   [C{start}+{step}:{width}] – all three, e.g. [C10+5:4]
///   [D]                     – date in yyyy-MM-dd format
///   [D:{format}]            – date with custom format, e.g. [D:dd.MM.yyyy]
///   [T]                     – time in HH-mm-ss format
///   [P]                     – parent folder name
/// A folder has no extension: [N] is its whole name and [E] is empty.
/// </summary>
public sealed class RenameTemplate
{
    private static readonly Regex _placeholderRegex = new(
        @"\.\[E\]|\[N\]|\[E\]|\[C(?:(\d+)(?:\+(\d+))?)?(?::(\d+))?\]|\[D(?::([^\]]+))?\]|\[T\]|\[P\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _template;

    public RenameTemplate(string template)
    {
        _template = template ?? throw new ArgumentNullException(nameof(template));
    }

    /// <summary>The raw template string.</summary>
    public string Template => _template;

    /// <summary>
    /// Evaluates the template for the given context, producing a new file name.
    /// If the template is empty, the original name is returned unchanged.
    /// </summary>
    public string Evaluate(RenameContext context)
    {
        if (string.IsNullOrEmpty(_template))
        {
            return context.Entry.Name;
        }

        var entryName = context.Entry.Name;
        var (nameWithoutExt, extension) = context.Entry.IsDirectory
            ? (entryName, string.Empty)
            : FileNameParts.Split(entryName);

        // Extension without the leading dot
        var extNoDot = extension.Length > 0 ? extension[1..] : string.Empty;

        // Parent folder name
        var parentPath = context.Entry.Path.Parent;
        var parentName = parentPath is not null
            ? Path.GetFileName(parentPath.Value.TrimEnd('/'))
            : string.Empty;

        return _placeholderRegex.Replace(_template, match =>
        {
            var token = match.Value.ToUpperInvariant();

            if (token == ".[E]")
            {
                return extNoDot.Length > 0 ? "." + extNoDot : string.Empty;
            }

            if (token == "[N]")
            {
                return nameWithoutExt;
            }

            if (token == "[E]")
            {
                return extNoDot;
            }

            if (token.StartsWith("[C", StringComparison.Ordinal))
            {
                return FormatCounter(match, context);
            }

            if (token.StartsWith("[D", StringComparison.Ordinal))
            {
                if (match.Groups[4].Success)
                {
                    // [D:{format}]
                    try
                    {
                        return context.ReferenceDate.ToString(match.Groups[4].Value, CultureInfo.CurrentCulture);
                    }
                    catch (FormatException)
                    {
                        return context.ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    }
                }

                return context.ReferenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            if (token == "[T]")
            {
                return context.ReferenceDate.ToString("HH-mm-ss", CultureInfo.InvariantCulture);
            }

            if (token == "[P]")
            {
                return parentName;
            }

            return match.Value;
        });
    }

    private static string FormatCounter(Match match, RenameContext context)
    {
        long value;
        if (match.Groups[1].Success || match.Groups[2].Success)
        {
            // The placeholder names its own start and step: it counts positions in the batch itself.
            var start = match.Groups[1].Success && long.TryParse(match.Groups[1].Value, out var s) ? s : 1;
            var step = match.Groups[2].Success && long.TryParse(match.Groups[2].Value, out var t) ? t : 1;
            value = start + context.Index * step;
        }
        else
        {
            value = context.Counter;
        }

        var width = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var w)
            ? w
            : context.CounterWidth;

        var digits = value.ToString(CultureInfo.InvariantCulture);
        return width > digits.Length ? digits.PadLeft(width, '0') : digits;
    }

    public override string ToString() => _template;
}
