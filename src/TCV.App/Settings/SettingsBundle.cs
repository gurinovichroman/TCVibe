using System.IO;
using System.Text.Json;
using TCV.App.Workspaces;
using TCV.Search;

namespace TCV.App.Settings;

/// <summary>
/// Everything a user configured, in one portable file (6.20): the settings themselves plus the
/// side-car stores that live next to them. Exported to move a set-up to another machine or to
/// keep it in a repository; imported to replay it.
/// </summary>
/// <remarks>
/// The bundle is a snapshot, not a merge format: importing replaces each section it carries and
/// leaves the ones it does not. Deliberately excluded is anything that is not configuration —
/// last-used paths, MRU timestamps and window state ride along inside <see cref="Settings"/>
/// because they live in the same object, but nothing extra is added for them.
/// </remarks>
public sealed class SettingsBundle
{
    /// <summary>Bumped when a future version stops being readable by this one.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>App version that wrote the file — diagnostics only, never a compatibility gate.</summary>
    public string WrittenBy { get; set; } = string.Empty;

    public DateTime ExportedUtc { get; set; }

    public AppSettings? Settings { get; set; }

    public List<Workspace>? Workspaces { get; set; }

    public List<SearchPreset>? SearchPresets { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Serialises the bundle to JSON.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>
    /// Parses a bundle, or returns null when the text is not one. A newer major version is
    /// rejected rather than half-read: importing an unknown shape would silently drop settings.
    /// </summary>
    public static SettingsBundle? FromJson(string json)
    {
        try
        {
            var bundle = JsonSerializer.Deserialize<SettingsBundle>(json, JsonOptions);
            if (bundle is null || bundle.Version > CurrentVersion) return null;
            // A file with no section at all is not a bundle — most likely the wrong file.
            if (bundle.Settings is null && bundle.Workspaces is null && bundle.SearchPresets is null)
                return null;

            // Hand-editable like settings.json: a JSON null for a section would otherwise be copied
            // over the live settings and throw in the theme or hotkey reload right after import.
            bundle.Settings?.Normalize();
            return bundle;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the bundle; false when the file could not be written.</summary>
    public bool Write(string path)
    {
        try
        {
            File.WriteAllText(path, ToJson());
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Reads a bundle from disk, or null when it is missing or not a bundle.</summary>
    public static SettingsBundle? Read(string path)
    {
        try
        {
            return File.Exists(path) ? FromJson(File.ReadAllText(path)) : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}
