using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TCV.App.Tests;

/// <summary>
/// Guards TRAP-11 mechanically instead of by review: both dictionaries carry the same keys, every
/// key the code and the XAML ask for exists, and the windows that were moved onto the dictionaries
/// do not slip back to typed-in text or typed-in colours.
/// </summary>
public class LocalizationGuardTests
{
    private static readonly string Repo = SourceTree.Root;
    private static readonly string AppDir = SourceTree.AppDirectory;

    private static Dictionary<string, string> Load(string language)
    {
        var text = File.ReadAllText(Path.Combine(AppDir, "Localization", $"Strings.{language}.xaml"));
        return Regex.Matches(text, @"<sys:String x:Key=""([^""]+)""[^>]*>(.*?)</sys:String>", RegexOptions.Singleline)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    private static readonly Lazy<Dictionary<string, string>> Ru = new(() => Load("ru"));
    private static readonly Lazy<Dictionary<string, string>> En = new(() => Load("en"));

    [Fact]
    public void Dictionaries_HaveTheSameKeys()
    {
        Ru.Value.Keys.Except(En.Value.Keys).Should().BeEmpty("every Russian string needs an English one");
        En.Value.Keys.Except(Ru.Value.Keys).Should().BeEmpty("every English string needs a Russian one");
    }

    [Fact]
    public void MultiLineStrings_PreserveTheirLineBreaks()
    {
        // XAML folds whitespace in element text, a &#10; included: without xml:space="preserve"
        // every paragraph break in a message (and every line of a help article) became a space.
        foreach (var language in new[] { "ru", "en" })
        {
            var text = File.ReadAllText(Path.Combine(AppDir, "Localization", $"Strings.{language}.xaml"));
            Regex.Matches(text, @"<sys:String x:Key=""([^""]+)""([^>]*)>([^<]*)</sys:String>")
                .Where(m => m.Groups[3].Value.Contains("&#10;") && !m.Groups[2].Value.Contains("xml:space=\"preserve\""))
                .Select(m => m.Groups[1].Value)
                .Should().BeEmpty($"Strings.{language}.xaml: a line break needs xml:space=\"preserve\"");
        }
    }

    [Fact]
    public void Dictionaries_HaveNoDuplicateKeys()
    {
        foreach (var language in new[] { "ru", "en" })
        {
            var text = File.ReadAllText(Path.Combine(AppDir, "Localization", $"Strings.{language}.xaml"));
            var keys = Regex.Matches(text, @"<sys:String x:Key=""([^""]+)""").Select(m => m.Groups[1].Value);
            keys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key)
                .Should().BeEmpty($"Strings.{language}.xaml must not define a key twice");
        }
    }

    [Fact]
    public void Translations_KeepTheSamePlaceholders()
    {
        var mismatched = Ru.Value
            .Where(kv => En.Value.ContainsKey(kv.Key))
            .Where(kv => Placeholders(kv.Value) != Placeholders(En.Value[kv.Key]))
            .Select(kv => kv.Key);

        mismatched.Should().BeEmpty("a {0} missing from one language breaks string.Format at runtime");
    }

