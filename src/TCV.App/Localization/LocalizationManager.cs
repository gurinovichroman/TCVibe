using System.Globalization;
using System.Windows;
using TCV.App.Commands;
using TCV.App.Settings;

namespace TCV.App.Localization;

/// <summary>
/// Runtime localization manager based on WPF ResourceDictionaries.
/// Supports switching language without restarting the application.
/// </summary>
public static class LocalizationManager
{
    /// <summary>Language codes with a Strings.&lt;code&gt;.xaml dictionary. The settings dialog builds
    /// its list from this, so a new language is added in one place.</summary>
    public static IReadOnlyList<string> SupportedLanguages { get; } = ["en", "ru"];

    private static string _currentLanguage = "en";

    /// <summary>
    /// Raised after the language dictionary was swapped. Text that XAML binds with DynamicResource
    /// follows on its own; text built in code (the F-key bar, dynamic menu items) listens to this.
    /// </summary>
    public static event EventHandler? LanguageChanged;

    /// <summary>
    /// Raised after the shortcuts were rebound. Strings that name a shortcut with a
    /// <c>{gesture:…}</c> token read differently from then on; <see cref="LiveText"/> listens to this.
    /// </summary>
    public static event EventHandler? GesturesChanged;

    private static HotkeyManager? _hotkeys;

    public static string CurrentLanguage => _currentLanguage;

    /// <summary>Numbers in the interface's language («4 478», «1,2»), whatever the machine's culture is.</summary>
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(_currentLanguage == "ru" ? "ru-RU" : "en-US");

    /// <summary>
    /// Lets <see cref="Get"/> fill <c>{gesture:…}</c> tokens with the live bindings. Called once at
    /// startup; until then (and in headless tests) a token falls back to the command's title.
    /// </summary>
    public static void UseHotkeys(HotkeyManager hotkeys)
    {
        _hotkeys = hotkeys;
        hotkeys.BindingsChanged += (_, _) => GesturesChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Initialize localization from the saved settings or system culture.
    /// </summary>
    public static void Initialize(string? savedLanguage = null)
    {
        var lang = savedLanguage ?? DetectSystemLanguage();
        ApplyLanguage(lang);
    }

    /// <summary>
    /// Switch the UI language at runtime without restarting.
    /// Triggers a full ResourceDictionary reload so all bound strings update.
    /// Null or empty falls back to the system culture.
    /// </summary>
    public static void SetLanguage(string? languageCode)
    {
        ApplyLanguage(string.IsNullOrWhiteSpace(languageCode)
            ? DetectSystemLanguage()
            : languageCode);
    }

    /// <summary>
    /// Localised string for <paramref name="key"/>, or <c>[key]</c> when it is missing — a visible
    /// placeholder beats an empty label when a key was added to one dictionary only (TRAP-11).
    /// Null-safe on <see cref="Application.Current"/> for the same reason
    /// <see cref="ApplyLanguage"/> is: view models are constructed headless in tests, and asking
    /// for a string must not be what takes them down. Shortcut tokens come back filled in
    /// (<see cref="GestureTokens"/>).
    /// </summary>
    public static string Get(string key) =>
        Application.Current?.TryFindResource(key) is string text
            ? GestureTokens.Expand(text, GestureOf, TitleOf)
            : $"[{key}]";

    /// <summary>
    /// <see cref="Get"/> plus <see cref="string.Format(string, object[])"/>, with plural forms
    /// (<c>{0:plural:file|files}</c>, see <see cref="PluralFormat"/>). A string that has them must be
    /// formatted here; the rest may be too.
    /// </summary>
    public static string Format(string key, params object?[] args) =>
        PluralFormat.Format(_currentLanguage, Get(key), args);

    private static string? GestureOf(string commandId) =>
        _hotkeys?.GetBinding(commandId) is { } binding ? GestureText.Format(binding) : null;

    private static string? TitleOf(string commandId) =>
        CommandCatalog.All.FirstOrDefault(c => c.Id == commandId)?.Title(_currentLanguage);

    // ──────────────────────────────────────────────────────────────────────

    private static void ApplyLanguage(string code)
    {
        if (!SupportedLanguages.Contains(code))
            code = "en";

        // Swapping the merged dictionary invalidates every DynamicResource in the app, so skip
        // the work when nothing changes — opening the settings dialog re-applies the current
        // language on every show.
        if (code == _currentLanguage && Application.Current?.Resources.MergedDictionaries
                .Any(d => d.Source?.OriginalString.StartsWith("Localization/Strings.") == true) == true)
            return;

        _currentLanguage = code;

        // No application object means no resources to swap — headless callers (tests) may still
        // construct view models that switch language, and that must not throw.
        if (Application.Current is null) return;

        var uri = new Uri($"Localization/Strings.{code}.xaml", UriKind.Relative);
        var dict = new ResourceDictionary { Source = uri };

        // Replace the localization dictionary in the merged dictionaries
        var merged = Application.Current.Resources.MergedDictionaries;
        var existing = merged.FirstOrDefault(d =>
            d.Source?.OriginalString.StartsWith("Localization/Strings.") == true);

        if (existing is not null)
            merged.Remove(existing);

        merged.Add(dict);

        // Sizes and dates in the lists are formatted in code, not bound to the dictionary.
        Converters.FileSizeConverter.UseUnits(Get("Unit_B"), Get("Unit_KB"), Get("Unit_MB"), Get("Unit_GB"),
            Culture);
        Converters.DateFormatConverter.UsePattern(Get("Format_ListDate"));

        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    private static string DetectSystemLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        return culture.TwoLetterISOLanguageName switch
        {
            "ru" => "ru",
            _ => "en",
        };
    }
}
