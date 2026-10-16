using System.IO;
using System.Text;
using TCV.Core.Domain;

namespace TCV.App.Navigation;

/// <summary>What a name-index walk skips and where it stops.</summary>
/// <param name="SkipVendored">Skip <c>node_modules</c>, <c>__pycache__</c> and virtualenvs by name, and a
/// profile's AppData and package caches (<see cref="NameIndexCrawlPolicy"/>). On for the whole-disk
/// default roots, where nobody navigates to a dependency by name; off when the user points the index at
/// a folder on purpose (they may well be looking inside one).</param>
/// <param name="RespectGitignore">Do not enter directories a <c>.gitignore</c> on the way down ignores
/// (<c>bin/</c>, <c>obj/</c>, <c>dist/</c>, <c>target/</c>). Ignored <i>files</i> outside such directories
/// stay searchable: <c>.env</c> is exactly what one looks for by name.</param>
/// <param name="MaxEntries">Hard cap; the walk stops and reports truncation.</param>
/// <param name="MaxDepth">Directory levels entered below a root; 0 lists the root's own children only.</param>
/// <param name="Priority">Which folders are walked first (6.56); null walks plain breadth-first.</param>
public sealed record NameIndexWalkOptions(
    bool SkipVendored = true, bool RespectGitignore = true, int MaxEntries = NameIndexService.MaxEntries,
    int MaxDepth = int.MaxValue, NameIndexPriority? Priority = null);

/// <summary>Result of a walk.</summary>
public sealed record NameIndexWalkResult(IReadOnlyList<NameIndexEntry> Entries, bool Truncated);

/// <summary>
/// Breadth-first disk walk behind the whole-disk name index. Pure enumeration — no UI, no state — so
/// the skip rules are testable on a temp directory.
/// </summary>
public static class NameIndexWalker
{
    private const int MaxGitignoreBytes = 256 * 1024;

    /// <param name="Tier">The <see cref="NameIndexPriority"/> tier this folder hands down to its children.</param>
    private readonly record struct Level(
        string Path, string Relative, int Depth, GitignoreScope Scope, int Tier = NameIndexPriority.Rest);

