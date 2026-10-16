using System.Text.Json;

namespace TCV.App.Settings;

/// <summary>
/// A deep copy of <see cref="AppSettings"/>, made by a JSON round trip — the same shape that goes
/// to disk, so a field added to the settings is copied without anyone listing it here. Dialogs write
/// into a copy first: a save that fails leaves the dialog open, and a Cancel after that must not
/// leave half-applied values in the live instance for the next save to persist.
/// </summary>
public static class SettingsCopy
{
    public static AppSettings Of(AppSettings source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(source)) ?? new AppSettings();
        copy.Normalize();
        return copy;
    }
}
