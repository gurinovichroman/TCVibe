using System.IO;
using System.Windows;
using System.Windows.Media;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// Accent colour, panel font and chrome visibility (6.33): what settings.json holds, what the
/// Settings dialog writes, and what <see cref="AppearanceResources"/> lays over the theme.
/// </summary>
public sealed class AppearanceSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-appearance-" + Guid.NewGuid().ToString("N"));

    public AppearanceSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SettingsSerializer Serializer() =>
        new(NullLogger<SettingsSerializer>.Instance, Path.Combine(_dir, "settings.json"));

    private AppSettings LoadJson(string json)
    {
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        return Serializer().Load();
    }

    // ── settings.json ─────────────────────────────────────────────────────────

    [Fact]
    public void Load_SettingsWrittenBeforeTheAppearanceFields_GetsTheDefaults()
    {
        var settings = LoadJson("""
            { "Theme": { "ThemeName": "Light", "UseSystemTheme": false, "Language": "ru" }, "SplitVertical": true }
            """);

        settings.Theme.ThemeName.Should().Be("Light");
        settings.Theme.AccentColor.Should().BeEmpty();
        settings.Theme.PanelFontFamily.Should().BeEmpty();
        settings.Theme.PanelFontSize.Should().Be(AppearanceResources.DefaultPanelFontSize);
        settings.Layout.Should().BeEquivalentTo(new LayoutSettings());
        settings.Layout.ShowFunctionKeyBar.Should().BeTrue();
        settings.Layout.ShowStatusBar.Should().BeTrue();
    }

    [Fact]
    public void Load_NullAppearanceValues_AreNormalizedToTheDefaults()
    {
        var settings = LoadJson("""
            { "Layout": null, "Theme": { "AccentColor": null, "PanelFontFamily": null } }
            """);

        settings.Layout.Should().BeEquivalentTo(new LayoutSettings());
        settings.Theme.AccentColor.Should().BeEmpty();
        settings.Theme.PanelFontFamily.Should().BeEmpty();
    }

    [Fact]
    public void SaveThenLoad_AppearanceValues_SurviveTheRoundTrip()
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = "#C42B1C";
        settings.Theme.PanelFontFamily = "Consolas";
        settings.Theme.PanelFontSize = 15;
        settings.Layout.ShowDriveBar = false;
        settings.Layout.ShowViewToolbar = false;

        Serializer().Save(settings).Should().BeTrue();
        var loaded = Serializer().Load();

        loaded.Theme.AccentColor.Should().Be("#C42B1C");
        loaded.Theme.PanelFontFamily.Should().Be("Consolas");
        loaded.Theme.PanelFontSize.Should().Be(15);
        loaded.Layout.ShowDriveBar.Should().BeFalse();
        loaded.Layout.ShowViewToolbar.Should().BeFalse();
        loaded.Layout.ShowTabBar.Should().BeTrue();
    }

    // ── Font size ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 8)]
    [InlineData(7.4, 8)]
    [InlineData(12, 12)]
    [InlineData(12.5, 13)]
    [InlineData(23.6, 24)]
    [InlineData(300, 24)]
    [InlineData(-5, 8)]
    public void ClampFontSize_AnySize_IsAWholeSizeWithinTheBounds(double size, double expected) =>
        AppearanceResources.ClampFontSize(size).Should().Be(expected);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ClampFontSize_NotANumber_FallsBackToTheDefault(double size) =>
        AppearanceResources.ClampFontSize(size).Should().Be(AppearanceResources.DefaultPanelFontSize);

    // ── What is laid over the theme ───────────────────────────────────────────

    [Fact]
    public void Build_DefaultSettings_ShowsEverythingAndKeepsTheThemeAccentAndFont()
    {
        var resources = AppearanceResources.Build(new AppSettings());

        resources[AppearanceResources.PanelFontSizeKey].Should().Be(AppearanceResources.DefaultPanelFontSize);
        foreach (var key in VisibilityKeys)
        {
            resources[key].Should().Be(Visibility.Visible, key);
        }

        resources.Should().NotContainKey(AppearanceResources.AccentColorKey);
        resources.Should().NotContainKey(AppearanceResources.AccentBrushKey);
        resources.Should().NotContainKey(AppearanceResources.AccentForegroundBrushKey);
        resources.Should().NotContainKey(AppearanceResources.PanelFontFamilyKey);
    }

    [Fact]
    public void Build_HiddenElements_AreCollapsedAndTheRestStayVisible()
    {
        var settings = new AppSettings();
        settings.Layout.ShowFunctionKeyBar = false;
        settings.Layout.ShowPathBar = false;

        var resources = AppearanceResources.Build(settings);

        resources[AppearanceResources.FunctionKeyBarVisibilityKey].Should().Be(Visibility.Collapsed);
        resources[AppearanceResources.PathBarVisibilityKey].Should().Be(Visibility.Collapsed);
        resources[AppearanceResources.DriveBarVisibilityKey].Should().Be(Visibility.Visible);
        resources[AppearanceResources.TabBarVisibilityKey].Should().Be(Visibility.Visible);
        resources[AppearanceResources.ViewToolbarVisibilityKey].Should().Be(Visibility.Visible);
        resources[AppearanceResources.StatusBarVisibilityKey].Should().Be(Visibility.Visible);
    }

    [Fact]
    public void Build_ValidAccent_ReplacesTheAccentColourBrushAndItsText()
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = "c42b1c";   // forgiving spelling, as a workspace colour

        var resources = AppearanceResources.Build(settings);

        var expected = Color.FromRgb(0xC4, 0x2B, 0x1C);
        resources[AppearanceResources.AccentColorKey].Should().Be(expected);
        var brush = resources[AppearanceResources.AccentBrushKey].Should().BeOfType<SolidColorBrush>().Subject;
        brush.Color.Should().Be(expected);
        brush.IsFrozen.Should().BeTrue("the brush is shared by every element that reads the theme");
        resources[AppearanceResources.AccentForegroundBrushKey].Should().BeOfType<SolidColorBrush>()
            .Which.Color.Should().Be(Colors.White);
    }

    // The theme tokens the selection is fitted to (Themes/Dark.xaml, Themes/Light.xaml).
    private static readonly AppearanceResources.ThemeTones DarkTones = new(Rgb("#213324"), Rgb("#CFD1D5"));
    private static readonly AppearanceResources.ThemeTones LightTones = new(Rgb("#CAE7CF"), Rgb("#282B30"));

    private static Color Rgb(string hex)
    {
        WorkspaceColor.TryParseRgb(hex, out var r, out var g, out var b).Should().BeTrue(hex);
        return Color.FromRgb(r, g, b);
    }

    [Fact]
    public void Build_ValidAccentAndThemeTones_TintsTheSelectionWithTheAccent()
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = "#0063B1";

        var resources = AppearanceResources.Build(settings, DarkTones);

        var selection = resources[AppearanceResources.SelectionColorKey].Should().BeOfType<Color>().Subject;
        selection.B.Should().BeGreaterThan(selection.G, "a blue accent gives a blue slab, not the theme's green");
        resources[AppearanceResources.SelectionBrushKey].Should().BeOfType<SolidColorBrush>()
            .Which.Color.Should().Be(selection);
    }

    [Fact]
    public void Build_NoAccentOrNoThemeTones_LeavesTheThemeSelection()
    {
        AppearanceResources.Build(new AppSettings(), DarkTones).Should().NotContainKey(AppearanceResources.SelectionBrushKey);

        var settings = new AppSettings();
        settings.Theme.AccentColor = "#0063B1";
        AppearanceResources.Build(settings).Should().NotContainKey(AppearanceResources.SelectionBrushKey);
    }

    [Theory]
    [InlineData("#8DD86D", "#213324")]   // the dark theme's own accent gives back its own slab
    [InlineData("#177225", "#CAE7CF")]   // and the light theme's
    public void SelectionFor_TheThemeAccent_IsTheThemeSelection(string accent, string selection)
    {
        var tones = selection == "#213324" ? DarkTones : LightTones;

        var result = AppearanceResources.SelectionFor(Rgb(accent), tones.Selection, tones.Foreground);

        var expected = Rgb(selection);
        Math.Abs(result.R - expected.R).Should().BeLessThanOrEqualTo(6);
        Math.Abs(result.G - expected.G).Should().BeLessThanOrEqualTo(6);
        Math.Abs(result.B - expected.B).Should().BeLessThanOrEqualTo(6);
    }

    [Theory]
    [InlineData("#0063B1")]   // blue
    [InlineData("#000080")]   // navy: mixed into a dark panel it would vanish
    [InlineData("#FFD700")]   // yellow: mixed into a light panel it would glare
    [InlineData("#C42B1C")]   // red
    [InlineData("#E3008C")]   // magenta
    [InlineData("#808080")]   // grey: no hue at all
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    public void SelectionFor_AnyAccent_KeepsTheTextReadableAndTheSlabVisible(string accent)
    {
        foreach (var (tones, background) in new[] { (DarkTones, Rgb("#0E0F11")), (LightTones, Rgb("#F8F8FA")) })
        {
            var result = AppearanceResources.SelectionFor(Rgb(accent), tones.Selection, tones.Foreground);

            AppearanceResources.Contrast(tones.Foreground, result).Should()
                .BeGreaterThanOrEqualTo(AppearanceResources.MinSelectionTextContrast, $"{accent} on {background}");
            AppearanceResources.Contrast(result, background).Should().BeGreaterThan(
                AppearanceResources.Contrast(tones.Selection, background) * 0.85,
                $"{accent}: the slab stands out from the panel as much as the theme's does");
        }
    }

    [Theory]
    [InlineData("blue")]
    [InlineData("#12345")]
    [InlineData("#GGHHII")]
    public void Build_UnparsableAccent_LeavesTheThemeAccent(string accent)
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = accent;

        AppearanceResources.Build(settings).Should().NotContainKey(AppearanceResources.AccentBrushKey);
    }

    [Fact]
    public void Build_PanelFontAndSize_AreTrimmedAndClamped()
    {
        var settings = new AppSettings();
        settings.Theme.PanelFontFamily = "  Consolas ";
        settings.Theme.PanelFontSize = 99;

        var resources = AppearanceResources.Build(settings);

        resources[AppearanceResources.PanelFontFamilyKey].Should().BeOfType<FontFamily>()
            .Which.Source.Should().Be("Consolas");
        resources[AppearanceResources.PanelFontSizeKey].Should().Be(AppearanceResources.MaxPanelFontSize);
    }

    [Theory]
    [InlineData("#")]
    [InlineData("a#b")]
    [InlineData(",")]
    [InlineData("Not Installed Anywhere")]
    public void Build_HandEditedFontName_DoesNotThrow(string family)
    {
        var settings = new AppSettings();
        settings.Theme.PanelFontFamily = family;

        // Runs at startup, before the window: a throw here would be a crash on launch.
        var act = () => AppearanceResources.Build(settings);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("#FFC000", "#FF000000")]   // amber: white text would vanish
    [InlineData("#FFFFFF", "#FF000000")]
    [InlineData("#8BC34A", "#FF000000")]   // pale green
    [InlineData("#0063B1", "#FFFFFFFF")]
    [InlineData("#000000", "#FFFFFFFF")]
    public void ForegroundOn_Accent_PicksTheMoreReadableOfWhiteAndBlack(string accent, string expected)
    {
        WorkspaceColor.TryParseRgb(accent, out var r, out var g, out var b).Should().BeTrue();

        AppearanceResources.ForegroundOn(Color.FromRgb(r, g, b)).ToString().Should().Be(expected);
    }

    // ── The Settings dialog ───────────────────────────────────────────────────

    private SettingsDialogViewModel Dialog(AppSettings settings) => new(settings, Serializer());

    [Fact]
    public void AccentPresets_Every_KeepsWhiteTextAtAaContrast()
    {
        var presets = Dialog(new AppSettings()).AccentOptions.Where(o => !string.IsNullOrEmpty(o.Hex)).ToList();

        presets.Should().NotBeEmpty();
        foreach (var preset in presets)
        {
            WorkspaceColor.TryParseRgb(preset.Hex, out var r, out var g, out var b).Should().BeTrue(preset.Hex);
            var color = Color.FromRgb(r, g, b);
            var contrastWithWhite = 1.05 / (AppearanceResources.RelativeLuminance(color) + 0.05);

            contrastWithWhite.Should().BeGreaterThanOrEqualTo(4.5, preset.Hex);
            AppearanceResources.ForegroundOn(color).Should().Be(Colors.White, preset.Hex);
        }
    }

    [Fact]
    public void Open_SavedAppearance_IsWhatTheDialogShows()
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = "#107C10";
        settings.Theme.PanelFontFamily = "Consolas";
        settings.Theme.PanelFontSize = 14;
        settings.Layout.ShowStatusBar = false;
        settings.ShowWorkspaceSidebar = true;

        var vm = Dialog(settings);

        vm.AccentColor.Should().Be("#107C10");
        vm.SelectedAccent!.Hex.Should().Be("#107C10");
        vm.PanelFontFamily.Should().Be("Consolas");
        vm.PanelFontSize.Should().Be(14);
        vm.ShowStatusBar.Should().BeFalse();
        vm.ShowDriveBar.Should().BeTrue();
        vm.ShowWorkspaceSidebar.Should().BeTrue();
    }

    [Fact]
    public void Open_DefaultSettings_SelectsTheThemeAccent()
    {
        var vm = Dialog(new AppSettings());

        vm.SelectedAccent.Should().BeSameAs(vm.AccentOptions[0]);
        vm.SelectedAccent!.Hex.Should().BeEmpty();
        vm.AccentError.Should().BeEmpty();
    }

    [Fact]
    public void SelectedAccent_Preset_FillsTheHexBox()
    {
        var vm = Dialog(new AppSettings());
        var red = vm.AccentOptions.Single(o => o.Hex == "#C42B1C");

        vm.SelectedAccent = red;

        vm.AccentColor.Should().Be("#C42B1C");
    }

    [Fact]
    public void AccentColor_TypedPresetValueInAnySpelling_SelectsThatPreset()
    {
        var vm = Dialog(new AppSettings());

        vm.AccentColor = "0063b1";

        vm.SelectedAccent!.Hex.Should().Be("#0063B1");
    }

    [Theory]
    [InlineData("#123456")]
    [InlineData("not a colour")]
    public void AccentColor_TypedNonPresetValue_SelectsCustom(string typed)
    {
        var vm = Dialog(new AppSettings());

        vm.AccentColor = typed;

        vm.SelectedAccent!.IsCustom.Should().BeTrue();
    }

    [Fact]
    public void SelectedAccent_Custom_LeavesTheTypedColourAlone()
    {
        var vm = Dialog(new AppSettings());
        vm.AccentColor = "#0063B1";

        vm.SelectedAccent = vm.AccentOptions.Single(o => o.IsCustom);

        vm.AccentColor.Should().Be("#0063B1");
        vm.SelectedAccent!.IsCustom.Should().BeTrue();
    }

    [Fact]
    public void Save_AppearanceAndLayout_AreWrittenNormalized()
    {
        var settings = new AppSettings();
        var vm = Dialog(settings);

        vm.AccentColor = " c42b1c ";
        vm.PanelFontFamily = "  Consolas ";
        vm.PanelFontSize = 40;
        vm.ShowTabBar = false;
        vm.ShowFunctionKeyBar = false;
        vm.ShowWorkspaceSidebar = true;
        vm.SaveCommand.Execute(null);

        vm.SavedSuccessfully.Should().BeTrue();
        settings.Theme.AccentColor.Should().Be("#C42B1C");
        settings.Theme.PanelFontFamily.Should().Be("Consolas");
        settings.Theme.PanelFontSize.Should().Be(AppearanceResources.MaxPanelFontSize);
        settings.Layout.ShowTabBar.Should().BeFalse();
        settings.Layout.ShowFunctionKeyBar.Should().BeFalse();
        settings.Layout.ShowStatusBar.Should().BeTrue();
        settings.ShowWorkspaceSidebar.Should().BeTrue();
        Serializer().Load().Theme.AccentColor.Should().Be("#C42B1C");
    }

    [Fact]
    public void Save_ThemeDefaultAccent_StoresEmpty()
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = "#107C10";
        var vm = Dialog(settings);

        vm.SelectedAccent = vm.AccentOptions[0];
        vm.SaveCommand.Execute(null);

        settings.Theme.AccentColor.Should().BeEmpty();
    }

    [Fact]
    public void Save_UnparsableAccent_KeepsTheDialogOpenAndTheSettingsUntouched()
    {
        var settings = new AppSettings();
        settings.Theme.AccentColor = "#107C10";
        var vm = Dialog(settings);

        vm.AccentColor = "blue";
        vm.ShowTabBar = false;
        vm.SaveCommand.Execute(null);

        vm.SavedSuccessfully.Should().BeFalse();
        vm.SaveError.Should().NotBeEmpty();
        vm.AccentError.Should().NotBeEmpty();
        settings.Theme.AccentColor.Should().Be("#107C10");
        settings.Layout.ShowTabBar.Should().BeTrue();
        File.Exists(Path.Combine(_dir, "settings.json")).Should().BeFalse();
    }

    [Fact]
    public void BuildFontList_SavedFamilyNotInstalled_IsKeptAndTheListIsSorted()
    {
        var list = SettingsDialogViewModel.BuildFontList(["Verdana", "Arial", "arial", " "], "Fira Code", "Default");

        list.Select(f => f.Name).Should().Equal("", "Arial", "Fira Code", "Verdana");
        list[0].DisplayName.Should().Be("Default");
    }

    [Fact]
    public void BuildFontList_SavedFamilyInstalled_IsNotListedTwice()
    {
        var list = SettingsDialogViewModel.BuildFontList(["Consolas", "Arial"], "consolas", "Default");

        list.Select(f => f.Name).Should().Equal("", "Arial", "Consolas");
    }

    [Fact]
    public void Xaml_KeysTheAppearanceSettingsReplace_AreNeverReadAsStaticResource()
    {
        // A StaticResource is resolved once, from the theme as loaded: it would keep the theme's blue
        // (or the default font, or a visible bar) whatever the user saved.
        var offenders = Directory.EnumerateFiles(Path.Combine(SourceTree.Root, "src"), "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                        && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .SelectMany(f => System.Text.RegularExpressions.Regex
                .Matches(File.ReadAllText(f), @"\{StaticResource\s+(AccentBrush|AccentForegroundBrush|Chrome\w+|PanelFont\w+)\}")
                .Select(m => $"{Path.GetFileName(f)}: {m.Value}"))
            .ToList();

        offenders.Should().BeEmpty();
    }

    private static readonly string[] VisibilityKeys =
    [
        AppearanceResources.FunctionKeyBarVisibilityKey,
        AppearanceResources.DriveBarVisibilityKey,
        AppearanceResources.TabBarVisibilityKey,
        AppearanceResources.PathBarVisibilityKey,
        AppearanceResources.ViewToolbarVisibilityKey,
        AppearanceResources.StatusBarVisibilityKey,
    ];
}
