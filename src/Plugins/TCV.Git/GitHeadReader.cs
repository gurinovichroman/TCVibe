using System.IO;

namespace TCV.Git;

/// <summary>
/// Answers "which branch is this directory on?" by reading <c>HEAD</c> off disk, without opening a
/// repository handle. That matters because the panel asks on every navigation: a LibGit2Sharp
/// <c>Repository</c> costs a native handle and an index load, while this is one small file read
/// against a path the OS already has cached.
/// <para>
/// Linked worktrees are the reason this is not a two-liner: inside one, <c>.git</c> is a *file*
/// pointing at <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>, and that directory — not the main
/// repository's — holds the worktree's own HEAD.
/// </para>
/// </summary>
public static class GitHeadReader
{
    /// <summary>Prefix of a symbolic HEAD; anything else in the file is a detached commit id.</summary>
    private const string RefPrefix = "ref: ";
    private const string HeadsPrefix = "refs/heads/";

    /// <summary>
    /// Branch checked out in the repository containing <paramref name="directory"/>, an abbreviated
    /// commit id when HEAD is detached, or an empty string when the path is not in a repository.
    /// Never throws: a panel label is not worth an exception.
    /// </summary>
    public static string ReadBranch(string? directory)
    {
        try
        {
            var gitDir = FindGitDirectory(directory);
            if (gitDir is null) return string.Empty;

            var headFile = Path.Combine(gitDir, "HEAD");
            if (!File.Exists(headFile)) return string.Empty;

            var head = File.ReadAllText(headFile).Trim();
            if (head.Length == 0) return string.Empty;

            if (!head.StartsWith(RefPrefix, StringComparison.Ordinal))
                return head.Length > 7 ? head[..7] : head;   // detached: show the short id

            var reference = head[RefPrefix.Length..].Trim();
            return reference.StartsWith(HeadsPrefix, StringComparison.Ordinal)
                ? reference[HeadsPrefix.Length..]
                : reference;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// True when <paramref name="label"/> is what <see cref="ReadBranch"/> returns for a detached HEAD
    /// — exactly seven hexadecimal digits — so the UI can say "detached" instead of showing a bare
    /// hash as if it were a branch. A branch literally named like that is indistinguishable, which
    /// is the price of not changing the label the panel already shows.
    /// </summary>
    public static bool LooksLikeCommitId(string? label) =>
        label is { Length: 7 } && label.All(Uri.IsHexDigit);

    /// <summary>
    /// Walks up from <paramref name="directory"/> to the git directory that owns it: the plain
    /// <c>.git</c> folder of a normal checkout, or the per-worktree directory a <c>.git</c> file
    /// points at. Returns null when nothing above the path is a repository.
    /// </summary>
    public static string? FindGitDirectory(string? directory)
    {
        var root = FindWorkingDirectory(directory);
        if (root is null) return null;

        var candidate = Path.Combine(root, ".git");
        if (Directory.Exists(candidate)) return candidate;

        return File.Exists(candidate) ? ResolveGitFile(candidate, root) : null;
    }

    /// <summary>
    /// The checkout <paramref name="directory"/> lives in: the nearest ancestor holding a
    /// <c>.git</c> entry. Separate from <see cref="FindGitDirectory"/> because for a linked worktree
    /// the two are nowhere near each other — the git directory is
    /// <c>&lt;main&gt;/.git/worktrees/&lt;name&gt;</c>, and deriving the checkout from it lands in
    /// the main repository's internals instead of the worktree.
    /// </summary>
    public static string? FindWorkingDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;

        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(directory));
        }
        catch
        {
            return null;
        }

        while (current is not null)
        {
            var candidate = Path.Combine(current.FullName, ".git");
            if (Directory.Exists(candidate) || File.Exists(candidate)) return current.FullName;

            current = current.Parent;
        }

        return null;
    }

    /// <summary>
    /// Resolves a <c>.git</c> file — <c>gitdir: ../main/.git/worktrees/feature</c> — to the directory
    /// it names. The path may be relative, and it is relative to the file's own directory, not to
    /// the process's current one.
    /// </summary>
    private static string? ResolveGitFile(string gitFile, string containingDirectory)
    {
        const string GitDirPrefix = "gitdir:";

        foreach (var line in File.ReadLines(gitFile))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith(GitDirPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            var target = trimmed[GitDirPrefix.Length..].Trim();
            if (target.Length == 0) return null;

            var resolved = Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(containingDirectory, target));

            return Directory.Exists(resolved) ? resolved : null;
        }

        return null;
    }
}
