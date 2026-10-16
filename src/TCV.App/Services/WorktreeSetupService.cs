using System.IO;
using Microsoft.Extensions.Logging;
using TCV.App.Settings;
using TCV.Git;

namespace TCV.App.Services;

/// <summary>What the setup hook actually did, for the message shown after the worktree appears.</summary>
/// <param name="CopiedFiles">Untracked files carried over from the source checkout.</param>
/// <param name="CommandStarted">True when the setup command was launched in a terminal.</param>
public sealed record WorktreeSetupResult(int CopiedFiles, bool CommandStarted);

/// <summary>
/// The second half of "new worktree for this task": everything git deliberately will not do for you.
/// A fresh worktree is a clean tree, which is the point — and also why it does not run: the
/// gitignored configuration is missing and the dependencies are not installed. Those two chores are
/// the whole friction of the worktree workflow, so they happen here, right after the checkout.
/// </summary>
public sealed class WorktreeSetupService
{
    /// <summary>
    /// Directories never walked when looking for files to carry over. These are exactly the folders
    /// a setup command rebuilds, and one of them (<c>node_modules</c>) can hold more entries than the
    /// rest of the repository put together — walking it would stall the dialog for no benefit.
    /// </summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "target", "out",
        ".venv", "venv", "__pycache__", ".next", ".nuxt", ".gradle", "packages", ".vs",
    };

    /// <summary>
    /// How deep the carry-over scan goes. Configuration files that matter live near the top of a
    /// project; an unbounded walk on a monorepo is a hang, not a feature.
    /// </summary>
    private const int MaxScanDepth = 5;

    private readonly TerminalService _terminal;
    private readonly ILogger<WorktreeSetupService> _logger;

    public WorktreeSetupService(TerminalService terminal, ILogger<WorktreeSetupService> logger)
    {
        _terminal = terminal;
        _logger = logger;
    }

    /// <summary>
    /// Where a worktree for <paramref name="repositoryRoot"/> should go: the configured root when
    /// there is one, otherwise <c>&lt;repo&gt;-worktrees</c> beside the checkout. Beside and not
    /// inside — a worktree nested in its own repository turns up as untracked in every status and
    /// gets swept into the next <c>git add .</c>.
    /// </summary>
    public static string SuggestWorktreeRoot(string repositoryRoot, string? configuredRoot)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot)) return configuredRoot.Trim();
        if (string.IsNullOrWhiteSpace(repositoryRoot)) return string.Empty;

        var root = Path.GetFullPath(repositoryRoot).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(root);
        var name = Path.GetFileName(root);

        return string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name)
            ? root + "-worktrees"
            : Path.Combine(parent, name + "-worktrees");
    }

    /// <summary>Carry patterns in force: the user's list, or the built-in defaults when unset.</summary>
    public static IReadOnlyList<string> EffectivePatterns(GitSettings settings) =>
        settings.WorktreeCarryPatterns.Count > 0
            ? settings.WorktreeCarryPatterns
            : WorktreeSetupPlan.DefaultCarryPatterns;

    /// <summary>
    /// Runs the hook: copies the matching untracked files from <paramref name="sourceRoot"/> into
    /// <paramref name="worktreePath"/>, then starts the setup command there. Failures are reported,
    /// never thrown — the worktree already exists at this point, and losing it over a missing
    /// <c>.env</c> would be worse than the missing file.
    /// </summary>
    public Task<WorktreeSetupResult> RunAsync(
        string sourceRoot,
        string worktreePath,
        GitSettings settings,
        bool carryFiles,
        bool runCommand,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var copied = carryFiles
                ? CopyCarryFiles(sourceRoot, worktreePath, EffectivePatterns(settings), ct)
                : 0;

            var started = runCommand
                          && !string.IsNullOrWhiteSpace(settings.WorktreeSetupCommand)
                          && _terminal.OpenWithCommand(worktreePath, settings.WorktreeSetupCommand);

            return new WorktreeSetupResult(copied, started);
        }, ct);

    /// <summary>
    /// Copies the files the patterns select, recreating the directories they sit in. Existing files
    /// in the target are left alone: the worktree is fresh, so anything already there was put there
    /// by the checkout and is tracked content that must not be overwritten by a local variant.
    /// </summary>
    private int CopyCarryFiles(
        string sourceRoot, string targetRoot, IReadOnlyList<string> patterns, CancellationToken ct)
    {
        if (!Directory.Exists(sourceRoot) || !Directory.Exists(targetRoot)) return 0;

        var candidates = EnumerateRelativePaths(sourceRoot, ct);
        var selected = WorktreeSetupPlan.SelectCarryFiles(candidates, patterns);

        var copied = 0;
        foreach (var relative in selected)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var source = Path.Combine(sourceRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(targetRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(target)) continue;

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                File.Copy(source, target);
                copied++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not carry {File} into the new worktree", relative);
            }
        }

        if (copied > 0)
            _logger.LogInformation("Carried {Count} untracked file(s) into {Path}", copied, targetRoot);

        return copied;
    }

    /// <summary>
    /// Relative paths under <paramref name="root"/>, skipping build output and capped in depth.
    /// Written as an explicit queue rather than <see cref="SearchOption.AllDirectories"/> because
    /// that overload offers no way to prune a subtree, and pruning is the entire point.
    /// </summary>
    private IEnumerable<string> EnumerateRelativePaths(string root, CancellationToken ct)
    {
        var results = new List<string>();
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (directory, depth) = queue.Dequeue();

            try
            {
                foreach (var file in Directory.EnumerateFiles(directory))
                    results.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));

                if (depth >= MaxScanDepth) continue;

                foreach (var sub in Directory.EnumerateDirectories(directory))
                {
                    var name = Path.GetFileName(sub);
                    if (SkipDirectories.Contains(name)) continue;
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex)
            {
                // A denied or vanished subdirectory must not abort the scan of everything else.
                _logger.LogDebug(ex, "Skipping {Directory} while scanning for carry-over files", directory);
            }
        }

        return results;
    }
}
