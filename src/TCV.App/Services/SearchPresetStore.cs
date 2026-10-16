using System.IO;
using System.Text.Json;
using TCV.Search;

namespace TCV.App.Services;

/// <summary>
/// Persists named search presets to <c>search-presets.json</c> next to the executable (INV-03),
/// mirroring <see cref="MultiRenameProfileStore"/>. Upsert and delete are keyed on the preset name,
/// case-insensitively. A corrupt file degrades to the built-in list for reading and is copied aside
/// before the next save replaces it (<see cref="JsonListFile"/>); a locked file fails the save rather
/// than being overwritten from an empty list.
/// </summary>
public sealed class SearchPresetStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public SearchPresetStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("search-presets.json")) { }

    public SearchPresetStore(string filePath) => _filePath = filePath;

    /// <summary>
    /// Every saved preset, or the built-in starter set when there is no usable file yet — a fresh
    /// portable copy should not open an empty preset list. A file that exists and holds an empty
    /// list means the user deleted them all, and that is respected: the built-ins do not come back.
    /// </summary>
    public IReadOnlyList<SearchPreset> LoadAll() =>
        JsonListFile.TryRead<SearchPreset>(_filePath, JsonOptions, out var list) == JsonFileState.Ok
            ? list
            : SearchPreset.Builtin;

    /// <summary>Adds the preset, or replaces one of the same name. False when the write failed.</summary>
    public bool Save(SearchPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        if (string.IsNullOrWhiteSpace(preset.Name))
        {
            throw new ArgumentException("Preset name is required.", nameof(preset));
        }

        if (!TryLoadForUpdate(out var list))
        {
            return false;
        }

        list.RemoveAll(p => string.Equals(p.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(preset);
        return Write(list);
    }

    public bool Delete(string name)
    {
        if (!TryLoadForUpdate(out var list))
        {
            return false;
        }

        if (list.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        return Write(list);
    }

    /// <summary>
    /// The list a save starts from: what is on disk, the built-ins when there is no file (so saving
    /// one preset does not drop the starter set), and never a guess when the file could not be read.
    /// </summary>
    private bool TryLoadForUpdate(out List<SearchPreset> list)
    {
        switch (JsonListFile.TryRead(_filePath, JsonOptions, out list))
        {
            case JsonFileState.Ok:
                return true;
            case JsonFileState.Missing:
                list = SearchPreset.Builtin.ToList();
                return true;
            case JsonFileState.Corrupt:
                JsonListFile.BackupCorrupt(_filePath);
                list = SearchPreset.Builtin.ToList();
                return true;
            default:
                return false;   // locked: keep whatever is on disk
        }
    }

    private bool Write(List<SearchPreset> list) =>
        // Losing one preset edit beats taking the UI thread down over a locked file.
        JsonListFile.WriteAtomic(_filePath, JsonSerializer.Serialize(list, JsonOptions));
}
