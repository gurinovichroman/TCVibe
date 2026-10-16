using System.Text.Json;
using System.Text.Json.Serialization;

namespace TCV.Ai.Agents;

/// <summary>
/// Recorded agent sessions, persisted next to the executable (INV-03) as
/// <c>agent-sessions.json</c>. Written whenever an agent CLI is started from the app and read by the
/// «Agent changes» dashboard to attribute edits (<see cref="ChangeAttribution"/>).
/// </summary>
/// <remarks>
/// Deliberately a whole-file rewrite rather than an append-only journal like <c>OperationLog</c>:
/// entries are <em>mutated</em> (a session gets an end time), the list is small and bounded, and a
/// rewrite is the only way to close a session without a compaction pass. The cost is that two
/// portable instances writing at the same moment can lose one entry — acceptable for provenance
/// hints, and the reason the operation log made the opposite trade.
/// </remarks>
public sealed class AgentSessionStore
{
    /// <summary>Older sessions stop being useful for attribution long before this.</summary>
    public const int MaxSessions = 200;

    private readonly string _filePath;
    private readonly object _gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public AgentSessionStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("agent-sessions.json")) { }

    public AgentSessionStore(string filePath) => _filePath = filePath;

    public string FilePath => _filePath;

    /// <summary>All sessions, newest first. Never throws — an unreadable file reads as empty.</summary>
    public IReadOnlyList<AgentSession> Load()
    {
        lock (_gate)
        {
            return ReadAll()
                .OrderByDescending(s => s.StartedUtc)
                .ToList();
        }
    }

    /// <summary>Sessions still considered live, newest first.</summary>
    public IReadOnlyList<AgentSession> LoadOpen() =>
        Load().Where(s => s.IsOpen).ToList();

    /// <summary>
    /// Records a started session and returns it. The id is generated here so the caller can close
    /// exactly this session later, even after a restart.
    /// </summary>
    public AgentSession Start(string tool, string root, string task, DateTime? startedUtc = null)
    {
        var session = new AgentSession(
            Guid.NewGuid().ToString("n"),
            tool,
            NormalizeRoot(root),
            startedUtc ?? DateTime.UtcNow,
            null,
            task.Trim());

        lock (_gate)
        {
            var all = ReadAll();
            all.Add(session);
            WriteAll(all);
        }

        return session;
    }

    /// <summary>
    /// Marks a session finished. False when the id is unknown — the caller treats that as "already
    /// gone" rather than an error, since the file may have been trimmed or edited by hand.
    /// </summary>
    public bool Close(string sessionId, DateTime? endedUtc = null)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var index = all.FindIndex(s => string.Equals(s.Id, sessionId, StringComparison.Ordinal));
            if (index < 0) return false;

            all[index] = all[index] with { EndedUtc = endedUtc ?? DateTime.UtcNow };
            WriteAll(all);
            return true;
        }
    }

    /// <summary>Closes every open session — used on shutdown so nothing keeps claiming edits.</summary>
    public int CloseAll(DateTime? endedUtc = null)
    {
        lock (_gate)
        {
            var all = ReadAll();
            var closed = 0;
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].IsOpen) continue;
                all[i] = all[i] with { EndedUtc = endedUtc ?? DateTime.UtcNow };
                closed++;
            }

            if (closed > 0) WriteAll(all);
            return closed;
        }
    }

    /// <summary>How recent a heartbeat has to be for its window to count as still running.</summary>
    public static readonly TimeSpan AliveWindow = TimeSpan.FromMinutes(3);

    /// <summary>File beside the sessions file holding the last moment this app was known to be running.</summary>
    private string HeartbeatPath => _filePath + ".alive";

    /// <summary>
    /// Records «the application is alive right now». A crash or a kill skips <see cref="CloseAll"/>, and
    /// without this the next start could only guess when the orphaned sessions ended.
    /// </summary>
    public void Heartbeat(DateTime? nowUtc = null)
    {
        try
        {
            File.WriteAllText(HeartbeatPath, (nowUtc ?? DateTime.UtcNow).Ticks.ToString());
        }
        catch
        {
            // A missing heartbeat only makes the stale-session sweep less exact.
        }
    }

    /// <summary>
    /// Closes sessions a previous run left open (TRAP-31, the crash case: <c>SaveState</c> closes them on
    /// a normal exit). Called once at startup by the window that owns the sessions. Each is ended at the
    /// last heartbeat — never at «now»: an agent session claims every change under its root while it is
    /// open, and ending it at the restart would blame it for whatever happened while the app was down.
    /// </summary>
    /// <returns>How many sessions were closed.</returns>
    public int CloseStale(DateTime? nowUtc = null)
    {
        lock (_gate)
        {
            var all = ReadAll();
            if (!all.Any(s => s.IsOpen)) return 0;

            var lastAlive = ReadHeartbeat();
            var fallback = nowUtc ?? DateTime.UtcNow;

            // A fresh heartbeat means another window from this folder is running right now and the
            // open sessions are its own — not orphans.
            if (lastAlive is { } alive && fallback - alive < AliveWindow) return 0;

            var closed = 0;

            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].IsOpen) continue;

                // No heartbeat (older build, deleted file): the session's own start is the only
                // moment we can vouch for, and ending it there attributes nothing to it.
                var ended = lastAlive ?? all[i].StartedUtc;
                if (ended < all[i].StartedUtc) ended = all[i].StartedUtc;
                if (ended > fallback) ended = fallback;

                all[i] = all[i] with { EndedUtc = ended };
                closed++;
            }

            WriteAll(all);
            return closed;
        }
    }

    private DateTime? ReadHeartbeat()
    {
        try
        {
            return File.Exists(HeartbeatPath) && long.TryParse(File.ReadAllText(HeartbeatPath), out var ticks)
                ? new DateTime(ticks, DateTimeKind.Utc)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private List<AgentSession> ReadAll()
    {
        if (!File.Exists(_filePath)) return new List<AgentSession>();

        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<AgentSession>>(json, JsonOptions)
                   ?? new List<AgentSession>();
        }
        catch
        {
            // Provenance hints are a convenience: a corrupt file must not break the dashboard.
            return new List<AgentSession>();
        }
    }

    private void WriteAll(List<AgentSession> sessions)
    {
        try
        {
            if (sessions.Count > MaxSessions)
            {
                var trimmed = sessions
                    .OrderByDescending(s => s.StartedUtc)
                    .Take(MaxSessions)
                    .ToList();
                sessions.Clear();
                sessions.AddRange(trimmed);
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(_filePath, JsonSerializer.Serialize(sessions, JsonOptions));
        }
        catch
        {
            // Same reasoning as the read path — never fail the caller over a hint file.
        }
    }

    private static string NormalizeRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return string.Empty;

        try
        {
            return Path.GetFullPath(root).TrimEnd('\\', '/');
        }
        catch
        {
            return root.Trim().TrimEnd('\\', '/');
        }
    }
}
