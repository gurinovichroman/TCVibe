using System.IO;
using TCV.Ai.Agents;

namespace TCV.App.Agents;

/// <summary>
/// The strip under the panels: the latest agent run in the active panel's repository and what it left
/// to review. Built from the same rows as the «Agent changes» dashboard, so the two never disagree.
/// </summary>
/// <param name="Tool">The agent's name ("Claude Code").</param>
/// <param name="IsRunning">The session is still open.</param>
/// <param name="AtUtc">When it ended, or the newest change it made while it runs.</param>
/// <param name="RelativePaths">The changes the agent is credited with, repository-relative.</param>
/// <param name="Confirmed">Every one of them is a write the operation log holds (through the write
/// gate), not a guess from the session's time window.</param>
public sealed record AgentRunSummary(
    string Tool,
    bool IsRunning,
    DateTime AtUtc,
    IReadOnlyList<string> RelativePaths,
    bool Confirmed)
{
    public int FileCount => RelativePaths.Count;

    /// <returns>Null when there is nothing to show: no agent ran here, or a finished run left no change.</returns>
    public static AgentRunSummary? Build(
        string repositoryRoot,
        IReadOnlyList<AgentChangeRow> rows,
        IReadOnlyList<AgentSession> sessionsNewestFirst)
    {
        if (string.IsNullOrEmpty(repositoryRoot))
        {
            return null;
        }

        var credited = rows
            .Where(r => r.Origin is ChangeOrigin.Agent or ChangeOrigin.Supervised)
            .GroupBy(r => r.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        var session = sessionsNewestFirst.FirstOrDefault(s => SameTree(s.Root, repositoryRoot));
        if (session is null && credited.Count == 0)
        {
            return null;
        }

        if (session is { IsOpen: false } && credited.Count == 0)
        {
            return null;
        }

        var newestChange = credited.Select(r => r.AtUtc).Where(t => t.HasValue).Select(t => t!.Value)
            .DefaultIfEmpty(DateTime.MinValue).Max();

        var tool = session?.Tool
                   ?? credited.Where(r => r.Actor.Length > 0).OrderByDescending(r => r.AtUtc).Select(r => r.Actor).FirstOrDefault()
                   ?? string.Empty;

        var at = session switch
        {
            { EndedUtc: { } ended } => ended,
            not null when newestChange > session.StartedUtc => newestChange,
            not null => session.StartedUtc,
            _ => newestChange,
        };

        return new AgentRunSummary(
            tool,
            session?.IsOpen ?? false,
            at,
            credited.Select(r => r.RelativePath).ToList(),
            credited.Count > 0 && credited.All(r => r.Origin == ChangeOrigin.Supervised));
    }

    /// <summary>The session ran in this checkout: its folder is the repository, inside it, or holds it.</summary>
    private static bool SameTree(string sessionRoot, string repositoryRoot)
    {
        if (string.IsNullOrEmpty(sessionRoot))
        {
            return false;
        }

        var a = Normalize(sessionRoot);
        var b = Normalize(repositoryRoot);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase)
               || a.StartsWith(b + "\\", StringComparison.OrdinalIgnoreCase)
               || b.StartsWith(a + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('/', '\\').TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Replace('/', '\\').TrimEnd('\\');
        }
    }
}

/// <summary>The unit «2 minutes ago» is counted in.</summary>
public enum TimeAgoUnit
{
    JustNow,
    Minutes,
    Hours,
    Days,
}

/// <summary>«2 minutes ago»: the unit and the whole number of it (the wording is the string dictionary's).</summary>
public static class RelativeTime
{
    public static (TimeAgoUnit Unit, int Count) Describe(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return (TimeAgoUnit.JustNow, 0);
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return (TimeAgoUnit.Minutes, (int)elapsed.TotalMinutes);
        }

        return elapsed < TimeSpan.FromDays(1)
            ? (TimeAgoUnit.Hours, (int)elapsed.TotalHours)
            : (TimeAgoUnit.Days, (int)elapsed.TotalDays);
    }
}
