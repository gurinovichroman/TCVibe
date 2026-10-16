namespace TCV.Ai.Agents;

/// <summary>Where a working-tree change came from, strongest evidence first.</summary>
public enum ChangeOrigin
{
    /// <summary>Nothing points at anyone: an editor, a build, a git command, or an agent we never saw.</summary>
    External,

    /// <summary>Inside a recorded agent session's root and time window. Circumstantial, not proof.</summary>
    Agent,

    /// <summary>A file operation of ours, recorded in the operation log.</summary>
    Manager,

    /// <summary>A supervised agent write: it went through our queue and carries the agent's tag.</summary>
    Supervised,
}

/// <summary>One changed file as the working tree reports it.</summary>
/// <param name="RelativePath">Repository-relative path, forward slashes — the key everything joins on.</param>
/// <param name="FullPath">Absolute path, used to test containment in a session root.</param>
/// <param name="ModifiedUtc">Last write time, or null when the file is gone (a deletion).</param>
public sealed record ChangedFileFact(string RelativePath, string FullPath, DateTime? ModifiedUtc);

/// <summary>
/// One line of our own operation log, reduced to what attribution needs.
/// </summary>
/// <param name="AtUtc">When the operation finished.</param>
/// <param name="Paths">Files the operation wrote or removed, by name. Matched exactly.</param>
/// <param name="Origin">Agent tag when the operation was a supervised agent write; empty for ours.</param>
/// <param name="Directories">Directories a batch wrote into or removed from without naming each file
/// (the log records a batch as its folder). They claim the files directly inside — weaker evidence
/// than a name, and reported as such.</param>
public sealed record ManagerWriteFact(
    DateTime AtUtc,
    IReadOnlyList<string> Paths,
    string Origin,
    IReadOnlyList<string>? Directories = null);

/// <summary>A changed file with a verdict about who produced it.</summary>
/// <param name="Actor">Who, in words: the agent's label, the manager, or empty for <see cref="ChangeOrigin.External"/>.</param>
/// <param name="AtUtc">The timestamp the verdict was based on, for the tooltip.</param>
/// <param name="ByDirectory">True when a log entry claimed the file through its folder (a batch) rather
/// than by name: still a record of our write into that folder at that time, but not proof about this
/// particular file.</param>
public sealed record AttributedChange(
    string RelativePath,
    ChangeOrigin Origin,
    string Actor,
    DateTime? AtUtc,
    bool ByDirectory = false);

/// <summary>
/// Joins "what changed" (git) with "who was writing" (our operation log, recorded agent sessions)
/// and produces a per-file verdict. Pure: no file system, no git, no clock — the caller supplies the
/// facts and the current time, which is what makes the heuristics below testable.
/// <para>
/// The design constraint is honesty. Git stores no author for uncommitted work and an agent writing
/// with its own tools leaves no trace in our queue, so a confident answer is impossible for the
/// general case. Rather than guess, the verdict degrades explicitly:
/// <see cref="ChangeOrigin.Supervised"/> and <see cref="ChangeOrigin.Manager"/> are records of writes
/// we performed (hard evidence), <see cref="ChangeOrigin.Agent"/> is "this happened inside a session
/// we started, in its directory" (circumstantial), and <see cref="ChangeOrigin.External"/> means we
/// genuinely do not know. The dashboard shows the difference instead of flattening it, because a
/// review surface that overstates its knowledge is worse than one that admits the gap.
/// </para>
/// </summary>
public static class ChangeAttribution
{
    /// <summary>
    /// How far before a log entry's timestamp a file's mtime may sit and still be considered part of
    /// that operation. The log records when an operation <em>finished</em>; the files it wrote are
    /// older than that by however long the operation took. Five minutes covers ordinary copies
    /// without letting an entry adopt changes from an unrelated session an hour earlier.
    /// </summary>
    public static readonly TimeSpan DefaultWriteWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How far back a logged operation may sit and still claim a <em>deletion</em>. A deleted file has
    /// no mtime to line the entry up against, so the only bound left is "recently enough to plausibly
    /// be this deletion" — and git keeps reporting the deletion until it is committed, which is why
    /// this is a day rather than <see cref="DefaultWriteWindow"/>. Without it the check degenerates
    /// into "always true" and a week-old entry naming the parent directory claims today's manual
    /// deletion as ours.
    /// </summary>
    public static readonly TimeSpan DeletionLookback = TimeSpan.FromHours(24);

    /// <summary>Clock skew allowance: a file written moments after the entry was appended.</summary>
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(2);

    public static IReadOnlyList<AttributedChange> Attribute(
        IReadOnlyList<ChangedFileFact> changes,
        IReadOnlyList<ManagerWriteFact> managerWrites,
        IReadOnlyList<AgentSession> sessions,
        DateTime nowUtc,
        TimeSpan? writeWindow = null)
    {
        ArgumentNullException.ThrowIfNull(changes);

        var window = writeWindow ?? DefaultWriteWindow;
        var result = new List<AttributedChange>(changes.Count);

        foreach (var change in changes)
        {
            result.Add(AttributeOne(
                change,
                managerWrites ?? Array.Empty<ManagerWriteFact>(),
                sessions ?? Array.Empty<AgentSession>(),
                nowUtc,
                window));
        }

        return result;
    }

