using System.IO;
using System.Text.Json;

namespace TCV.App.Workspaces;

/// <summary>Outcome of <see cref="WorkspaceStore.Rename"/>.</summary>
public enum RenameResult
{
    Renamed,
    InvalidName,
    NotFound,
    NameTaken,
    Unavailable,
}

/// <summary>
/// Persists workspaces to a JSON file next to the executable (portable), mirroring
/// <see cref="Services.MultiRenameProfileStore"/>. Upserts and deletes are keyed on the workspace
/// name, case-insensitively. A corrupt file degrades to the last good backup (or an empty list)
/// rather than throwing.
/// </summary>
public sealed class WorkspaceStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public WorkspaceStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("workspaces.json")) { }

    public WorkspaceStore(string filePath) => _filePath = filePath;

    /// <summary>The previous good content, left next to the store by every write.</summary>
    private string BackupPath => _filePath + ".bak";

    public IReadOnlyList<Workspace> LoadAll() =>
        TryLoadAll(out var list) ? list : Array.Empty<Workspace>();

    /// <summary>
    /// Like <see cref="LoadAll"/>, but says when the file could not be read at all (another window
    /// holds it). A caller that mirrors the store into a UI list keeps what it already shows on
    /// <c>false</c> instead of blanking the list for the duration of a neighbour's write.
    /// </summary>
    public bool TryLoad(out IReadOnlyList<Workspace> workspaces)
    {
        var ok = TryLoadAll(out var list);
        workspaces = ok ? list : Array.Empty<Workspace>();
        return ok;
    }

    /// <summary>
    /// Reads the file, telling "there is nothing to read" (missing or corrupt — an empty list is the
    /// right answer) apart from "it could not be read right now" (another window holds it). Every
    /// mutation is a read-modify-write, and treating a locked file as empty would rewrite the store
    /// from nothing — the one failure mode that actually destroys data.
    /// </summary>
    private bool TryLoadAll(out List<Workspace> list)
    {
        list = new List<Workspace>();
        try
        {
            if (!File.Exists(_filePath))
            {
                return true;
            }

            var json = File.ReadAllText(_filePath);
            list = JsonSerializer.Deserialize<List<Workspace>>(json, JsonOptions) ?? new List<Workspace>();
            return true;
        }
        catch (JsonException)
        {
            // Hand-edited or truncated by a crash. The previous write left its predecessor in the
            // .bak, so prefer that to a silent "no workspaces" that the next save would make permanent.
            // The damaged file is copied aside first: the next write moves it into the .bak and the
            // one after that overwrites it, so a hand edit with a typo would vanish without a trace.
            PreserveCorruptOnce();
            list = LoadBackup();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Where the damaged <c>workspaces.json</c> was copied this session, or null when it never was.
    /// The window reads it once to tell the user their file was replaced by the backup.
    /// </summary>
    public string? CorruptCopyPath { get; private set; }

    /// <summary>Last-write time of the damaged file already copied, so every read of the same
    /// broken file (each mutation re-reads) does not add another copy.</summary>
    private DateTime? _preservedWriteTime;

    private void PreserveCorruptOnce()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(_filePath);
            if (_preservedWriteTime == stamp)
            {
                return;
            }

            _preservedWriteTime = stamp;
            CorruptCopyPath = TCV.Core.Domain.AtomicFile.PreserveCorrupt(_filePath) ?? CorruptCopyPath;
        }
        catch (Exception)
        {
            // The copy is a courtesy; reading the backup must still go ahead.
        }
    }

    private List<Workspace> LoadBackup()
    {
        try
        {
            if (!File.Exists(BackupPath))
            {
                return new List<Workspace>();
            }

            return JsonSerializer.Deserialize<List<Workspace>>(File.ReadAllText(BackupPath), JsonOptions)
                   ?? new List<Workspace>();
        }
        catch (Exception)
        {
            return new List<Workspace>();
        }
    }

    /// <summary>Workspaces in the rail's order (<see cref="WorkspaceOrder.Arrange"/>) — the order used
    /// by the menu, the switcher and the Alt+1..9 hotkeys.</summary>
    public IReadOnlyList<Workspace> LoadOrdered() => WorkspaceOrder.Arrange(LoadAll());

    /// <summary>
    /// Numbers the named workspaces 1, 2, 3… in the order given — their places on the rail (6.79) —
    /// and leaves everything else as it is on disk: every other field of those entries, and the
    /// entries not named. A point edit, like <see cref="Touch"/>: a guest window (TRAP-22) edits the
    /// same file, and a reorder must not carry this window's copy of anything but the order.
    /// </summary>
    public bool SetOrder(IReadOnlyList<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        if (!TryLoadAll(out var list))
        {
            return false;
        }

        for (var i = 0; i < names.Count; i++)
        {
            var stored = list.FirstOrDefault(w => string.Equals(w.Name, names[i], StringComparison.OrdinalIgnoreCase));
            if (stored is not null)
            {
                stored.Order = i + 1;
            }
        }

        return Write(list);
    }

    public Workspace? Get(string name) =>
        LoadAll().FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the workspace, or replaces one with the same name. Returns false when the file
    /// could not be read or written — the caller's copy stays authoritative for its session.</summary>
    public bool Save(Workspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        if (string.IsNullOrWhiteSpace(workspace.Name))
        {
            throw new ArgumentException("Workspace name is required.", nameof(workspace));
        }

        if (!TryLoadAll(out var list))
        {
            return false;
        }

        list.RemoveAll(w => string.Equals(w.Name, workspace.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(workspace);
        return Write(list);
    }

    public bool Delete(string name)
    {
        if (!TryLoadAll(out var list))
        {
            return false;
        }

        list.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        return Write(list);
    }

    /// <summary>
    /// Records a switch-to on one workspace, leaving every other entry exactly as it is on disk.
    /// The MRU bump must not travel as a whole-file rewrite: a second window (<c>--workspace</c>)
    /// edits the same file, and rewriting it from one window's in-memory list would silently undo
    /// whatever the other saved meanwhile.
    /// </summary>
    public bool Touch(string name, DateTime lastUsedUtc)
    {
        if (!TryLoadAll(out var list))
        {
            return false;
        }

        var stored = list.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
        {
            return false;
        }

        stored.LastUsedUtc = lastUsedUtc;
        return Write(list);
    }

    /// <summary>
    /// Writes a captured layout (both panels, focus, split) into the stored entry and leaves the
    /// rest of it — colour, icon, group, pin, terminal, MRU — as it is on disk. A layout autosave
    /// must not carry this window's copy of those fields: a second window (<c>--workspace</c>) may
    /// have changed them meanwhile. Returns false, and writes nothing, when the entry is gone —
    /// another window renamed or deleted it, and re-adding it would resurrect it under the old name.
    /// </summary>
    public bool UpdateLayout(string name, Workspace layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        if (!TryLoadAll(out var list))
        {
            return false;
        }

        var stored = list.FirstOrDefault(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
        {
            return false;
        }

        stored.CopyLayoutFrom(layout);
        return Write(list);
    }

    /// <summary>
    /// Saves workspaces that came from a settings bundle, one entry at a time (a second window may
    /// own the file). The bundle is a hand-editable file from somewhere else, so entries without a
    /// name are skipped rather than thrown on, and the "command I have approved" marker is dropped:
    /// trust is earned on this machine, it does not travel. Returns how many were saved.
    /// </summary>
    public int SaveImported(IEnumerable<Workspace?> workspaces)
    {
        var saved = 0;
        foreach (var workspace in workspaces)
        {
            if (workspace is null || string.IsNullOrWhiteSpace(workspace.Name))
            {
                continue;
            }

            workspace.Name = workspace.Name.Trim();
            workspace.TrustedTerminalCommand = string.Empty;
            if (Save(workspace))
            {
                saved++;
            }
        }

        return saved;
    }

    /// <summary>
    /// Renames a workspace in place, keeping everything else about it. The new name must not belong
    /// to a different workspace (names are unique case-insensitively); changing only the casing of
    /// the same workspace is allowed.
    /// </summary>
    public RenameResult Rename(string oldName, string newName)
    {
        newName = (newName ?? string.Empty).Trim();
        if (newName.Length == 0)
        {
            return RenameResult.InvalidName;
        }

        if (!TryLoadAll(out var list))
        {
            return RenameResult.Unavailable;
        }

        var stored = list.FirstOrDefault(w => string.Equals(w.Name, oldName, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
        {
            return RenameResult.NotFound;
        }

        var clash = list.Any(w => !ReferenceEquals(w, stored)
                                  && string.Equals(w.Name, newName, StringComparison.OrdinalIgnoreCase));
        if (clash)
        {
            return RenameResult.NameTaken;
        }

        stored.Name = newName;
        return Write(list) ? RenameResult.Renamed : RenameResult.Unavailable;
    }

    /// <summary>
    /// Writes through a temp file and swaps it in, keeping the old content as <c>.bak</c>. A plain
    /// <c>WriteAllText</c> truncates first, so a crash or power loss mid-write leaves a half file
    /// that reads as "no workspaces" — and the next save would make that permanent.
    /// </summary>
    private bool Write(List<Workspace> list)
    {
        var temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOptions));
            if (File.Exists(_filePath))
            {
                File.Replace(temp, _filePath, BackupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _filePath);
            }

            return true;
        }
        catch (Exception)
        {
            // Losing one edit beats taking the UI thread down because the other window happened to
            // hold the file for the millisecond we wrote.
            try { File.Delete(temp); } catch (Exception) { /* nothing more to do */ }
            return false;
        }
    }
}