    /// <summary>
    /// Walks <paramref name="roots"/>. Directories that cannot be read are skipped rather than
    /// aborting — an inaccessible system folder is normal. Reparse points (junctions, symlinks) are
    /// not followed: they would send the walk round in circles.
    /// </summary>
    /// <param name="progress">Called with the running entry count every couple of thousand entries.</param>
    public static NameIndexWalkResult Walk(
        IReadOnlyList<string> roots, NameIndexWalkOptions options, CancellationToken ct,
        IProgress<int>? progress = null)
    {
        // Every root is listed first, whatever its tier: a drive's own listing is a few dozen entries,
        // and it is what tells the walk where D:\Projects is before the profile's tier fills the cap.
        var priority = options.Priority ?? NameIndexPriority.None;
        var starts = roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => (
                new Level(root, string.Empty, 0, GitignoreScope.Empty, priority.ClassifyRoot(root).Inherited),
                NameIndexPriority.Explicit));
        return WalkCore(starts, options, ct, progress);
    }

    private static NameIndexWalkResult WalkCore(
        IEnumerable<(Level Level, int Walk)> starts, NameIndexWalkOptions options, CancellationToken ct,
        IProgress<int>? progress)
    {
        var entries = new List<NameIndexEntry>();
        var priority = options.Priority ?? NameIndexPriority.None;

        // Breadth-first within a priority tier, so a cancelled or truncated walk still covers the
        // shallow — and most navigable — part of every root rather than all of the first one; and the
        // user's own folders before the rest of the disk, so the cap cuts noise rather than projects.
        // The sequence number keeps equal keys in arrival order: with no priority this is the plain
        // FIFO breadth-first walk it always was.
        var queue = new PriorityQueue<Level, (int Walk, int Depth, long Seq)>();
        long seq = 0;
        foreach (var (start, walk) in starts)
        {
            queue.Enqueue(start, (walk, start.Depth, seq++));
        }

        var truncated = false;
        var nextReport = 2000;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var level = queue.Dequeue();

            FileSystemInfo[] children;
            try
            {
                children = new DirectoryInfo(level.Path).GetFileSystemInfos();
            }
            catch
            {
                continue;   // unreadable or vanished
            }

            var scope = options.RespectGitignore ? PushGitignore(level, children) : level.Scope;

            foreach (var child in children)
            {
                if (entries.Count >= options.MaxEntries)
                {
                    truncated = true;
                    goto done;
                }

                var isDirectory = (child.Attributes & FileAttributes.Directory) != 0;
                if (!isDirectory)
                {
                    entries.Add(new NameIndexEntry(child.FullName, child.Name, false));
                    continue;
                }

                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (NameIndexCrawlPolicy.IsSkipped(child.Name, level.Path, wholeDisk: options.SkipVendored))
                {
                    continue;
                }

                var relative = level.Relative.Length == 0 ? child.Name : level.Relative + "/" + child.Name;
                if (options.RespectGitignore && scope.IsIgnoredEntry(relative, isDirectory: true))
                {
                    continue;
                }

                entries.Add(new NameIndexEntry(child.FullName, child.Name, true));
                if (level.Depth < options.MaxDepth)
                {
                    var (walk, inherited) = priority.Classify(child.FullName, level.Tier);
                    var next = new Level(child.FullName, relative, level.Depth + 1, scope, inherited);
                    queue.Enqueue(next, (walk, next.Depth, seq++));
                }
            }

            if (entries.Count >= nextReport)
            {
                progress?.Report(entries.Count);
                nextReport = entries.Count + 2000;
            }
        }

    done:
        progress?.Report(entries.Count);
        return new NameIndexWalkResult(entries, truncated);
    }

    private static GitignoreScope PushGitignore(Level level, FileSystemInfo[] children)
    {
        var file = children.FirstOrDefault(c =>
            (c.Attributes & FileAttributes.Directory) == 0 &&
            string.Equals(c.Name, ".gitignore", StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            return level.Scope;
        }

        try
        {
            using var reader = new StreamReader(file.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[MaxGitignoreBytes];
            var read = reader.Read(buffer, 0, buffer.Length);
            return level.Scope.Push(level.Relative, GitignoreRules.Parse(new string(buffer, 0, read)));
        }
        catch
        {
            return level.Scope;   // an unreadable .gitignore must not abort the index
        }
    }

    /// <summary>
    /// Lists everything below one directory for an incremental refresh, capped so a copied folder of a
    /// million files cannot stall it. A truncated result is <b>not</b> the whole subtree: the caller
    /// must not let it replace what the index already holds (see <see cref="NameIndexWalkResult.Truncated"/>).
    /// </summary>
    /// <remarks>
    /// The walk starts in the middle of a tree, so the <c>.gitignore</c> files above the folder are
    /// loaded first; otherwise a rescan of <c>repo/src</c> would index the <c>bin/</c> and <c>obj/</c>
    /// folders the repository's top-level rules keep out of a full build.
    /// </remarks>
    public static NameIndexWalkResult ListSubtree(
        string directory, NameIndexWalkOptions options, CancellationToken ct, int maxEntries = 20_000)
    {
        var start = new Level(directory, string.Empty, 0, GitignoreScope.Empty);
        if (options.RespectGitignore)
        {
            var trimmed = directory.TrimEnd((char)92, '/');
            var lookup = GitignoreLookup.ForDirectory(Path.GetDirectoryName(trimmed));
            if (lookup.IsActive)
            {
                var name = Path.GetFileName(trimmed);
                var relative = lookup.RelativeDirectory.Length == 0 ? name : lookup.RelativeDirectory + "/" + name;
                if (lookup.Scope.IsIgnored(relative, isDirectory: true))
                {
                    return new NameIndexWalkResult([], Truncated: false);   // a full build never enters it either
                }

                start = new Level(directory, relative, 0, lookup.Scope);
            }
        }

        // One folder, refreshed whole or not at all: there is nothing to put first.
        return WalkCore([(start, NameIndexPriority.Rest)], options with { MaxEntries = maxEntries, Priority = null }, ct, progress: null);
    }
}
