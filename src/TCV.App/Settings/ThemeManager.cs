using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace TCV.App.Settings;

/// <summary>
/// Manages theme switching (Dark / Light) and the appearance settings laid over the theme: the
/// accent colour, the file panels' font and the visibility of the window chrome (6.33).
/// Supports hot-swapping themes without restarting the application.
/// </summary>
public sealed class ThemeManager
{
    private const string ThemesFolder = "Themes/";

    private readonly AppSettings _settings;
    private string _currentTheme = "Dark";

    public ThemeManager(AppSettings settings)
    {
        _settings = settings;
    }

    public string CurrentTheme => _currentTheme;

    /// <summary>
    /// The theme <see cref="ApplyFromSettings"/> would pick: the operating system's when "follow
    /// Windows" is on, else the saved name (an unknown name falls back to Dark).
    /// </summary>
    public string ResolveThemeName() =>
        IsSupported(Pick()) ? Pick() : "Dark";

    private string Pick() => _settings.Theme.UseSystemTheme ? DetectSystemTheme() : _settings.Theme.ThemeName;

    /// <summary>Apply the theme stored in <see cref="AppSettings"/>.</summary>
    public void ApplyFromSettings()
    {
        Apply(Pick());
    }

    /// <summary>Switch to the named theme ("Dark" or "Light") at runtime.</summary>
    public void Apply(string themeName)
    {
        if (!IsSupported(themeName)) themeName = "Dark";

        _currentTheme = themeName;

        // No application object means no resources to swap — headless callers (tests) may still ask
        // which theme would be active.
        if (Application.Current is null)
        {
            return;
        }

        var uri = new Uri($"{ThemesFolder}{themeName}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        // The user's values go into the freshly loaded theme itself rather than into the application's
        // own resources: every apply starts from a clean copy, so clearing a setting brings the
        // theme's value back, and a theme switch cannot inherit a stale override. The themes read
        // these keys with DynamicResource, which is what lets a swap repaint without a restart.
        var tones = dict[AppearanceResources.SelectionColorKey] is Color selection && dict["ForegroundColor"] is Color text
            ? new AppearanceResources.ThemeTones(selection, text)
            : (AppearanceResources.ThemeTones?)null;
        foreach (var (key, value) in AppearanceResources.Build(_settings, tones))
        {
            dict[key] = value;
        }

        var merged = Application.Current.Resources.MergedDictionaries;

        // Remove the previous palette. Base.xaml and Controls.xaml stay: they hold no colours, only
        // typography and the control styles, which read the palette with DynamicResource.
        var existing = merged.FirstOrDefault(d => IsPalette(d.Source?.OriginalString));

        if (existing is not null)
            merged.Remove(existing);

        merged.Add(dict);

        // The dialogs' native frames are not resources: they are told (6.76).
        Controls.TitleBarTheme.ApplyToOpenWindows();
    }

    private static bool IsSupported(string name) =>
        name is "Dark" or "Light";

    private static bool IsPalette(string? source) =>
        source is not null && source.StartsWith(ThemesFolder, StringComparison.Ordinal) &&
        System.IO.Path.GetFileNameWithoutExtension(source) is { } file && IsSupported(file);

    private static string DetectSystemTheme()
    {
        try
        {
            const string keyPath =
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int intVal)
                return intVal == 1 ? "Light" : "Dark";
        }
        catch
        {
            // Registry not available — fall back to dark
        }

        return "Dark";
    }
}
