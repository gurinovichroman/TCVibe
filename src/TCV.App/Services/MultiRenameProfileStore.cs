using System.IO;
using System.Text.Json;

namespace TCV.App.Services;

/// <summary>
/// A saved Multi-Rename configuration — a snapshot of every field in the dialog, so a user can
/// re-apply a favourite rename recipe. Serialized as JSON.
/// </summary>
public sealed record MultiRenameProfile
{
    public string Name { get; init; } = string.Empty;
    public string TemplateText { get; init; } = "[N].[E]";
    public int CounterStart { get; init; } = 1;
    public int CounterStep { get; init; } = 1;

    /// <summary>Zero-padding of a plain <c>[C]</c>; profiles saved before it existed read as 0 (none).</summary>
    public int CounterWidth { get; init; }
    public bool ApplyUppercase { get; init; }
    public bool ApplyLowercase { get; init; }
    public bool ApplyTitleCase { get; init; }
    public string RegexFind { get; init; } = string.Empty;
    public string RegexReplace { get; init; } = string.Empty;
    public bool RegexCaseSensitive { get; init; }
    public string InsertText { get; init; } = string.Empty;
    public int InsertPosition { get; init; }
    public int DeleteStart { get; init; }
    public int DeleteCount { get; init; }
    public bool RangeRulesSkipExtension { get; init; } = true;
}

/// <summary>
/// Persists Multi-Rename profiles to a JSON file next to the executable (portable). Upserts and
/// deletes are keyed on the profile name, case-insensitively.
/// </summary>
public sealed class MultiRenameProfileStore
{
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public MultiRenameProfileStore()
        : this(global::TCV.Core.Domain.AppPaths.Data("multirename-profiles.json")) { }

    public MultiRenameProfileStore(string filePath) => _filePath = filePath;

    public IReadOnlyList<MultiRenameProfile> LoadAll()
    {
        try
        {
            if (!File.Exists(_filePath)) return Array.Empty<MultiRenameProfile>();
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<List<MultiRenameProfile>>(json, JsonOptions)
                   ?? new List<MultiRenameProfile>();
        }
        catch (JsonException)
        {
            // A corrupt file must not break the dialog — start from an empty list, but keep the
            // file: the next Save would otherwise replace the only copy of the user's recipes.
            TCV.Core.Domain.AtomicFile.PreserveCorrupt(_filePath);
            return Array.Empty<MultiRenameProfile>();
        }
        catch (IOException)
        {
            return Array.Empty<MultiRenameProfile>();
        }
    }

    public IReadOnlyList<string> Names() =>
        // Order with the same comparer used for equality (Get/Save/Delete) so the ComboBox order
        // never disagrees with which profile a name resolves to under a case-folding culture.
        LoadAll().Select(p => p.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();

    public MultiRenameProfile? Get(string name) =>
        LoadAll().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the profile, or replaces one with the same name.</summary>
    public void Save(MultiRenameProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Profile name is required.", nameof(profile));

        var list = LoadAll().Where(p => !string.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(profile);
        Write(list);
    }

    public void Delete(string name)
    {
        var list = LoadAll().Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        Write(list);
    }

    private void Write(List<MultiRenameProfile> list) =>
        TCV.Core.Domain.AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(list, JsonOptions));
}
