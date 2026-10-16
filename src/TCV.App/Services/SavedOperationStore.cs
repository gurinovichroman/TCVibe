using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TCV.App.Operations;

namespace TCV.App.Services;

/// <summary>
/// Persists saved operations to <c>saved-operations.json</c> next to the executable (INV-03),
/// mirroring <see cref="MultiRenameProfileStore"/> and <see cref="SearchPresetStore"/> (6.22).
/// A locked file fails the write instead of being replaced from an empty list, and a corrupt one is
/// copied aside before the next save (<see cref="JsonListFile"/>).
/// </summary>
public sealed class SavedOperationStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public SavedOperationStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("saved-operations.json")) { }

    public SavedOperationStore(string filePath) => _filePath = filePath;

    /// <summary>Saved operations, most recently used first.</summary>
    public IReadOnlyList<SavedOperation> LoadAll()
    {
        JsonListFile.TryRead<SavedOperation>(_filePath, JsonOptions, out var list);
        return Order(list);
    }

    /// <summary>Adds the operation, or replaces one of the same name.</summary>
    public bool Save(SavedOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrWhiteSpace(operation.Name))
        {
            throw new ArgumentException("Operation name is required.", nameof(operation));
        }

        if (!TryLoadForUpdate(out var list))
        {
            return false;
        }

        list.RemoveAll(o => string.Equals(o.Name, operation.Name, StringComparison.OrdinalIgnoreCase));
        list.Add(operation);
        return Write(list);
    }

    public bool Delete(string name)
    {
        if (!TryLoadForUpdate(out var list))
        {
            return false;
        }

        if (list.RemoveAll(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase)) == 0)
        {
            return false;
        }

        return Write(list);
    }

    /// <summary>Records a run, so the picker can put what the user actually uses at the top.</summary>
    public bool Touch(string name, DateTime whenUtc)
    {
        if (!TryLoadForUpdate(out var list))
        {
            return false;
        }

        var stored = list.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        if (stored is null)
        {
            return false;
        }

        stored.LastUsedUtc = whenUtc;
        return Write(list);
    }

    private static List<SavedOperation> Order(List<SavedOperation> list) =>
        list.OrderByDescending(o => o.LastUsedUtc)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool TryLoadForUpdate(out List<SavedOperation> list)
    {
        switch (JsonListFile.TryRead(_filePath, JsonOptions, out list))
        {
            case JsonFileState.Ok:
            case JsonFileState.Missing:
                return true;
            case JsonFileState.Corrupt:
                JsonListFile.BackupCorrupt(_filePath);
                return true;
            default:
                return false;
        }
    }

    private bool Write(List<SavedOperation> list) =>
        JsonListFile.WriteAtomic(_filePath, JsonSerializer.Serialize(list, JsonOptions));
}
