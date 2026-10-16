using TCV.Git;

namespace TCV.App.Services;

/// <summary>
/// The "git as a process" services handed around as one dependency: worktrees, writes, clone and
/// the setup hook that makes a new worktree runnable. Bundled rather than injected one by one
/// because <c>MainViewModel</c> already carries more constructor parameters than it should
/// (<see href="../../../CLAUDE.md">TRAP-17</see>), and four more would be four more reasons
/// not to split it later.
/// <para>
/// Deliberately not a facade: it exposes the services as they are instead of wrapping every method,
/// so the git logic keeps living in <c>TCV.Git</c> where it is testable without WPF.
/// </para>
/// </summary>
public sealed class GitWorkflowServices
{
    public GitWorkflowServices(
        GitWorktreeService worktrees,
        GitWriteService write,
        GitCloneService clone,
        WorktreeSetupService setup)
    {
        Worktrees = worktrees;
        Write = write;
        Clone = clone;
        Setup = setup;
    }

    /// <summary>List/create/remove worktrees (6.9).</summary>
    public GitWorktreeService Worktrees { get; }

    /// <summary>Stage, commit, branch, stash (6.11).</summary>
    public GitWriteService Write { get; }

    /// <summary>Clone from a URL (6.11a).</summary>
    public GitCloneService Clone { get; }

    /// <summary>Carries gitignored configuration into a fresh worktree and runs its setup command (6.10).</summary>
    public WorktreeSetupService Setup { get; }
}