    // Plural forms ({0:plural:…}) are left out: one language may need them where another does not,
    // and the forms themselves are words of that language. The number item they read stays compared.
    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{\d+[^}]*\}").Select(m => m.Value)
            .Where(item => !item.Contains(":" + TCV.App.Localization.PluralFormat.Prefix))
            .Distinct()
            .OrderBy(x => x));

    // ── Shortcuts and plural forms in the text (6.40) ─────────────────────────

    private static IEnumerable<string> GestureIds(string text) => TCV.App.Localization.GestureTokens.CommandIds(text);

    [Fact]
    public void GestureTokens_NameARealCommand_AndTheSameOnesInBothLanguages()
    {
        var hotkeys = new TCV.App.Settings.HotkeyManager(new TCV.App.Settings.AppSettings());
        var catalog = TCV.App.Commands.CommandCatalog.All.Select(c => c.Id).ToHashSet();
        var problems = new List<string>();

        foreach (var (key, en) in En.Value)
        {
            var ids = GestureIds(en).OrderBy(x => x).ToList();
            if (Ru.Value.TryGetValue(key, out var ru) && !ids.SequenceEqual(GestureIds(ru).OrderBy(x => x)))
            {
                problems.Add($"{key}: the languages name different shortcuts");
            }

            foreach (var id in ids)
            {
                // Without a gesture a bare token reads the palette title; a hint in parentheses just goes.
                if (!catalog.Contains(id) && !en.Contains($" ({{gesture:{id}}})"))
                {
                    problems.Add($"{key}: {id} is not in CommandCatalog");
                }

                if (hotkeys.GetBinding(id) is null)
                {
                    problems.Add($"{key}: {id} has no default gesture");
                }
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void KeysWithAGestureToken_AreNotBoundAsAPlainResource()
    {
        var tokenKeys = En.Value.Where(kv => GestureIds(kv.Value).Any()).Select(kv => kv.Key).ToHashSet();
        var offenders = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource)\s+([A-Za-z0-9_.]+)\}"))
            {
                if (tokenKeys.Contains(m.Groups[1].Value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
                }
            }
        }

        offenders.Should().BeEmpty("a resource binding shows the raw {gesture:…} token; use {loc:Live Key}");
    }

    // Keys that describe keys of their own window (the viewer, the finder, the search dialog) or of a
    // field (Alt+↓ in the command line), or show how to type a gesture. Those keys are fixed, not
    // commands, so there is nothing to look up.
    private static readonly HashSet<string> FixedKeyTexts =
    [
        "Search_Start", "Hotkeys_KeyTip", "Hotkeys_BadKey", "UserTools_HotkeyHint", "UserTools_ErrorHotkey",
        "Shell_ShellBoxTip",
    ];

    [Fact]
    public void Strings_DoNotTypeInACommandShortcut()
    {
        var typedIn = new Regex(@"\b(Ctrl|Alt|Shift)\+|\bF([1-9]|1[0-2])\b");
        var offenders = En.Value.Concat(Ru.Value)
            .Where(kv => typedIn.IsMatch(kv.Value))
            .Select(kv => kv.Key)
            .Where(k => !FixedKeyTexts.Contains(k) && !(k.StartsWith("Viewer_") && k != "Viewer_Title"))
            .Distinct();

        offenders.Should().BeEmpty("a typed-in shortcut lies after a rebind; name the command with {gesture:Id}");
    }

    [Fact]
    public void EnglishStrings_SayPluralsWithPluralForms()
    {
        En.Value.Where(kv => Regex.IsMatch(kv.Value, @"\w\((s|es)\)")).Select(kv => kv.Key)
            .Should().BeEmpty("\"file(s)\" reads badly; use {0:plural:file|files}");
    }

    [Fact]
    public void PluralForms_HaveAsManyFormsAsTheLanguageHas()
    {
        static IEnumerable<string> Bad(Dictionary<string, string> strings, int forms) =>
            strings.SelectMany(kv => Regex.Matches(kv.Value, @"\{\d+:plural:([^}]*)\}")
                .Where(m => m.Groups[1].Value.Split('|').Length != forms)
                .Select(m => $"{kv.Key}: {m.Value}"));

        Bad(En.Value, 2).Should().BeEmpty("English has one and other");
        Bad(Ru.Value, 3).Should().BeEmpty("Russian has one, few and many");
    }

    [Fact]
    public void KeysWithPluralForms_AreFormattedByLocalizationManager()
    {
        var pluralKeys = En.Value.Concat(Ru.Value)
            .Where(kv => kv.Value.Contains(":" + TCV.App.Localization.PluralFormat.Prefix))
            .Select(kv => kv.Key)
            .ToHashSet();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            foreach (var line in File.ReadLines(file))
            {
                foreach (Match m in Regex.Matches(line, @"""([A-Za-z0-9_]+)"""))
                {
                    if (pluralKeys.Contains(m.Groups[1].Value) && !line.Contains("LocalizationManager.Format("))
                    {
                        offenders.Add($"{Path.GetFileName(file)}: {line.Trim()}");
                    }
                }
            }
        }

        foreach (var file in XamlFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource|loc:Live)\s+([A-Za-z0-9_.]+)\}"))
            {
                if (pluralKeys.Contains(m.Groups[1].Value))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
                }
            }
        }

        offenders.Should().BeEmpty("plain string.Format prints the plural forms as a number format");
    }

    [Fact]
    public void EnglishStrings_ContainNoCyrillic()
    {
        En.Value.Where(kv => Regex.IsMatch(kv.Value, "[а-яА-ЯёЁ]")).Select(kv => kv.Key)
            .Where(k => k != "Settings_LanguageGroup") // "Language / Язык": the one label meant to be found in either language
            .Should().BeEmpty("the English dictionary is what an English-speaking user sees");
    }

    [Fact]
    public void EveryStringDefined_IsNonEmpty()
    {
        Ru.Value.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).Should().BeEmpty();
        En.Value.Where(kv => string.IsNullOrWhiteSpace(kv.Value)).Select(kv => kv.Key).Should().BeEmpty();
    }

    private static IEnumerable<string> XamlFiles() =>
        Directory.EnumerateFiles(Path.Combine(Repo, "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    [Fact]
    public void EveryResourceKeyTheXamlReferences_IsDefinedSomewhere()
    {
        var defined = new HashSet<string>(Ru.Value.Keys);
        foreach (var file in XamlFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"x:Key=""([^""]+)"""))
            {
                defined.Add(m.Groups[1].Value);
            }
        }

        var missing = new List<string>();
        foreach (var file in XamlFiles())
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{(?:DynamicResource|StaticResource|loc:Live)\s+([A-Za-z0-9_.]+)\}"))
            {
                if (!defined.Contains(m.Groups[1].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
                }
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void EveryLiteralKeyPassedToLocalizationManagerGet_IsDefined()
    {
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repo, "src"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)))
        {
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"LocalizationManager\.(?:Get\(\s*""(?<key>[A-Za-z0-9_]+)""\s*\)|Format\(\s*""(?<key>[A-Za-z0-9_]+)"")"))
            {
                if (!Ru.Value.ContainsKey(m.Groups["key"].Value))
                {
                    missing.Add($"{Path.GetFileName(file)}: {m.Groups["key"].Value}");
                }
            }
        }

        missing.Should().BeEmpty();
    }

    [Fact]
    public void KeysBuiltFromAPrefix_ExistForEveryMember()
    {
        // Text picked by enum member (EnumLocalizationConverter, the F-key bar): the key is prefix + name.
        var expected = new List<string>();
        expected.AddRange(Enum.GetNames<TCV.Network.TransferStatus>().Select(n => "Transfer_Status_" + n));
        expected.AddRange(Enum.GetNames<TCV.Network.TransferDirection>().Select(n => "Transfer_Dir_" + n));
        expected.AddRange(Enum.GetNames<TCV.MultiRename.ConflictKind>().Where(n => n != "None").Select(n => "MR_Conflict_" + n));
        expected.AddRange(new[] { "OK", "FAILED", "Missing", "Error" }.Select(n => "CS_Status_" + n));
        expected.AddRange(new[] { "View", "Edit", "Copy", "Move", "NewFolder", "Delete", "Terminal", "Quit" }
            .Select(n => "Bar_" + n));

        expected.Where(k => !Ru.Value.ContainsKey(k)).Should().BeEmpty();
    }

    // ── The help window describes every command (Shift+F1) ─────────────────────

    [Fact]
    public void EveryCommand_HasAHelpDescription()
    {
        // The help is generated from CommandCatalog; a command added without a description would show
        // up there as a title with nothing under it.
        TCV.App.Commands.CommandCatalog.All
            .Select(c => c.Id)
            .Where(id => !TCV.App.Help.HelpCatalog.IsFolded(id))
            .Select(id => TCV.App.Help.HelpCatalog.DescriptionPrefix + id)
            .Where(key => !En.Value.ContainsKey(key) || En.Value[key].Trim().Length == 0)
            .Should().BeEmpty("every command needs Help_Cmd_<Id> in both string dictionaries");
    }

    [Fact]
    public void EveryHelpArticle_HasATitleAndABody()
    {
        TCV.App.Help.HelpCatalog.TopicIds
            .SelectMany(id => new[] { TCV.App.Help.HelpCatalog.TopicTitlePrefix + id, TCV.App.Help.HelpCatalog.TopicTitlePrefix + id + "_Body" })
            .Where(key => !En.Value.ContainsKey(key))
            .Should().BeEmpty();
    }

    [Fact]
    public void NoHelpDescription_IsLeftOverFromARemovedCommand()
    {
        var ids = TCV.App.Commands.CommandCatalog.All.Select(c => c.Id).ToHashSet();

        En.Value.Keys
            .Where(k => k.StartsWith(TCV.App.Help.HelpCatalog.DescriptionPrefix, StringComparison.Ordinal))
            .Select(k => k[TCV.App.Help.HelpCatalog.DescriptionPrefix.Length..])
            .Where(id => !ids.Contains(id))
            .Should().BeEmpty("a description for a command that no longer exists describes nothing");
    }

    // ── The windows that were moved onto the dictionaries stay there ─────────

    private static readonly string[] Localised =
    [
        "MainWindow.xaml",
        Path.Combine("Controls", "CommandBar.xaml"),
        Path.Combine("Controls", "DriveBar.xaml"),
        Path.Combine("Controls", "BreadcrumbBar.xaml"),
        Path.Combine("Views", "Dialogs", "SettingsDialog.xaml"),
        Path.Combine("Views", "Dialogs", "HotkeySettingsDialog.xaml"),
        Path.Combine("Views", "Dialogs", "MultiRenameDialog.xaml"),
        Path.Combine("Views", "Dialogs", "ChecksumDialog.xaml"),
        Path.Combine("Views", "Dialogs", "TransferQueueDialog.xaml"),
        Path.Combine("Views", "Dialogs", "PluginManagerDialog.xaml"),
        Path.Combine("Views", "Dialogs", "PackDialog.xaml"),
        Path.Combine("Views", "Dialogs", "UnpackDialog.xaml"),
        Path.Combine("Views", "Dialogs", "PasswordDialog.xaml"),
        Path.Combine("Views", "Dialogs", "ConnectionManagerDialog.xaml"),
        Path.Combine("Views", "Dialogs", "SshConfigImportDialog.xaml"),
        Path.Combine("Views", "Dialogs", "HelpDialog.xaml"),
        Path.Combine("Views", "Dialogs", "WorkspacePropertiesDialog.xaml"),
        Path.Combine("Views", "Dialogs", "SavedOperationsDialog.xaml"),
    ];

    // Technical labels that read the same in every language.
    private static readonly Regex TechnicalLabel = new(@"^[A-Za-z0-9+\-:.\s✕]{1,14}$");

    [Fact]
    public void LocalisedWindows_HaveNoTypedInUiText()
    {
        var offenders = new List<string>();
        foreach (var relative in Localised)
        {
            var text = File.ReadAllText(Path.Combine(AppDir, relative));
            foreach (Match m in Regex.Matches(text, @"\b(Content|Text|Header|ToolTip|Title|Watermark)=""([^""{][^""]*)"""))
            {
                var value = m.Groups[2].Value;
                if (!Regex.IsMatch(value, @"[A-Za-zА-Яа-я]{2,}") || TechnicalLabel.IsMatch(value))
                {
                    continue;
                }

                offenders.Add($"{relative}: {m.Groups[1].Value}=\"{value}\"");
            }
        }

        offenders.Should().BeEmpty("UI text belongs in Strings.ru.xaml / Strings.en.xaml (TRAP-11)");
    }

    [Fact]
    public void LocalisedWindows_HaveNoTypedInColours()
    {
        var offenders = new List<string>();
        foreach (var relative in Localised)
        {
            var text = File.ReadAllText(Path.Combine(AppDir, relative));
            foreach (Match m in Regex.Matches(text, @"""#[0-9A-Fa-f]{3,8}"""))
            {
                offenders.Add($"{relative}: {m.Value}");
            }
        }

        offenders.Should().BeEmpty("colours come from the theme, or the Light theme shows dark islands");
    }

    [Fact]
    public void ThemeFiles_DefineTheSameBrushes()
    {
        static HashSet<string> Keys(string theme) =>
            Regex.Matches(File.ReadAllText(Path.Combine(AppDir, "Themes", theme + ".xaml")), @"x:Key=""([^""]+Brush)""")
                .Select(m => m.Groups[1].Value).ToHashSet();

        Keys("Dark").Except(Keys("Light")).Should().BeEmpty();
        Keys("Light").Except(Keys("Dark")).Should().BeEmpty();
    }

    [Fact]
    public void IconOnlyButtons_InLocalisedWindows_HaveAnAccessibleName()
    {
        var offenders = new List<string>();
        foreach (var relative in Localised)
        {
            var text = File.ReadAllText(Path.Combine(AppDir, relative));
            foreach (Match m in Regex.Matches(text, @"<Button\b[^>]*Content=""✕""[^>]*>", RegexOptions.Singleline))
            {
                if (!m.Value.Contains("AutomationProperties.Name"))
                {
                    offenders.Add($"{relative}: {m.Value[..Math.Min(80, m.Value.Length)]}");
                }
            }
        }

        offenders.Should().BeEmpty();
    }
}
