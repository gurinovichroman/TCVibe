using LibGit2Sharp;

namespace TCV.Git.Tests;

/// <summary>
/// A throwaway git repository with one commit, used by the tests that exercise real git behaviour
/// rather than our own string handling. Worktrees, staging and stashing are all thin wrappers over
/// libgit2, and the only way to know the wrapper is right is to run it against a repository.
/// <para>
/// Identity is written into the repository's own config: <c>GitWriteService</c> commits with
/// <c>Configuration.BuildSignature</c>, which returns null — and fails the commit — on a machine
/// where <c>user.name</c> was never set. A CI agent is exactly such a machine.
/// </para>
/// </summary>
internal sealed class TempRepository : IDisposable
{
    public TempRepository(string? prefix = null)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"tcv_{prefix ?? "git"}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
        Repository.Init(Path);

        using var repo = new Repository(Path);
        repo.Config.Set("user.name", "TCV Test");
        repo.Config.Set("user.email", "test@example.invalid");
        // Without this the test machine's global core.autocrlf decides what a checkout writes back,
        // and "hello\n" returns from a stash as "hello\r\n" on a developer box but not on CI.
        repo.Config.Set("core.autocrlf", false);

        File.WriteAllText(System.IO.Path.Combine(Path, "README.md"), "hello\n");
        Commands.Stage(repo, "README.md");
        repo.Commit("init", Signature, Signature);

        InitialBranch = repo.Head.FriendlyName;
    }

    public string Path { get; }

    /// <summary>Whatever libgit2 called the first branch — never assumed to be "master": the name
    /// depends on the machine's <c>init.defaultBranch</c>.</summary>
    public string InitialBranch { get; }

    public static Signature Signature => new("TCV Test", "test@example.invalid", DateTimeOffset.Now);

    /// <summary>Writes a file inside the working tree, creating directories as needed.</summary>
    public string WriteFile(string relativePath, string content = "content\n")
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Sibling directory for worktrees, so they never land inside the repository.</summary>
    public string SiblingPath(string name) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileName(Path) + "-" + name);

    public void Dispose()
    {
        DeleteTree(Path);
        // Worktrees created by a test live beside the repository; clean up whatever is left.
        foreach (var leftover in Directory.EnumerateDirectories(
                     System.IO.Path.GetDirectoryName(Path)!, System.IO.Path.GetFileName(Path) + "-*"))
        {
            DeleteTree(leftover);
        }
    }

    /// <summary>Git marks its object files read-only, and a plain recursive delete throws on the
    /// first one — the same reason <see cref="GitWorktreeService"/> deletes through
    /// <see cref="TCV.Core.Domain.DirectoryTree"/>.</summary>
    internal static void DeleteTree(string path)
    {
        if (!Directory.Exists(path)) return;

        try
        {
            TCV.Core.Domain.DirectoryTree.Delete(path);
        }
        catch
        {
            // A leaked temp directory is not worth failing an otherwise green test over.
        }
    }
}
