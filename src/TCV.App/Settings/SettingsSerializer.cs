using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TCV.Core.Domain;

namespace TCV.App.Settings;

public sealed class SettingsSerializer
{
    private readonly ILogger<SettingsSerializer> _logger;
    private readonly string _filePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public SettingsSerializer(ILogger<SettingsSerializer> logger)
        : this(logger, global::TCV.Core.Domain.AppPaths.Data("settings.json")) { }

    /// <summary>
    /// Writes to an explicit file instead of <c>settings.json</c> next to the exe. Used by tests, the
    /// same way <see cref="Navigation.FileUsageStore"/> and <see cref="Workspaces.WorkspaceStore"/>
    /// take a path — production always uses the portable location (INV-03).
    /// </summary>
    public SettingsSerializer(ILogger<SettingsSerializer> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    /// <summary>
    /// A guest window (<c>--workspace</c>, TRAP-22) does not own <c>settings.json</c>: it holds a
    /// snapshot from when it started, and writing that back would undo whatever the main window
    /// changed since. With this set, <see cref="Save"/> changes nothing on disk.
    /// </summary>
    public bool ReadOnly
    {
        get => _readOnly || (ReadOnlyWhen?.Invoke() ?? false);
        init => _readOnly = value;
    }

    private readonly bool _readOnly;

    /// <summary>
    /// Asked on every write instead of once at construction. A <c>--workspace</c> process whose
    /// workspace is gone is downgraded to a primary window after the serializer already exists
    /// (<see cref="StartupOptions.DowngradeToPrimary"/>); a flag captured at startup would keep it
    /// from ever saving its session.
    /// </summary>
    public Func<bool>? ReadOnlyWhen { get; init; }

    /// <summary>True when the main file was damaged and <c>settings.json.bak</c> was loaded instead.</summary>
    public bool RestoredFromBackup { get; private set; }

    /// <summary>Set when the file existed but was unreadable: what happened and what was kept.</summary>
    public string? LoadWarning { get; private set; }

    /// <summary>The reason the last <see cref="Save"/> failed, or null when it succeeded.</summary>
    public string? LastSaveError { get; private set; }

    public AppSettings Load()
    {
        try
        {
            var json = AtomicFile.ReadAllTextOrNull(_filePath);
            if (json is null)
            {
                _logger.LogInformation("No settings file found, using defaults");
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            _logger.LogInformation("Settings loaded from {Path}", _filePath);
            settings ??= new AppSettings();
            settings.Normalize();
            return settings;
        }
        catch (JsonException ex)
        {
            // The damaged file is kept, and the user is told. Without the copy the next save would
            // replace the only evidence of what their settings were.
            var copy = AtomicFile.PreserveCorrupt(_filePath);
            LoadWarning = copy is null
                ? ex.Message
                : $"{ex.Message} ({Path.GetFileName(copy)})";

            // The previous save left its predecessor in the .bak. Defaults here would be written on
            // the next save, and File.Replace would then move the damaged file over that good .bak.
            if (TryLoadBackup() is { } backup)
            {
                RestoredFromBackup = true;
                _logger.LogWarning(ex, "settings.json is damaged; loaded the backup (copy: {Copy})", copy);
                return backup;
            }

            _logger.LogWarning(ex, "settings.json is damaged; using defaults (copy: {Copy})", copy);
            return new AppSettings();
        }
        catch (IOException ex)
        {
            // Locked by another instance. Do not treat it as "no settings": the next Save would
            // overwrite a good file. Run on defaults and refuse to write until restart.
            LoadWarning = ex.Message;
            _logger.LogWarning(ex, "settings.json could not be read; using defaults and not saving");
            _saveBlocked = true;
            return new AppSettings();
        }
    }

    private bool _saveBlocked;

    private AppSettings? TryLoadBackup()
    {
        try
        {
            var json = AtomicFile.ReadAllTextOrNull(_filePath + ".bak");
            if (json is null) return null;

            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            settings?.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes the settings; returns false (with <see cref="LastSaveError"/>) when they could not be persisted.</summary>
    public bool Save(AppSettings settings)
    {
        if (ReadOnly)
        {
            _logger.LogDebug("Settings not saved: this window does not own settings.json");
            return false;
        }

        if (_saveBlocked)
        {
            LastSaveError = LoadWarning;
            return false;
        }

        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            AtomicFile.WriteAllText(_filePath, json);
            LastSaveError = null;
            _logger.LogDebug("Settings saved to {Path}", _filePath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LastSaveError = ex.Message;
            _logger.LogError(ex, "Failed to save settings");
            return false;
        }
    }
}
