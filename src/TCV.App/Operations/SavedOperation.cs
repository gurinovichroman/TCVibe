using System.Text;

namespace TCV.App.Operations;

/// <summary>
/// A parameterised, replayable file operation (6.22): "copy <c>*.log</c> from here into
/// <c>D:\archive\%Y-%M-%D</c>", saved by name and re-runnable against whatever the panels show.
/// </summary>
/// <remarks>
/// Pure data plus macro expansion, deliberately: the execution goes through the ordinary
/// <see cref="OperationQueue"/> so a replayed operation is logged and undone exactly like a
/// hand-driven one (INV-10). Everything that can differ between runs is a macro rather than a
/// stored absolute path — that is what makes a saved operation reusable instead of a bookmark.
/// </remarks>
public sealed class SavedOperation
{
    /// <summary>Shown in the picker; also the identity used for upsert and delete.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What to do. Only the kinds the queue can replay are offered.</summary>
    public SavedOperationKind Kind { get; set; } = SavedOperationKind.Copy;

    /// <summary>
    /// Mask selecting the source entries in the active panel (<c>*.log;*.tmp</c>). Empty means
    /// "whatever is selected right now", which is how a saved operation replays over a fresh choice.
    /// </summary>
    public string SourceMask { get; set; } = string.Empty;

    /// <summary>Destination, macros allowed. Empty means the opposite panel's directory.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Create the destination directory when it does not exist.</summary>
    public bool CreateTarget { get; set; } = true;

    /// <summary>Ask before running. On by default for anything destructive (INV-11).</summary>
    public bool Confirm { get; set; } = true;

    /// <summary>When this operation last ran, for ordering the picker most-recent-first.</summary>
    public DateTime LastUsedUtc { get; set; }

    /// <summary>True for kinds that destroy data and must always be confirmed.</summary>
    public bool IsDestructive => Kind is SavedOperationKind.Move or SavedOperationKind.Delete;

    /// <summary>A detached copy — what the manager edits, so Cancel leaves the stored one alone.</summary>
    public SavedOperation Clone() => (SavedOperation)MemberwiseClone();

