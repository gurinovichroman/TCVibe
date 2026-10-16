using System.Text;
using System.Text.RegularExpressions;
using TCV.Core.Domain;

namespace TCV.Search;

/// <summary>Defines what to search for.</summary>
/// <remarks>
/// Call <see cref="Compile"/> before matching; <see cref="SearchEngine"/> does it for you. Every
/// "how to walk" knob lives here too, so a preset is just a serialised copy of this object.
/// </remarks>
public sealed class SearchCriteria
{
    // ── Name ──────────────────────────────────────────────────────────────────

    /// <summary>File name pattern. Supports wildcards * and ?, several masks separated by
    /// <c>;</c>, or plain substring; a regular expression when <see cref="NameIsRegex"/>.</summary>
    public string? NamePattern { get; set; }

    /// <summary>When true, NamePattern is treated as a regular expression.</summary>
    public bool NameIsRegex { get; set; }

    /// <summary>Case-sensitive name and content matching.</summary>
    public bool CaseSensitive { get; set; }

    // ── Size ──────────────────────────────────────────────────────────────────

    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }

    // ── Date ──────────────────────────────────────────────────────────────────

    public DateTime? ModifiedAfter { get; set; }
    public DateTime? ModifiedBefore { get; set; }

    // ── Attributes ────────────────────────────────────────────────────────────

    public bool IncludeHidden { get; set; } = false;
    public bool IncludeSystem { get; set; } = false;
    public bool DirectoriesOnly { get; set; } = false;
    public bool FilesOnly { get; set; } = false;

    // ── Content ───────────────────────────────────────────────────────────────

    /// <summary>Search inside file contents for this text.</summary>
    public string? ContentText { get; set; }

    /// <summary>When true, ContentText is a regular expression.</summary>
    public bool ContentIsRegex { get; set; }

    /// <summary>Match only whole words — the ripgrep <c>-w</c> switch.</summary>
    public bool WholeWord { get; set; }

    /// <summary>
    /// Code page used to decode file contents; 0 auto-detects per file (BOM, then UTF-16 shape,
    /// then strict UTF-8, then Latin-1). Legacy pages need the <c>CodePages</c> provider, which
    /// this assembly registers on first use.
    /// </summary>
    public int ContentCodePage { get; set; }

    /// <summary>Skip files whose first bytes look binary. On by default, as in ripgrep.</summary>
    public bool SkipBinary { get; set; } = true;

    /// <summary>Lines of context kept on each side of a hit (ripgrep <c>-C</c>). 0 keeps none.</summary>
    public int ContextLines { get; set; }

    /// <summary>Stop after this many hits in one file; 0 means every hit.</summary>
    public int MaxMatchesPerFile { get; set; }

    /// <summary>
    /// Files larger than this are not opened for a content search (they are still name-matched).
    /// Guards against dragging a multi-gigabyte artefact through memory.
    /// </summary>
    public long MaxContentBytes { get; set; } = 16L * 1024 * 1024;

    /// <summary>
    /// Budget for one regex match on one line. A pattern such as <c>(a+)+$</c> can take exponential time
    /// on a hostile line; past this budget the file is skipped (and counted) instead of hanging the search.
    /// </summary>
    public static readonly TimeSpan ContentMatchTimeout = TimeSpan.FromSeconds(2);

    // ── Scope ─────────────────────────────────────────────────────────────────

    /// <summary>Maximum depth from the starting path (0 = unlimited).</summary>
    public int MaxDepth { get; set; } = 0;

    /// <summary>Honour <c>.gitignore</c> files found during the walk — the dev-tool default.</summary>
    public bool RespectGitignore { get; set; } = true;

    /// <summary>
    /// Extra exclusions in gitignore grammar, one per line or separated by <c>;</c>:
    /// <c>node_modules/</c>, <c>*.min.js</c>, <c>!keep.min.js</c>. Applied to the path relative
    /// to the search root, so <c>src/**/obj/</c> works.
    /// </summary>
    public string? ExcludePatterns { get; set; }

    /// <summary>
    /// Directories never entered, matched by name at any depth. VCS metadata holds tens of
    /// thousands of files nobody greps and is not listed in <c>.gitignore</c>.
    /// </summary>
    public IReadOnlyCollection<string> SkipDirectories { get; set; } = [".git", ".hg", ".svn"];

    /// <summary>
    /// Do not descend into junctions and symlinks. Off for the user's own searches; the agent bridge
    /// turns it on so a link inside an approved directory cannot carry a search out of it.
    /// </summary>
    public bool SkipReparsePoints { get; set; }

    /// <summary>Hard cap on results; 0 means unlimited. The walk stops once it is reached.</summary>
    public int MaxResults { get; set; }

    // ── Compiled matchers (built once) ────────────────────────────────────────

    public Regex? CompiledNameRegex { get; private set; }
    public Regex? CompiledContentRegex { get; private set; }

    /// <summary>Name masks when <see cref="NameIsRegex"/> is off; empty matches everything.</summary>
    public FileMaskSet NameMasks { get; private set; } = FileMaskSet.Empty;

    /// <summary>Compiled <see cref="ExcludePatterns"/>, or <see cref="GitignoreRules.Empty"/>.</summary>
    public GitignoreRules ExcludeRules { get; private set; } = GitignoreRules.Empty;

    /// <summary>Encoding forced by <see cref="ContentCodePage"/>, or null to auto-detect.</summary>
    public Encoding? ForcedEncoding { get; private set; }

    public void Compile()
    {
        var opts = CaseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase;

        CompiledNameRegex = null;
        NameMasks = FileMaskSet.Empty;
        if (!string.IsNullOrEmpty(NamePattern))
        {
            if (NameIsRegex)
                CompiledNameRegex = new Regex(NamePattern, opts | RegexOptions.Compiled);
            else
                NameMasks = FileMaskSet.Parse(NamePattern, CaseSensitive, bareNameIsSubstring: true);
        }

        CompiledContentRegex = null;
        if (!string.IsNullOrEmpty(ContentText))
        {
            var pattern = ContentIsRegex ? ContentText : Regex.Escape(ContentText);
            // \b around a pattern starting or ending with a non-word character never matches, so
            // the guard is only applied where a word boundary can actually exist.
            if (WholeWord) pattern = WrapWholeWord(pattern);
            CompiledContentRegex = new Regex(pattern, opts | RegexOptions.Compiled | RegexOptions.Multiline, ContentMatchTimeout);
        }

        ExcludeRules = string.IsNullOrWhiteSpace(ExcludePatterns)
            ? GitignoreRules.Empty
            : GitignoreRules.Parse(ExcludePatterns.Replace(';', '\n'));

        ForcedEncoding = ContentCodePage > 0 ? SearchEncodings.Get(ContentCodePage) : null;
    }

    /// <summary>True when the entry name passes the name filter (regex or masks).</summary>
    public bool NameMatches(string name) =>
        CompiledNameRegex is not null ? CompiledNameRegex.IsMatch(name) : NameMasks.Matches(name);

    private static string WrapWholeWord(string pattern)
    {
        var prefix = StartsWithWordChar(pattern) ? @"\b" : string.Empty;
        var suffix = EndsWithWordChar(pattern) ? @"\b" : string.Empty;
        return prefix.Length == 0 && suffix.Length == 0 ? pattern : prefix + "(?:" + pattern + ")" + suffix;
    }

    private static bool StartsWithWordChar(string p) => p.Length > 0 && IsWordish(p[0]);

    private static bool EndsWithWordChar(string p) => p.Length > 0 && IsWordish(p[^1]);

    private static bool IsWordish(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '\\';
}

/// <summary>Resolves code pages, registering the legacy provider once.</summary>
internal static class SearchEncodings
{
    static SearchEncodings() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>The encoding for a code page, or null when the platform does not know it.</summary>
    public static Encoding? Get(int codePage)
    {
        try { return Encoding.GetEncoding(codePage); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}
