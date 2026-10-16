using System.IO;

namespace TCV.App.Viewer;

/// <summary>The kind of token a highlighted span carries; the view maps it to a brush.</summary>
public enum TokenKind
{
    /// <summary>Ordinary text — no colour of its own.</summary>
    Plain,
    Keyword,
    /// <summary>A second keyword tier: types, built-ins, HTML/XML tag names.</summary>
    Type,
    String,
    Number,
    Comment,
    /// <summary>Preprocessor lines, attributes, decorators, front matter.</summary>
    Meta,
    /// <summary>Punctuation and operators.</summary>
    Operator,
    /// <summary>A diff line that adds, or a markdown heading.</summary>
    Added,
    /// <summary>A diff line that removes.</summary>
    Removed,
    /// <summary>A diff hunk header.</summary>
    Hunk,
}

/// <summary>
/// A lexical grammar the highlighter can tokenize with: the delimiters and word lists of one
/// language family. Deliberately small — this is a viewer, not a compiler, and a table-driven
/// lexer covers the constructs that make code readable at a glance.
/// </summary>
public sealed record SyntaxLanguage(
    string Name,
    IReadOnlySet<string> Keywords,
    IReadOnlySet<string> Types,
    IReadOnlyList<string> LineComments,
    IReadOnlyList<(string Open, string Close)> BlockComments,
    IReadOnlyList<char> StringDelimiters,
    bool HasCharLiteral = false,
    bool HasPreprocessor = false,
    string? PreprocessorPrefix = null,
    bool CaseSensitive = true)
{
    /// <summary>Nothing is highlighted — plain text, or an extension we do not know.</summary>
    public static SyntaxLanguage None { get; } = new(
        "Text", Empty, Empty, [], [], []);

    private static IReadOnlySet<string> Empty { get; } = new HashSet<string>();

    private static IReadOnlySet<string> Words(string words, bool caseSensitive = true) =>
        new HashSet<string>(
            words.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries),
            caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);

    // ── Grammars ──────────────────────────────────────────────────────────────

    public static SyntaxLanguage CSharp { get; } = new(
        "C#",
        Words("abstract as async await base break case catch checked class const continue default delegate do " +
              "else enum event explicit extern false finally fixed for foreach get goto if implicit in init " +
              "interface internal is lock namespace new null operator out override params private protected " +
              "public readonly record ref return sealed set sizeof stackalloc static struct switch this throw " +
              "true try typeof unchecked unsafe using var virtual volatile when where while yield global with " +
              "and or not required file scoped nameof"),
        Words("bool byte char decimal double dynamic float int long nint nuint object sbyte short string uint " +
              "ulong ushort void Task List Dictionary IEnumerable IReadOnlyList Span ReadOnlySpan"),
        ["//"],
        [("/*", "*/")],
        ['"', '\''],
        HasCharLiteral: true,
        HasPreprocessor: true,
        PreprocessorPrefix: "#");

    public static SyntaxLanguage JavaScript { get; } = new(
        "JavaScript/TypeScript",
        Words("as async await break case catch class const continue debugger default delete do else enum export " +
              "extends false finally for from function get if implements import in instanceof interface let new " +
              "null of package private protected public readonly return set static super switch this throw true " +
              "try type typeof var void while with yield declare namespace satisfies keyof infer"),
        Words("any bigint boolean never number object string symbol undefined unknown Array Promise Record Map Set"),
        ["//"],
        [("/*", "*/")],
        ['"', '\'', '`']);

    public static SyntaxLanguage Python { get; } = new(
        "Python",
        Words("and as assert async await break class continue def del elif else except finally for from global " +
              "if import in is lambda match case nonlocal not or pass raise return try while with yield None " +
              "True False self"),
        Words("bool bytes dict float frozenset int list object set str tuple type"),
        ["#"],
        [("\"\"\"", "\"\"\""), ("'''", "'''")],
        ['"', '\'']);

    public static SyntaxLanguage PowerShell { get; } = new(
        "PowerShell",
        Words("begin break catch class continue data define do dynamicparam else elseif end enum exit filter " +
              "finally for foreach from function hidden if in param process return switch throw trap try until " +
              "using var while", caseSensitive: false),
        Words("true false null", caseSensitive: false),
        ["#"],
        [("<#", "#>")],
        ['"', '\''],
        CaseSensitive: false);

    public static SyntaxLanguage Sql { get; } = new(
        "SQL",
        Words("select from where group by having order insert into values update set delete create table alter " +
              "drop index view join left right inner outer on as and or not null distinct union all limit " +
              "offset primary key foreign references default constraint case when then else end", caseSensitive: false),
        Words("int bigint varchar nvarchar text date datetime timestamp boolean decimal numeric float", caseSensitive: false),
        ["--"],
        [("/*", "*/")],
        ['\'', '"'],
        CaseSensitive: false);

    public static SyntaxLanguage Json { get; } = new(
        "JSON",
        Words("true false null"),
        Empty,
        [],
        [],
        ['"']);

    public static SyntaxLanguage Yaml { get; } = new(
        "YAML",
        Words("true false null yes no on off", caseSensitive: false),
        Empty,
        ["#"],
        [],
        ['"', '\''],
        CaseSensitive: false);

    public static SyntaxLanguage Ini { get; } = new(
        "INI",
        Words("true false", caseSensitive: false),
        Empty,
        [";", "#"],
        [],
        ['"'],
        CaseSensitive: false);

    public static SyntaxLanguage Shell { get; } = new(
        "Shell",
        Words("if then else elif fi for in do done while until case esac function return export local readonly " +
              "shift source alias unset echo exit trap"),
        Empty,
        ["#"],
        [],
        ['"', '\'']);

    /// <summary>
    /// XML, XAML and HTML. Tags are not word-matched but recognised structurally by
    /// <see cref="SyntaxHighlighter"/>, so the word lists stay empty on purpose.
    /// </summary>
    public static SyntaxLanguage Markup { get; } = new(
        "XML/HTML",
        Empty,
        Empty,
        [],
        [("<!--", "-->")],
        ['"', '\''],
        CaseSensitive: false);

    // ── Resolution ────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks the grammar for a file name. Unknown extensions get <see cref="None"/>, which renders
    /// as plain text — the viewer must never refuse to show a file because it cannot colour it.
    /// </summary>
    public static SyntaxLanguage ForFile(string fileName)
    {
        var name = Path.GetFileName(fileName);
        var ext = Path.GetExtension(name).ToLowerInvariant();

        // Extensionless dotfiles a developer opens often enough to be worth naming.
        if (ext.Length == 0 || name.StartsWith('.'))
        {
            var lower = name.ToLowerInvariant();
            if (lower is ".gitignore" or ".gitattributes" or ".dockerignore" or ".editorconfig") return Ini;
            if (lower is ".env" || lower.StartsWith(".env.", StringComparison.Ordinal)) return Ini;
            if (lower is "dockerfile" or "makefile") return Shell;
        }

        return ext switch
        {
            ".cs" or ".csx" => CSharp,
            ".java" or ".kt" or ".kts" or ".scala" or ".go" or ".rs" or ".swift" or
            ".c" or ".h" or ".cpp" or ".hpp" or ".cc" or ".cxx" or ".m" or ".mm" => CSharp,
            ".js" or ".mjs" or ".cjs" or ".jsx" or ".ts" or ".tsx" or ".php" => JavaScript,
            ".py" or ".pyi" or ".pyw" => Python,
            ".ps1" or ".psm1" or ".psd1" => PowerShell,
            ".sql" => Sql,
            ".json" or ".jsonc" or ".json5" or ".tcvproj" => Json,
            ".yml" or ".yaml" => Yaml,
            ".ini" or ".cfg" or ".conf" or ".toml" or ".properties" or ".editorconfig" => Ini,
            ".sh" or ".bash" or ".zsh" => Shell,
            ".xml" or ".xaml" or ".html" or ".htm" or ".xhtml" or ".svg" or ".config" or
            ".csproj" or ".props" or ".targets" or ".slnx" or ".resx" or ".plist" => Markup,
            _ => None,
        };
    }

    /// <summary>True when this file should be shown as rendered markdown rather than as code.</summary>
    public static bool IsMarkdown(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".md" or ".markdown" or ".mdown";

    /// <summary>True when this file should be shown as a coloured unified diff.</summary>
    public static bool IsDiff(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() is ".diff" or ".patch";
}
