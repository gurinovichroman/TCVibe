using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using FluentAssertions;
using TCV.App.Columns;
using TCV.App.Commands;
using TCV.App.Controls;
using TCV.App.Settings;
using TCV.App.ViewModels;

namespace TCV.App.Tests;

/// <summary>
/// What a translation promises about the program itself: the access keys of a menu, the headers a text
/// sends the reader to, the palette's and the columns' names, the date examples of the search form.
/// Read from the real dictionaries and the real XAML, so a menu that changes is checked as it now is.
/// </summary>
public partial class LocalizationGuardTests
{
    // ── Access keys: one letter, one item ─────────────────────────────────────

    /// <summary>A menu or a dialog: the keys of the strings shown in it at the same time.</summary>
    private sealed record KeyScope(string Name, IReadOnlyList<string> Keys);

    private static readonly Regex ResourceReference =
        new(@"\{(?:DynamicResource|StaticResource|loc:Live)\s+([A-Za-z0-9_.]+)\}");

    private static string? ResourceKey(XAttribute? attribute) =>
        attribute is not null && ResourceReference.Match(attribute.Value) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The letter WPF underlines: the first "_" that is not doubled ("__" is a literal one).</summary>
    private static char? AccessKey(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '_')
            {
                continue;
            }

            if (text[i + 1] != '_')
            {
                return text[i + 1];
            }

