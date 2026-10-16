using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCV.App.Operations;

namespace TCV.App.Services;

/// <summary>How a logged operation ended.</summary>
public enum OperationOutcome
{
    Completed,
    Failed,
    Cancelled,
}

/// <summary>
/// One line of the user-facing operation log: what was attempted, against which paths, and how it
/// ended. Serialized as one JSON object per line (JSONL) next to the executable (portable).
/// </summary>
public sealed record OperationLogEntry
{
    public DateTime Timestamp { get; init; }
    public OperationKind Kind { get; init; }
    public string Source { get; init; } = string.Empty;
    public string? Destination { get; init; }
    public int ItemCount { get; init; }
    public OperationOutcome Outcome { get; init; }
    public string? Error { get; init; }

    /// <summary>
    /// Who asked for this operation, when it was not the user directly — the agent's client id for a
    /// supervised MCP write (6.13). Null for ordinary operations, which keeps old
    /// <c>operation-log.json</c> files readable and is what the «Agent changes» dashboard uses to tell
    /// an agent's edit from a human's (<see cref="TCV.Ai.Agents.ChangeAttribution"/>).
    /// </summary>
    public string? Origin { get; init; }

    /// <summary>
    /// What a rename batch did, so the log can take it back after the window that applied it is closed
    /// (the AI rename, 6.55). Absent for every other operation, which keeps their lines as they were
    /// and old files readable.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RenameBatchRecord? Renames { get; init; }

    /// <summary>
    /// The folder the operation put new items into (<see cref="OperationLogDescriptor.ArrivedIn"/>):
    /// what the name index rescans, since <see cref="Destination"/> may name the arrived item itself.
    /// Absent where nothing arrived and in lines written before it existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ArrivedIn { get; init; }
}

/// <summary>
/// Persists the file-operation history to a JSONL file next to the executable. The queue appends an
/// entry after every copy/move/delete/new-folder, the transfer queue and remote edit after every upload
/// or download (<see cref="RemoteTransferLog"/>); the log dialog reads them back newest-first.
///
/// The file is append-only: each entry is one line, so a write is O(1) regardless of how much
/// history has accumulated, and two portable instances sharing the file only ever append (they
/// never rewrite the whole thing under each other, so neither can clobber the other's history).
/// The file is compacted back down to <see cref="MaxEntries"/> occasionally, under an exclusive
/// lock, so a long-running install never grows it without bound.
/// </summary>
public sealed class OperationLog
{
    // Roughly a few hundred KB at the cap — plenty of history without a runaway file.
    public const int MaxEntries = 1000;

    // Only compact every so many appends so the common path stays O(1); the file is allowed to
    // drift a little past the cap in between. Reads always clamp to MaxEntries regardless.
    private const int CompactEvery = 200;

    // Serialized writers can briefly collide across processes; retry a bounded number of times.
    private const int MaxLockAttempts = 20;
    private const int LockRetryDelayMs = 15;

    private readonly string _filePath;
    private readonly object _gate = new();
    private int _appendsSinceCompact;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // Must stay off: one entry per line is what makes the file append-only and O(1).
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public OperationLog()
        : this(global::TCV.Core.Domain.AppPaths.Data("operation-log.json")) { }

    public OperationLog(string filePath) => _filePath = filePath;

    /// <summary>
    /// Raised after the log changes. Carries the newly appended entry so an open dialog can add a
    /// single row instead of reloading everything; <c>null</c> means "changed wholesale" (cleared),
    /// so the listener should do a full refresh.
    /// </summary>
    public event EventHandler<OperationLogEntry?>? Changed;

    /// <summary>Appends an entry, trimming the oldest once the cap is reached. Never throws.</summary>
    public void Append(OperationLogEntry entry)
    {
        try
        {
            lock (_gate)
            {
                AppendLine(JsonSerializer.Serialize(entry, JsonOptions));
                if (++_appendsSinceCompact >= CompactEvery)
                {
                    Compact();
                    _appendsSinceCompact = 0;
                }
            }
            Changed?.Invoke(this, entry);
        }
        catch
        {
            // The log is a convenience; a write failure must never break the actual operation.
        }
    }

    /// <summary>All entries, newest first, clamped to the cap.</summary>
    public IReadOnlyList<OperationLogEntry> LoadAll()
    {
        lock (_gate)
        {
            var list = ReadAll();
            if (list.Count > MaxEntries)
                list.RemoveRange(0, list.Count - MaxEntries);
            list.Reverse();
            return list;
        }
    }

    public void Clear()
    {
        try
        {
            lock (_gate)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(_filePath)) File.Delete(_filePath);
                        break;
                    }
                    catch (IOException) when (attempt < MaxLockAttempts)
                    {
                        Thread.Sleep(LockRetryDelayMs);
                    }
                }
                _appendsSinceCompact = 0;
            }
            Changed?.Invoke(this, null);
        }
        catch
        {
            // Ignore — clearing the log is best-effort.
        }
    }

    /// <summary>
    /// Appends a single line. Writers open with <see cref="FileShare.Read"/> so a second writer
    /// (another portable instance) is serialized behind this one rather than interleaving; readers
    /// are still allowed. Retries briefly on a sharing violation.
    /// </summary>
    private void AppendLine(string json)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    _filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream);
                writer.WriteLine(json);
                return;
            }
            catch (IOException) when (attempt < MaxLockAttempts)
            {
                Thread.Sleep(LockRetryDelayMs);
            }
        }
    }

    private List<OperationLogEntry> ReadAll()
    {
        if (!File.Exists(_filePath)) return new List<OperationLogEntry>();
        try
        {
            using var stream = new FileStream(
                _filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return ParseEntries(stream);
        }
        catch
        {
            // An unreadable file must not break logging — treat it as empty.
            return new List<OperationLogEntry>();
        }
    }

    private static List<OperationLogEntry> ParseEntries(Stream stream)
    {
        var result = new List<OperationLogEntry>();
        using var reader = new StreamReader(stream, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            try
            {
                var entry = JsonSerializer.Deserialize<OperationLogEntry>(line, JsonOptions);
                if (entry is not null) result.Add(entry);
            }
            catch
            {
                // Skip a single corrupt or partially-written line; keep the rest of the history.
            }
        }
        return result;
    }

    /// <summary>
    /// Rewrites the file down to the last <see cref="MaxEntries"/> lines. Opened with
    /// <see cref="FileShare.None"/> so no other process can append between the read and the
    /// truncating rewrite — either the whole compaction happens or a colliding writer retries.
    /// </summary>
    private void Compact()
    {
        if (!File.Exists(_filePath)) return;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    _filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var list = ParseEntries(stream);
                if (list.Count <= MaxEntries) return;
                list.RemoveRange(0, list.Count - MaxEntries);

                stream.SetLength(0);
                stream.Position = 0;
                using var writer = new StreamWriter(stream);
                foreach (var entry in list)
                    writer.WriteLine(JsonSerializer.Serialize(entry, JsonOptions));
                return;
            }
            catch (IOException) when (attempt < MaxLockAttempts)
            {
                Thread.Sleep(LockRetryDelayMs);
            }
        }
    }
}
