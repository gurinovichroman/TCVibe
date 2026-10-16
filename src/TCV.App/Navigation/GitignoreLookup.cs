using System.IO;
using TCV.Core.Domain;

namespace TCV.App.Navigation;

/// <summary>
/// The <c>.gitignore</c> rules in force for one directory of a working copy, resolved by walking
/// up to the repository root and pushing every rule file on the way down (6.20).
/// </summary>
/// <remarks>
/// Where <see cref="FileIndexWalker"/> collects rules as it descends, a panel arrives at an
/// arbitrary directory with no history, so the stack has to be rebuilt from the root each time.
/// That is a handful of small file reads per navigation, done off the UI thread by the caller.
/// <para>Local disk only: a repository is found by its <c>.git</c> entry, which a mounted archive
/// or an FTP host does not have (TRAP-02).</para>
/// </remarks>
public sealed class GitignoreLookup
{
    /// <summary>A lookup that hides nothing — used outside a repository.</summary>
    public static GitignoreLookup None { get; } = new(GitignoreScope.Empty, string.Empty, string.Empty);

    private const int MaxGitignoreBytes = 256 * 1024;

    /// <summary>Guards against a symlink loop or a pathological path turning the walk into a hang.</summary>
    private const int MaxAncestorDepth = 64;

    private readonly GitignoreScope _scope;
    private readonly string _relativeDirectory;   // slash-separated, relative to the repo root

    /// <summary>Repository root this lookup was built for, or empty when there is none.</summary>
    public string RepositoryRoot { get; }

    private GitignoreLookup(GitignoreScope scope, string relativeDirectory, string repositoryRoot)
    {
        _scope = scope;
        _relativeDirectory = relativeDirectory;
        RepositoryRoot = repositoryRoot;
    }

    /// <summary>True when this lookup can actually hide something.</summary>
    public bool IsActive => RepositoryRoot.Length > 0;

    /// <summary>The rule stack in force for the directory, for a walk that continues below it.</summary>
    internal GitignoreScope Scope => _scope;

    /// <summary>The directory, slash-separated and relative to <see cref="RepositoryRoot"/> ("" at the root).</summary>
    internal string RelativeDirectory => _relativeDirectory;

    /// <summary>
    /// Builds the rules for <paramref name="directory"/>, or <see cref="None"/> when it is not
    /// inside a git working copy or cannot be read.
    /// </summary>
    public static GitignoreLookup ForDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return None;

        DirectoryInfo? current;
        try { current = new DirectoryInfo(Path.GetFullPath(directory)); }
        catch { return None; }

        // Walk up to the repository root, remembering the chain so rules can be pushed downwards.
        var chain = new List<DirectoryInfo>();
        DirectoryInfo? root = null;
        for (var depth = 0; current is not null && depth < MaxAncestorDepth; depth++)
        {
            chain.Add(current);
            if (HasGitEntry(current)) { root = current; break; }
            current = current.Parent;
        }

        if (root is null) return None;

        chain.Reverse();   // root first, target directory last
        var scope = GitignoreScope.Empty;
        var relative = string.Empty;

        foreach (var dir in chain)
        {
            if (!ReferenceEquals(dir, root))
                relative = relative.Length == 0 ? dir.Name : relative + "/" + dir.Name;

            var rules = ReadRules(dir);
            if (rules is not null) scope = scope.Push(relative, rules);
        }

        return new GitignoreLookup(scope, relative, root.FullName);
    }

    /// <summary>True when an entry of this name in the directory is ignored by git.</summary>
    public bool IsIgnored(string name, bool isDirectory)
    {
        if (!IsActive || string.IsNullOrEmpty(name)) return false;

        var relative = _relativeDirectory.Length == 0 ? name : _relativeDirectory + "/" + name;
        return _scope.IsIgnored(relative, isDirectory);
    }

    // A worktree's ".git" is a file pointing at the real git directory, not a directory — both
    // count as "this is the root of a working copy" (TRAP-25).
    private static bool HasGitEntry(DirectoryInfo dir)
    {
        try
        {
            var path = Path.Combine(dir.FullName, ".git");
            return Directory.Exists(path) || File.Exists(path);
        }
        catch { return false; }
    }

    private static GitignoreRules? ReadRules(DirectoryInfo dir)
    {
        try
        {
            var path = Path.Combine(dir.FullName, ".gitignore");
            if (!File.Exists(path)) return null;

            using var reader = new StreamReader(path);
            var buffer = new char[MaxGitignoreBytes];
            var read = reader.Read(buffer, 0, buffer.Length);
            return GitignoreRules.Parse(new string(buffer, 0, read));
        }
        catch
        {
            return null;   // an unreadable .gitignore must not stop the listing
        }
    }
}