            i++;
        }

        return null;
    }

    private static IEnumerable<string> InterfaceXaml() =>
        XamlFiles().Where(f => !f.Contains(Path.DirectorySeparatorChar + "Localization" + Path.DirectorySeparatorChar));

    private static IEnumerable<KeyScope> AccessKeyScopes()
    {
        foreach (var file in InterfaceXaml())
        {
            var name = Path.GetFileName(file);
            var elements = XDocument.Load(file).Descendants().ToList();

            foreach (var parent in elements.Where(e => e.Name.LocalName is "Menu" or "ContextMenu" or "MenuItem"))
            {
                var items = parent.Elements().Where(e => e.Name.LocalName == "MenuItem")
                    .Select(e => ResourceKey(e.Attribute("Header"))).OfType<string>().ToList();
                if (items.Count > 0)
                {
                    var title = ResourceKey(parent.Attribute("Header")) ?? parent.Name.LocalName;
                    yield return new KeyScope($"{name}, menu {title}", items);
                }
            }

            // Everything else in the file (a dialog's labels) answers Alt+letter in the same window.
            var labels = elements.Where(e => e.Name.LocalName != "MenuItem")
                .SelectMany(e => new[] { ResourceKey(e.Attribute("Content")), ResourceKey(e.Attribute("Header")) })
                .OfType<string>().Distinct().ToList();
            yield return new KeyScope(name, labels);
        }

        // The menus built in code. Pinning and unpinning are one item, whichever it reads now.
        var workspace = KeysUsedBy("MainWindow.xaml.cs", "private void AddWorkspaceActions(");
        yield return new KeyScope("workspace submenu (pin)", workspace.Where(k => k != "Menu_Ws_Unpin").ToList());
        yield return new KeyScope("workspace submenu (unpin)", workspace.Where(k => k != "Menu_Ws_Pin").ToList());
        yield return new KeyScope("workspace box", KeysUsedBy("MainWindow.Chrome.cs", "private void OnWorkspaceBoxClick("));
        yield return new KeyScope("connection chip", KeysUsedBy(Path.Combine("Controls", "DriveBar.xaml.cs"), "private void OnConnectionButtonClick("));
    }

    /// <summary>The dictionary keys a method names, read from its source: a menu built in code stays checked as it changes.</summary>
    private static IReadOnlyList<string> KeysUsedBy(string relativePath, string signature)
    {
        var source = File.ReadAllText(Path.Combine(AppDir, relativePath));
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"{relativePath} should still build its menu in {signature}");
        var end = Regex.Match(source[start..], @"\r?\n    \}").Index;
        return Regex.Matches(source.Substring(start, end), @"""([A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .Where(En.Value.ContainsKey)
            .Distinct()
            .ToList();
    }

    [Fact]
    public void AccessKeys_DoNotRepeat_WithinAMenuOrADialog()
    {
        // A repeated letter only moves the focus between the items; a letter of its own runs the item.
        var problems = new List<string>();
        foreach (var scope in AccessKeyScopes())
        {
            foreach (var language in Languages)
            {
                var strings = All.Value[language];
                var repeats = scope.Keys.Distinct()
                    .Where(strings.ContainsKey)
                    .Where(k => AccessKey(strings[k]) is not null)
                    .GroupBy(k => char.ToUpperInvariant(AccessKey(strings[k])!.Value))
                    .Where(g => g.Count() > 1);

                problems.AddRange(repeats.Select(g =>
                    $"{language}, {scope.Name}: letter '{g.Key}' in {string.Join(", ", g.Select(k => $"{k} = \"{strings[k]}\""))}"));
            }
        }

        problems.Should().BeEmpty("every item of a menu and every label of a dialog needs a letter of its own");
    }

    [Fact]
    public void AccessKeys_AreDroppedOnlyWhereTheAlphabetRunsOut()
    {
        // Commands has more items than a Latin alphabet has letters, so some go without one. Nothing
        // else may solve a repeat by losing the letter.
        var scopes = AccessKeyScopes().ToList();
        var lettered = Languages.SelectMany(l => All.Value[l].Where(kv => AccessKey(kv.Value) is not null).Select(kv => kv.Key)).ToHashSet();
        var overfull = scopes.Where(s => s.Keys.Distinct().Count(lettered.Contains) > 26).SelectMany(s => s.Keys).ToHashSet();

        var problems = new List<string>();
        foreach (var scope in scopes)
        {
            foreach (var key in scope.Keys.Where(lettered.Contains).Where(k => !overfull.Contains(k)).Distinct())
            {
                problems.AddRange(Languages
                    .Where(l => All.Value[l].TryGetValue(key, out var text) && AccessKey(text) is null)
                    .Select(l => $"{l}, {scope.Name}: {key} = \"{All.Value[l][key]}\" lost its letter"));
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void AccessKeys_AreLettersTheLanguagesKeyboardTypes()
    {
        var scopes = AccessKeyScopes().SelectMany(s => s.Keys).ToHashSet();
        var problems = new List<string>();
        foreach (var language in Languages)
        {
            foreach (var (key, text) in All.Value[language].Where(kv => scopes.Contains(kv.Key)))
            {
                if (AccessKey(text) is { } letter && !Typable(language, letter, text))
                {
                    problems.Add($"{language}: {key} = \"{text}\"");
                }
            }
        }

        problems.Should().BeEmpty("a letter behind AltGr or a dead key does not work as an access key");

        static bool Typable(string language, char letter, string text) => language switch
        {
            // "Git" is the one header without a Cyrillic letter to choose.
            "ru" or "uk" => char.IsAsciiLetter(letter) ? !Regex.IsMatch(text, "[а-яА-ЯіїєґІЇЄҐ]") : char.IsLetter(letter),
            "de" => char.IsAsciiLetter(letter) || "äöüÄÖÜ".Contains(letter),
            "es" => char.IsAsciiLetter(letter) || "ñÑ".Contains(letter),
            "fr" => char.IsAsciiLetter(letter) || "éÉ".Contains(letter),
            _ => char.IsAsciiLetter(letter),
        };
    }

    // The letter a key gives on the Russian and the Ukrainian layout, by its place on the keyboard.
    private const string Qwerty = "qwertyuiop[]asdfghjkl;'zxcvbnm,.";
    private const string Russian = "йцукенгшщзхъфывапролджэячсмитьбю";
    private const string Ukrainian = "йцукенгшщзхїфівапролджєячсмитьбю";

    [Fact]
    public void TopMenuLetters_AreNotTakenByAnAltShortcut()
    {
        // Alt+letter of a command is seen first (PreviewKeyDown): a top menu on that letter never opens.
        var hotkeys = new HotkeyManager(new AppSettings());
        var taken = CommandCatalog.All
            .Select(c => hotkeys.GetBinding(c.Id))
            .OfType<HotkeyBinding>()
            .Where(b => b.Second is null && b.First.Modifiers == ModifierKeys.Alt && b.First.Key is >= Key.A and <= Key.Z)
            .Select(b => char.ToLowerInvariant(b.First.Key.ToString()[0]))
            .ToHashSet();
        taken.Should().Contain('w', "the switcher is the shortcut this test was written against");

        var top = AccessKeyScopes().Single(s => s.Name == "MainWindow.xaml, menu Menu").Keys;
        var problems = new List<string>();
        foreach (var language in Languages)
        {
            var layout = language switch { "ru" => Russian, "uk" => Ukrainian, _ => Qwerty };
            foreach (var key in top)
            {
                var text = All.Value[language][key];
                if (AccessKey(text) is not { } letter)
                {
                    continue;
                }

                var at = layout.IndexOf(char.ToLowerInvariant(letter));
                var place = at >= 0 ? Qwerty[at] : char.ToLowerInvariant(letter);
                if (taken.Contains(place))
                {
                    problems.Add($"{language}: {key} = \"{text}\": Alt+{char.ToUpperInvariant(place)} runs a command");
                }
            }
        }

        problems.Should().BeEmpty();
    }

    // ── A path in a text names the real headers ───────────────────────────────

    /// <summary>Arrows that are not a way through this program's menus: direction hints, other sites' consoles.</summary>
    private static readonly HashSet<string> ForeignArrows =
    [
        "Cmp_AllToRight", "Cmp_DirectionHint", "MR_RuleOrder", "S3Hint_gcs",
        "Conn_ClientIdHint_GoogleDrive", "Conn_ClientIdHint_OneDrive",
    ];

    /// <summary>Words of the sentence, not a header: "the Workspaces menu → the workspace → Properties".</summary>
    private const string TheWorkspace = "the workspace";

    /// <summary>The steps texts describe, as header keys; null is <see cref="TheWorkspace"/>.</summary>
    private static readonly (string? From, string? To)[] MenuSteps =
    [
        ("Menu_Commands", "Menu_SavedOpsManage"),
        ("Menu_Commands", "Menu_GadgetsManage"),
        ("Menu_Commands", "Menu_ShellsManage"),
        ("Menu_Help", "Menu_CheckUpdates"),
        ("Menu_Help", "Menu_ReportBug"),
        ("Menu_Help", "Menu_OpenLogFolder"),
        ("Menu_Settings", "Menu_Hotkeys"),
        ("Menu_Workspaces", null),
        (null, "Menu_Ws_PropertiesItem"),
        ("Settings_Title", "Settings_TabExternal"),
        ("Settings_Title", "Settings_GadgetsTab"),
        ("Settings_Title", "Settings_Files"),
        ("Settings_Title", "Settings_AgentTab"),
        ("Settings_Title", "Settings_ShellsTab"),
        ("Settings_Files", "Settings_FileEditorGroup"),
    ];

    /// <summary>A header as prose quotes it: no access key, no shortcut, no "…", no detail after a colon.</summary>
    private static string HeaderTitle(string language, string key)
    {
        var plain = MenuHeaderText.Plain(All.Value[language][key]);
        plain = Regex.Replace(plain, @"\s*\(\{gesture:[^}]*\}\)", string.Empty);
        return plain.Split(':')[0].TrimEnd('…', ' ', ' ');
    }

    private static bool EndsWithTitle(string left, string title) =>
        Regex.IsMatch(left, Regex.Escape(title) + @"[»”“""']?(\s+\p{L}+)?$", RegexOptions.IgnoreCase);

    private static bool StartsWithTitle(string right, string title) =>
        right.TrimStart('«', '„', '“', '"').StartsWith(title, StringComparison.Ordinal);

    private static (string Left, string Right) AroundArrow(string[] parts, int arrow) =>
        (parts[arrow].TrimEnd(), parts[arrow + 1].TrimStart());

    [Fact]
    public void MenuPaths_InTexts_NameTheHeadersOfTheirLanguage()
    {
        var problems = new List<string>();
        foreach (var (key, english) in En.Value.Where(kv => kv.Value.Contains('→') && !ForeignArrows.Contains(kv.Key)))
        {
            var parts = english.Split('→');
            for (var arrow = 0; arrow < parts.Length - 1; arrow++)
            {
                var (enLeft, enRight) = AroundArrow(parts, arrow);
                var step = MenuSteps.Where(s =>
                    (s.From is null ? enLeft.EndsWith(TheWorkspace, StringComparison.Ordinal) : EndsWithTitle(enLeft, HeaderTitle("en", s.From)))
                    && (s.To is null ? enRight.StartsWith(TheWorkspace, StringComparison.Ordinal) : StartsWithTitle(enRight, HeaderTitle("en", s.To))))
                    .Cast<(string? From, string? To)?>()
                    .FirstOrDefault();

                if (step is null)
                {
                    problems.Add($"en: {key}, arrow {arrow + 1} (\"{enLeft[^Math.Min(30, enLeft.Length)..]} → {enRight[..Math.Min(30, enRight.Length)]}\") names no known step; add it to MenuSteps, or to ForeignArrows if it is not one");
                    continue;
                }

                foreach (var language in Translated)
                {
                    var translated = All.Value[language][key].Split('→');
                    if (translated.Length != parts.Length)
                    {
                        problems.Add($"{language}: {key}: {translated.Length - 1} arrows, English has {parts.Length - 1}");
                        break;
                    }

                    var (left, right) = AroundArrow(translated, arrow);
                    if (step.Value.From is { } from && !EndsWithTitle(left, HeaderTitle(language, from)))
                    {
                        problems.Add($"{language}: {key}, arrow {arrow + 1}: the header {from} says \"{HeaderTitle(language, from)}\", the text \"…{left[^Math.Min(30, left.Length)..]}\"");
                    }

                    if (step.Value.To is { } to && !StartsWithTitle(right, HeaderTitle(language, to)))
                    {
                        problems.Add($"{language}: {key}, arrow {arrow + 1}: the header {to} says \"{HeaderTitle(language, to)}\", the text \"{right[..Math.Min(40, right.Length)]}…\"");
                    }
                }
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void MenuSteps_AreRealStepsThroughTheWindow()
    {
        // The table above is only worth checking texts against while it describes the window.
        var main = XDocument.Load(Path.Combine(AppDir, "MainWindow.xaml")).Descendants().ToList();
        var settings = XDocument.Load(Path.Combine(AppDir, "Views", "Dialogs", "SettingsDialog.xaml")).Descendants().ToList();

        foreach (var (from, to) in MenuSteps.Where(s => s.From is not null && s.To is not null))
        {
            var found = from switch
            {
                "Settings_Title" => settings.Any(e => e.Name.LocalName == "TabItem" && ResourceKey(e.Attribute("Header")) == to),
                "Settings_Files" => settings.Where(e => e.Name.LocalName == "TabItem" && ResourceKey(e.Attribute("Header")) == from)
                    .SelectMany(e => e.Descendants()).Any(e => ResourceKey(e.Attribute("Header")) == to),
                _ => main.Where(e => e.Name.LocalName == "MenuItem" && ResourceKey(e.Attribute("Header")) == from)
                    .SelectMany(e => e.Elements()).Any(e => ResourceKey(e.Attribute("Header")) == to),
            };

            found.Should().BeTrue($"{to} should be directly under {from}");
        }

        KeysUsedBy("MainWindow.xaml.cs", "private void AddWorkspaceActions(").Should().Contain("Menu_Ws_PropertiesItem");
    }

    // ── The palette and the columns use the same names ────────────────────────

    private static string WithoutEllipsis(string text) => MenuHeaderText.Plain(text).TrimEnd('…', ' ');

    [Fact]
    public void PaletteTitles_SayWhatTheMenuSays_WhereEnglishDoes()
    {
        // The palette may say more than the menu ("Git: commit…" under the Git menu); where English
        // uses one wording for both, a translation that uses two sends the reader looking for another command.
        var problems = new List<string>();
        var catalog = CommandCatalog.All.ToDictionary(c => c.Id);
        var items = XDocument.Load(Path.Combine(AppDir, "MainWindow.xaml")).Descendants()
            .Where(e => e.Name.LocalName == "MenuItem")
            .Select(e => (Header: ResourceKey(e.Attribute("Header")),
                Id: e.Attributes().FirstOrDefault(a => a.Name.LocalName == "MenuGestures.CommandId")?.Value))
            .Where(i => i.Header is not null && i.Id is not null);

        foreach (var (header, id) in items)
        {
            if (!catalog.TryGetValue(id!, out var command)
                || !string.Equals(WithoutEllipsis(En.Value[header!]), WithoutEllipsis(command.TitleEn), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var language in Translated)
            {
                var menu = WithoutEllipsis(All.Value[language][header!]);
                var title = language == "ru" ? command.TitleRu : All.Value[language][CommandDescriptor.TitleKeyPrefix + id];
                if (!string.Equals(menu, WithoutEllipsis(title), StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"{language}: {id}: the menu says \"{menu}\", the palette \"{title}\"");
                }
            }
        }

        problems.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ColumnCatalog.Name, "Col_Name")]
    [InlineData(ColumnCatalog.Size, "Col_Size")]
    [InlineData(ColumnCatalog.Date, "Col_Modified")]
    public void ColumnTitles_MatchTheSearchResultsGrid(string column, string gridKey)
    {
        // The same column of the same files: its heading should not change between the panel and the
        // search window.
        var descriptor = ColumnCatalog.All.Single(c => c.Id == column);
        En.Value[gridKey].Should().Be(descriptor.TitleEn);
        Ru.Value[gridKey].Should().Be(descriptor.TitleRu);

        foreach (var language in Translated.Where(l => l != "ru"))
        {
            All.Value[language][ColumnDescriptor.TitleKeyPrefix + column].Should().Be(All.Value[language][gridKey], language);
        }
    }

    // ── Room for the words ────────────────────────────────────────────────────

    [Fact]
    public void ButtonsWithText_HaveAMinimumWidth_NotAFixedOne()
    {
        // "Abbrechen" did not fit the 80 px of a dialog's Cancel button: the last letter was cut off, and
        // only in the languages nobody looked at. A minimum keeps the English look and leaves room.
        var offenders = new List<string>();
        foreach (var file in InterfaceXaml())
        {
            foreach (var button in XDocument.Load(file).Descendants().Where(e => e.Name.LocalName == "Button"))
            {
                if (button.Attribute("Width") is { Value: not "Auto" } && ResourceKey(button.Attribute("Content")) is { } key)
                {
                    offenders.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }

        offenders.Should().BeEmpty("a translation can be longer than the English word");
    }

    // ── The date examples of the search form ──────────────────────────────────

    [Theory]
    [InlineData("Search_DateHint")]
    [InlineData("Search_BadDate")]
    public void DateExamples_MeanTheSameDay_InEveryLanguageAndOnEveryMachine(string key)
    {
        // DateParser tries the fixed forms before the machine's culture, and that culture is Windows',
        // not the interface language's. So the examples promise only the fixed forms. A day above 12
        // cannot be read as a month, on any reader's habit.
        var theDay = new DateTime(2026, 10, 31);
        var machine = CultureInfo.CurrentCulture;
        try
        {
            foreach (var language in Languages)
            {
                var examples = Regex.Matches(All.Value[language][key], @"\d{4}-\d{1,2}-\d{1,2}|\d{1,2}\.\d{1,2}\.\d{4}")
                    .Select(m => m.Value).ToList();
                examples.Should().HaveCount(2, $"{language}: one example of each form");

                foreach (var example in examples)
                {
                    foreach (var culture in Languages.Select(TCV.App.Localization.LocalizationManager.CultureOf))
                    {
                        CultureInfo.CurrentCulture = culture;
                        DateParser.TryParse(example, out var parsed).Should().BeTrue($"{language}: \"{example}\" on {culture.Name}");
                        parsed.Should().Be(theDay, $"{language}: \"{example}\" on {culture.Name}");
                    }
                }
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = machine;
        }
    }
}
