using System.IO;
using TCV.Ai.Agents;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Git;

namespace TCV.App.Agents;

/// <summary>One row of the «Agent changes» dashboard: what changed, and as far as we can tell, who did it.</summary>
public sealed record AgentChangeRow(
    string RelativePath,
    string FullPath,
    GitFileStatus Status,
    string StatusLabel,
    bool IsStaged,
    ChangeOrigin Origin,
    string Actor,
    DateTime? AtUtc,
    string OldRelativePath = "",
    bool ByDirectory = false)
{
    /// <summary>
    /// True when reverting this means deleting a file rather than restoring one. Untracked and
    /// staged-new files have nothing in HEAD to come back to, so they take the other path — the
    /// operation queue, with a confirmation (INV-10 / INV-11).
    /// </summary>
    public bool RevertMeansDelete => Status is GitFileStatus.Untracked or GitFileStatus.Added;

    /// <summary>
    /// A staged-new file lives in the index as well as on disk. Deleting only the file left it staged
    /// («AD»), and the next commit still took the agent's version — so it is unstaged first.
    /// </summary>
    public bool RevertUnstagesFirst => Status == GitFileStatus.Added;

    /// <summary>
    /// Paths a git-side revert restores to HEAD. A rename takes both: restoring only the new name
    /// left the original deleted, which is not what «revert» promised. (The new name stays behind as
    /// an untracked file — its content is the original's, and deleting it is a separate, visible step.)
    /// </summary>
    public IReadOnlyList<string> RestorePaths =>
        Status == GitFileStatus.Renamed && OldRelativePath.Length > 0
            ? [RelativePath, OldRelativePath]
            : [RelativePath];

    /// <summary>Local time for display; the model works in UTC.</summary>
    public DateTime? At => AtUtc?.ToLocalTime();

    /// <summary>The verdict in words, with how sure it is.</summary>
    public string OriginText => AgentOriginText.Name(Origin);

    /// <summary>What the verdict rests on — shown as a tooltip.</summary>
    public string OriginHint => AgentOriginText.Hint(Origin, ByDirectory);

    /// <summary>«Yes»/«No» instead of True/False.</summary>
    public string StagedText => Localization.LocalizationManager.Get(IsStaged ? "Agent_Yes" : "Agent_No");

    /// <summary>Local time as text, empty for a deleted file that has no timestamp.</summary>
    public string AtText => At is { } at ? at.ToString("g") : string.Empty;
}

/// <summary>
/// Assembles the dashboard's rows: git says what changed, the operation log and the recorded agent
/// sessions say who was writing, <see cref="ChangeAttribution"/> joins them.
/// <para>
/// The file-system part lives here rather than in the pure core: reading modification times needs the
/// disk, and keeping that out of <see cref="ChangeAttribution"/> is what makes the attribution rules
/// testable without a working tree.
/// </para>
/// </summary>
public static class AgentChangeSet
{
    public static IReadOnlyList<AgentChangeRow> Build(
        GitWorkingTreeStatus status,
        IReadOnlyList<OperationLogEntry> operations,
        IReadOnlyList<AgentSession> sessions,
        DateTime nowUtc)
    {
        if (!status.IsRepository || status.Files.Count == 0) return Array.Empty<AgentChangeRow>();

        var facts = new List<ChangedFileFact>(status.Files.Count);
        var fullPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in status.Files)
        {
            if (fullPaths.ContainsKey(file.RelativePath)) continue;   // staged + unstaged: one fact

            var fullPath = Path.Combine(status.RepositoryRoot, file.RelativePath.Replace('/', '\\'));
            fullPaths[file.RelativePath] = fullPath;
            facts.Add(new ChangedFileFact(file.RelativePath, fullPath, LastWriteUtc(fullPath)));
        }

        var attributed = ChangeAttribution
            .Attribute(facts, ToWriteFacts(operations), sessions, nowUtc)
            .ToDictionary(a => a.RelativePath, StringComparer.OrdinalIgnoreCase);

        var rows = new List<AgentChangeRow>(status.Files.Count);
        foreach (var file in status.Files)
        {
            attributed.TryGetValue(file.RelativePath, out var verdict);

            rows.Add(new AgentChangeRow(
                file.RelativePath,
                fullPaths[file.RelativePath],
                file.Status,
                file.StatusLabel,
                file.IsStaged,
                verdict?.Origin ?? ChangeOrigin.External,
                verdict?.Actor ?? string.Empty,
                verdict?.AtUtc,
                file.OldRelativePath,
                verdict?.ByDirectory ?? false));
        }