    /// <summary>
    /// True when both describe the same operation under the same name (letter case included, so a
    /// rename that only changes case is still written). <see cref="LastUsedUtc"/> is not part of the
    /// definition: running an operation does not make it "edited".
    /// </summary>
    public bool DefinesSameAs(SavedOperation other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Kind == other.Kind
        && string.Equals(SourceMask, other.SourceMask, StringComparison.Ordinal)
        && string.Equals(Target, other.Target, StringComparison.Ordinal)
        && CreateTarget == other.CreateTarget
        && Confirm == other.Confirm;

    /// <summary>
    /// Turns a line of the operation log into a replayable block (6.22) — "do that again", where
    /// "that" is a copy or move that already happened once.
    /// </summary>
    /// <remarks>
    /// The log records a summary, not a file list: a single path, or a directory plus a count
    /// (see <see cref="OperationLogDescriptor.DescribeEntries"/>). So the replay is derived rather
    /// than reconstructed — a single file becomes a mask matching its extension, a batch becomes
    /// "whatever is selected". That is the useful reading anyway: a block that only ever moved the
    /// same five files would be a recording, not a reusable operation.
    /// <para>Returns null for a delete: replaying one from a summary would guess at which files
    /// it meant, and guessing wrong destroys data (INV-11).</para>
    /// </remarks>
    public static SavedOperation? FromLogEntry(
        OperationKind kind, string source, string? destination, string name)
    {
        var savedKind = kind switch
        {
            OperationKind.Copy => SavedOperationKind.Copy,
            OperationKind.Move => SavedOperationKind.Move,
            OperationKind.CreateFolder => SavedOperationKind.CreateDirectory,
            _ => (SavedOperationKind?)null,
        };
        if (savedKind is null) return null;

        // A new folder is logged as its own path with no destination. Replayed as "%O" it "created"
        // the opposite panel's folder, which always exists, and did nothing; its name, relative,
        // means "a folder of this name where I am" — what F7 did the first time.
        var target = string.IsNullOrWhiteSpace(destination) ? "%O" : destination!;
        if (savedKind == SavedOperationKind.CreateDirectory)
        {
            var leaf = LeafName(source);
            if (leaf.Length == 0)
            {
                return null;
            }

            target = leaf;
        }

        return new SavedOperation
        {
            Name = name,
            Kind = savedKind.Value,
            SourceMask = savedKind == SavedOperationKind.CreateDirectory ? string.Empty : MaskFromLoggedSource(source),
            Target = target,
            Confirm = true,
        };
    }

    private static string LeafName(string? path)
    {
        var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>
    /// A mask from a logged source: the extension of a single named file, or nothing (meaning
    /// "the current selection") when the log recorded a directory and a count.
    /// </summary>
    public static string MaskFromLoggedSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        // "D:/dir (12)" — a batch, so there is no single name to derive a mask from.
        if (source.EndsWith(')') && source.LastIndexOf('(') > 0) return string.Empty;

        var slash = source.LastIndexOfAny(['\\', '/']);
        var name = slash < 0 ? source : source[(slash + 1)..];
        var dot = name.LastIndexOf('.');
        return dot > 0 ? "*" + name[dot..] : string.Empty;
    }
}

/// <summary>Operations a saved entry can replay.</summary>
public enum SavedOperationKind
{
    Copy,
    Move,
    Delete,
    /// <summary>Create the expanded <see cref="SavedOperation.Target"/> as a directory.</summary>
    CreateDirectory,
}

/// <summary>
/// Expands the macros a saved operation's target may contain. Pure and total: an unknown macro is
/// left as written rather than swallowed, so a typo is visible in the preview instead of silently
/// producing the wrong directory.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>%P</c> — active panel directory</item>
/// <item><c>%O</c> — opposite panel directory</item>
/// <item><c>%N</c> — name of the focused entry, without extension</item>
/// <item><c>%E</c> — extension of the focused entry, without the dot</item>
/// <item><c>%Y</c> <c>%M</c> <c>%D</c> — year, month, day</item>
/// <item><c>%H</c> <c>%m</c> — hour and minute</item>
/// <item><c>%%</c> — a literal per cent sign</item>
/// </list>
/// </remarks>
public static class OperationMacros
{
    /// <summary>Values a macro expansion draws on.</summary>
    /// <param name="ActivePath">Directory of the active panel.</param>
    /// <param name="OppositePath">Directory of the other panel.</param>
    /// <param name="FocusedName">Focused entry's name without extension, or empty.</param>
    /// <param name="FocusedExtension">Focused entry's extension without the dot, or empty.</param>
    /// <param name="Now">Timestamp the date macros expand from.</param>
    public readonly record struct Context(
        string ActivePath,
        string OppositePath,
        string FocusedName,
        string FocusedExtension,
        DateTime Now);

    public static string Expand(string? template, Context context)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;

        var sb = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] != '%' || i + 1 >= template.Length)
            {
                sb.Append(template[i]);
                continue;
            }

            var macro = template[i + 1];
            var replacement = macro switch
            {
                'P' => context.ActivePath,
                'O' => context.OppositePath,
                'N' => context.FocusedName,
                'E' => context.FocusedExtension,
                'Y' => context.Now.ToString("yyyy"),
                'M' => context.Now.ToString("MM"),
                'D' => context.Now.ToString("dd"),
                'H' => context.Now.ToString("HH"),
                'm' => context.Now.ToString("mm"),
                '%' => "%",
                _ => null,
            };

            if (replacement is null)
            {
                sb.Append(template[i]);   // unknown macro: leave it visible
                continue;
            }

            sb.Append(replacement);
            i++;
        }

        return sb.ToString();
    }
}