    private static AttributedChange AttributeOne(
        ChangedFileFact change,
        IReadOnlyList<ManagerWriteFact> managerWrites,
        IReadOnlyList<AgentSession> sessions,
        DateTime nowUtc,
        TimeSpan window)
    {
        // A deletion has no mtime to place in time. The log can still claim it by path, because a
        // delete we performed names what it removed; a session cannot, since there is no moment to
        // test against its window.
        var modified = change.ModifiedUtc;

        // A write that names the file beats one that only names its folder, whatever their order.
        ManagerWriteFact? logMatch = null;
        var matchKind = LogMatch.None;
        foreach (var write in managerWrites)
        {
            var kind = Claims(write, change, modified, nowUtc, window);
            if (kind == LogMatch.None) continue;

            if (logMatch is null || kind < matchKind || (kind == matchKind && write.AtUtc > logMatch.AtUtc))
            {
                logMatch = write;
                matchKind = kind;
            }
        }

        if (logMatch is not null)
        {
            var byDirectory = matchKind == LogMatch.Directory;
            return logMatch.Origin.Length > 0
                ? new AttributedChange(change.RelativePath, ChangeOrigin.Supervised, logMatch.Origin, logMatch.AtUtc, byDirectory)
                : new AttributedChange(change.RelativePath, ChangeOrigin.Manager, ManagerActor, logMatch.AtUtc, byDirectory);
        }

        if (modified is { } at)
        {
            // Newest session first: several agents may share a root (worktrees of one repo), and the
            // one that was running most recently is the better guess.
            var session = sessions
                .Where(s => s.Covers(at, nowUtc) && Contains(s.Root, change.FullPath))
                .OrderByDescending(s => s.StartedUtc)
                .FirstOrDefault();

            if (session is not null)
                return new AttributedChange(change.RelativePath, ChangeOrigin.Agent, session.Label, at);
        }

        return new AttributedChange(change.RelativePath, ChangeOrigin.External, string.Empty, modified);
    }

    /// <summary>Actor shown for our own file operations.</summary>
    public const string ManagerActor = "TCVibe";

    /// <summary>How a log entry matched a file; a lower value is the stronger evidence.</summary>
    private enum LogMatch
    {
        Name,
        Directory,
        None,
    }

    /// <summary>
    /// Whether a logged operation plausibly produced this file: it named the file itself, or it was a
    /// batch into or out of the directory the file sits in — and the timing lines up. Both halves are
    /// required — a directory match alone would let one copy claim every change in a busy folder.
    /// </summary>
    private static LogMatch Claims(
        ManagerWriteFact write,
        ChangedFileFact change,
        DateTime? modifiedUtc,
        DateTime nowUtc,
        TimeSpan window)
    {
        var match = write.Paths.Any(p => SamePath(p, change.FullPath))
            ? LogMatch.Name
            : (write.Directories ?? []).Any(d => IsParentOf(d, change.FullPath))
                ? LogMatch.Directory
                : LogMatch.None;

        if (match == LogMatch.None) return LogMatch.None;

        // No mtime (deleted): the path match is all the evidence there is, so the only timing test
        // left is against the clock — the entry has to be recent enough not to be ancient history.
        if (modifiedUtc is not { } at)
            return write.AtUtc >= nowUtc - DeletionLookback ? match : LogMatch.None;

        return at <= write.AtUtc + Skew && at >= write.AtUtc - window ? match : LogMatch.None;
    }

    /// <summary>True when <paramref name="logged"/> is the file itself.</summary>
    private static bool SamePath(string logged, string fullPath) =>
        logged.Length > 0
        && string.Equals(Normalize(logged), Normalize(fullPath), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="directory"/> is the file's immediate parent. Only the immediate
    /// parent: the operation log records a batch as "&lt;dir&gt; (N)", and treating that as the whole
    /// subtree would let one copy account for everything below it.
    /// </summary>
    private static bool IsParentOf(string directory, string fullPath)
    {
        if (directory.Length == 0) return false;

        var normalizedFile = Normalize(fullPath);
        var lastSlash = normalizedFile.LastIndexOf('/');
        if (lastSlash <= 0) return false;

        return string.Equals(
            Normalize(directory), normalizedFile[..lastSlash], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="fullPath"/> is inside <paramref name="root"/>. Compares whole path
    /// segments: a plain prefix test would put <c>C:\repo-old\x</c> inside <c>C:\repo</c>.
    /// </summary>
    public static bool Contains(string root, string fullPath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return false;

        var normalizedRoot = Normalize(root).TrimEnd('/');
        var normalizedPath = Normalize(fullPath);

        if (normalizedRoot.Length == 0) return false;
        if (string.Equals(normalizedRoot, normalizedPath, StringComparison.OrdinalIgnoreCase)) return true;

        return normalizedPath.Length > normalizedRoot.Length
            && normalizedPath[normalizedRoot.Length] == '/'
            && normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips the noise that makes two spellings of one path compare unequal: separator direction
    /// and a trailing slash. Deliberately <em>not</em> a security boundary — see
    /// <c>Mcp/PathScope</c>, which resolves symlinks before it trusts a containment test.
    /// </summary>
    private static string Normalize(string path) =>
        path.Replace('\\', '/').TrimEnd('/');
}