        return rows;
    }

    /// <summary>
    /// Reduces operation-log lines to the facts attribution needs: the files each operation actually
    /// wrote or removed, by name where the log has one, and by folder only for a batch.
    /// <para>
    /// The log keeps a single item's full path but a batch only as «&lt;dir&gt; (N)», and a copy's
    /// destination is the target folder unless the item was renamed on arrival. Treating every logged path as «this file or anything
    /// directly in this folder» let a one-file copy into <c>src\</c> claim — as proof — an unrelated
    /// edit of the user's in <c>src\</c> made minutes earlier, and let a batch's <em>source</em>
    /// folder claim edits there although copying reads its sources and writes nothing to them.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ManagerWriteFact> ToWriteFacts(IReadOnlyList<OperationLogEntry> operations)
    {
        var facts = new List<ManagerWriteFact>(operations.Count);

        foreach (var entry in operations)
        {
            // A failed operation wrote nothing, so letting it claim a change would attribute an edit
            // to an operation that did not happen.
            if (entry.Outcome == OperationOutcome.Failed) continue;

            var raw = entry.Source?.Trim() ?? string.Empty;
            var source = StripCount(entry.Source);
            var batch = source.Length > 0 && source.Length != raw.Length;

            var destination = StripCount(entry.Destination);
            if (destination == OperationLogDescriptor.RecycleBinToken) destination = string.Empty;

            var files = new List<string>(2);
            var directories = new List<string>(2);

            // The written side of a batch is a folder; of a single item, the item itself.
            void Written(List<string> into, string path)
            {
                if (path.Length > 0) into.Add(path);
            }

            switch (entry.Kind)
            {
                case OperationKind.Copy:
                case OperationKind.Move:
                case OperationKind.Download:
                    if (destination.Length > 0)
                    {
                        if (batch) Written(directories, destination);
                        else if (source.Length > 0) Written(files, ArrivedAt(entry.ArrivedIn, destination, source));
                    }

                    // A move also removes its sources; a copy leaves them as they were.
                    if (entry.Kind == OperationKind.Move) Written(batch ? directories : files, source);
                    break;

                case OperationKind.Delete:
                    Written(batch ? directories : files, source);
                    break;

                case OperationKind.Rename:
                    if (batch)
                    {
                        Written(directories, source);
                    }
                    else
                    {
                        Written(files, source);
                        if (destination.Length > 0 && source.Length > 0)
                            Written(files, Child(Parent(source), destination));
                    }

                    break;

                case OperationKind.Extract:
                    // Entries land in the destination folder; the archive itself is only read.
                    Written(directories, destination);
                    break;

                case OperationKind.Pack:
                    Written(files, destination);
                    break;

                case OperationKind.Upload:
                    // Reads a file on this disk and writes one on a server: nothing here changed. The
                    // default branch would claim the uploaded file, and with it an agent's edit to it.
                    break;

                default:
                    Written(files, source);
                    Written(files, destination);
                    break;
            }

            if (files.Count == 0 && directories.Count == 0) continue;

            facts.Add(new ManagerWriteFact(
                ToUtc(entry.Timestamp), files, entry.Origin ?? string.Empty, directories));
        }

        return facts;
    }

    /// <summary>
    /// The file a single copy, move or download wrote. The logged destination is the target folder
    /// when the item kept its name, but the item itself when it arrived under another one (F5 to
    /// «D:\x\new.txt», a transfer renamed on arrival); <see cref="OperationLogEntry.ArrivedIn"/>, the
    /// folder said outright, tells the two apart (TRAP-62). A line written before it existed has only
    /// the folder form.
    /// </summary>
    private static string ArrivedAt(string? arrivedIn, string destination, string source)
    {
        var folder = arrivedIn?.Trim().TrimEnd('\\', '/') ?? string.Empty;
        var renamed = folder.Length > 0
                      && !string.Equals(folder, destination.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        return renamed ? destination : Child(destination, Leaf(source));
    }

    /// <summary>Last segment of a logged path, whichever separator it uses.</summary>
    private static string Leaf(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? trimmed : trimmed[(slash + 1)..];
    }

    /// <summary>Everything before the last segment of a logged path.</summary>
    private static string Parent(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var slash = trimmed.LastIndexOfAny(['\\', '/']);
        return slash < 0 ? string.Empty : trimmed[..slash];
    }

    /// <summary>Joins a logged folder and a name with the separator the folder already uses.</summary>
    private static string Child(string directory, string name)
    {
        var separator = directory.Contains('/') && !directory.Contains('\\') ? '/' : '\\';
        return directory.TrimEnd('\\', '/') + separator + name;
    }

    /// <summary>
    /// Recovers the path from a log line's source field. A multi-item operation is recorded as
    /// <c>&lt;directory&gt; (7)</c> by <c>OperationLogDescriptor.DescribeEntries</c>, and matching that
    /// literal string against a path would never hit.
    /// </summary>
    public static string StripCount(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return string.Empty;

        var text = source.Trim();
        if (!text.EndsWith(')')) return text;

        var open = text.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0) return text;

        // Only a pure number in the brackets is a count; "My Folder (copy)" is a real directory name.
        var inner = text[(open + 2)..^1];
        return inner.Length > 0 && inner.All(char.IsAsciiDigit) ? text[..open] : text;
    }

    private static DateTime? LastWriteUtc(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) ? File.GetLastWriteTimeUtc(fullPath) : null;
        }
        catch
        {
            // A file we cannot stat is attributed as External, which is the honest answer.
            return null;
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Local).ToUniversalTime(),
    };
}
