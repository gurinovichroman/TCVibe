namespace TCV.Ai.Rename;

/// <summary>Why AI renaming was refused — a stable code the UI turns into a localised sentence.</summary>
public enum JunkCornerReason
{
    None,
    NothingSelected,
    InsideRepository,
    ProjectDirectory,
    SourceFiles,
}

/// <summary>Whether AI renaming is allowed here, and why not when it is not.</summary>
/// <param name="Allowed">True when this looks like a folder of unnamed junk.</param>
/// <param name="Reason">English explanation, for logs and tests; empty when allowed.</param>
/// <param name="Code">Machine-readable cause; the app localises it.</param>
/// <param name="Subject">Directory name for <see cref="JunkCornerReason.ProjectDirectory"/>.</param>
/// <param name="CodeFiles">Count of source files for <see cref="JunkCornerReason.SourceFiles"/>.</param>
/// <param name="Total">Selected files for <see cref="JunkCornerReason.SourceFiles"/>.</param>
public sealed record JunkCornerVerdict(
    bool Allowed,
    string Reason,
    JunkCornerReason Code = JunkCornerReason.None,
    string Subject = "",
    int CodeFiles = 0,
    int Total = 0)
{
    public static JunkCornerVerdict Allow() => new(true, string.Empty);

    public static JunkCornerVerdict Deny(
        string reason, JunkCornerReason code, string subject = "", int codeFiles = 0, int total = 0) =>
        new(false, reason, code, subject, codeFiles, total);
}

/// <summary>
/// Decides whether a directory is the "junk corner" that content-aware renaming is scoped to —
/// <c>Downloads</c>, a folder of scans, photos straight off a camera (ADR-0008).
/// <para>
/// The guard exists because the failure mode is severe and one-directional. Renaming
/// <c>IMG_4821.jpg</c> to <c>receipt-2026-03-invoice.jpg</c> is a clear win; renaming
/// <c>UserService.cs</c> to <c>user-service-implementation.cs</c> breaks a build, an import graph and
/// a git history at once. There is no "AI rename that understands code well enough": the correct
/// behaviour in a source tree is to refuse, so that is what this does.
/// </para>
/// </summary>
public static class JunkCornerGuard
{
    /// <summary>
    /// Extensions that make a directory somebody's working code. Not exhaustive by design — it does
    /// not need to be, because it is combined with the "inside a repository" check, and the
    /// consequence of an unlisted extension is a folder the user can still rename by hand.
    /// </summary>
    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".sln", ".slnx", ".fs", ".vb", ".c", ".h", ".cc", ".cpp", ".hpp", ".rs", ".go",
        ".java", ".kt", ".kts", ".swift", ".m", ".mm", ".rb", ".php", ".py", ".pyi", ".pl", ".lua",
        ".js", ".mjs", ".cjs", ".jsx", ".ts", ".tsx", ".vue", ".svelte", ".css", ".scss", ".less",
        ".html", ".htm", ".xaml", ".razor", ".sql", ".sh", ".ps1", ".psm1", ".bat", ".cmd",
        ".json", ".yml", ".yaml", ".toml", ".ini", ".props", ".targets", ".gradle", ".cmake", ".make",
        ".dockerfile", ".tf", ".proto", ".graphql", ".ipynb",
    };

    /// <summary>Directory names that are structural, so their contents are never junk.</summary>
    private static readonly HashSet<string> ProjectDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "source", "lib", "libs", "test", "tests", "spec", "specs", "app", "apps", "packages",
        "node_modules", "bin", "obj", "build", "dist", "target", "vendor", ".git", ".github", ".vs",
        ".idea", ".vscode", "include", "docs",
    };

    /// <summary>
    /// Share of code files at which a directory stops counting as junk. Low on purpose: a folder of
    /// scans with one stray script is still a junk corner, but a third being code means the user is
    /// looking at a project.
    /// </summary>
    public const double CodeShareLimit = 0.30;

    /// <param name="directoryName">Leaf name of the directory being renamed in.</param>
    /// <param name="fileNames">Names of the files up for renaming.</param>
    /// <param name="insideRepository">True when the directory is inside a git working copy — checked
    /// by the caller, since this class stays free of the file system.</param>
    public static JunkCornerVerdict Evaluate(
        string directoryName, IReadOnlyList<string> fileNames, bool insideRepository)
    {
        if (fileNames is null || fileNames.Count == 0)
            return JunkCornerVerdict.Deny("Nothing is selected.", JunkCornerReason.NothingSelected);

        // A version-controlled directory has a rename tool already, and it is `git mv`.
        if (insideRepository)
        {
            return JunkCornerVerdict.Deny(
                "This folder is inside a git working copy. AI renaming is limited to unversioned files "
                + "(downloads, scans, photos) — renaming tracked files would break references and history.",
                JunkCornerReason.InsideRepository);
        }

        if (ProjectDirectories.Contains(directoryName.Trim()))
        {
            return JunkCornerVerdict.Deny(
                $"«{directoryName}» is a project directory. AI renaming is limited to folders of "
                + "unnamed files such as downloads or scans.",
                JunkCornerReason.ProjectDirectory,
                subject: directoryName);
        }

        var codeFiles = fileNames.Count(name => CodeExtensions.Contains(Path.GetExtension(name)));
        if (codeFiles > 0 && (double)codeFiles / fileNames.Count > CodeShareLimit)
        {
            return JunkCornerVerdict.Deny(
                $"{codeFiles} of {fileNames.Count} selected files are source or configuration files. "
                + "AI renaming is limited to folders of unnamed files; use Multi-Rename (Ctrl+M) for code.",
                JunkCornerReason.SourceFiles,
                codeFiles: codeFiles,
                total: fileNames.Count);
        }

        return JunkCornerVerdict.Allow();
    }

    /// <summary>True when this single name is one AI renaming refuses to touch.</summary>
    public static bool IsCodeFile(string fileName) =>
        CodeExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// The names of an allowed selection that may actually be renamed. <see cref="Evaluate"/> judges the
    /// folder, and a folder of scans with a stray <c>install.ps1</c> or <c>config.json</c> passes — but
    /// that file is still code, and renaming it by content breaks whatever refers to it by name.
    /// </summary>
    public static IReadOnlyList<string> RenamableNames(IEnumerable<string> fileNames) =>
        fileNames.Where(name => !IsCodeFile(name)).ToList();
}
