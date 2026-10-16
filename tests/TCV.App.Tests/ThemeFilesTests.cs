using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TCV.App.Tests;

/// <summary>
/// Guards docs/DESIGN.md mechanically. The palette (Dark / Light) and the control styles
/// (Controls.xaml) are separate dictionaries read through <c>DynamicResource</c>, and WPF does not throw
/// for a DynamicResource key that does not exist: the colour just silently disappears. So a typo in a
/// key, or a colour added to one palette only, would show up as an invisible border, not as an error.
/// </summary>
public class ThemeFilesTests
{
    private static readonly string AppDir = SourceTree.AppDirectory;

    private static string Read(params string[] relative) =>
        File.ReadAllText(Path.Combine(new[] { AppDir }.Concat(relative).ToArray()));

    // Palette keys are plain CamelCase (BorderBrush, AccentColor): a name with an underscore, or a converter
    // a window keeps for itself, is not one, so the patterns below do not take those for palette keys.
    private static HashSet<string> Keys(string theme, string suffix) =>
        Regex.Matches(Read("Themes", theme + ".xaml"), @"x:Key=""([^""]+" + suffix + @")""")
            .Select(m => m.Groups[1].Value).ToHashSet();

    /// <summary>Every .xaml of the app except the themes themselves (which define, not use, the keys).</summary>
    private static IEnumerable<string> UsingXaml() =>
        Directory.EnumerateFiles(AppDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}")
                        && !f.EndsWith("Dark.xaml") && !f.EndsWith("Light.xaml"));

    [Fact]
    public void Palettes_DefineTheSameColours()
    {
        // The brushes are compared by LocalizationGuardTests; the Color keys they are built from too.
        Keys("Dark", "Color").Except(Keys("Light", "Color")).Should().BeEmpty();
        Keys("Light", "Color").Except(Keys("Dark", "Color")).Should().BeEmpty();
    }

    [Fact]
    public void EveryPaletteKeyUsedInXaml_IsDefined()
    {
        var defined = Keys("Dark", "Brush").Concat(Keys("Dark", "Color")).ToHashSet();
        var offenders = new List<string>();

        foreach (var file in UsingXaml())
        {
            var text = File.ReadAllText(file);

            // A window may keep a resource of its own whose name ends in Color (a converter, say).
            var local = Regex.Matches(text, @"x:Key=""(\w+)""").Select(m => m.Groups[1].Value).ToHashSet();

            foreach (Match m in Regex.Matches(text, @"(?:Dynamic|Static)Resource\s+([A-Za-z]+(?:Brush|Color))\b"))
            {
                if (!defined.Contains(m.Groups[1].Value) && !local.Contains(m.Groups[1].Value))
                {
                    offenders.Add($"{Path.GetRelativePath(AppDir, file)}: {m.Groups[1].Value}");
                }
            }
        }

        offenders.Should().BeEmpty("a missing DynamicResource key paints nothing and raises nothing (docs/DESIGN.md)");
    }

    [Fact]
    public void EveryPaletteKeyUsedInCode_IsDefined()
    {
        var defined = Keys("Dark", "Brush").Concat(Keys("Dark", "Color")).ToHashSet();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(AppDir, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            // Code looks brushes up by name (TryFindResource, SetResourceReference, a helper's key argument).
            foreach (Match m in Regex.Matches(File.ReadAllText(file), @"""([A-Za-z]+(?:Brush|Color))"""))
            {
                if (!defined.Contains(m.Groups[1].Value) && !IsAppearanceKey(m.Groups[1].Value))
                {
                    offenders.Add($"{Path.GetRelativePath(AppDir, file)}: {m.Groups[1].Value}");
                }
            }
        }

        offenders.Should().BeEmpty();
    }

    // Written over the palette at runtime (AppearanceResources), so absent from the files by design.
    private static bool IsAppearanceKey(string key) => key is "AccentColor" or "AccentBrush" or "AccentForegroundBrush";

    [Fact]
    public void ControlStyles_ReachThePaletteOnlyThroughDynamicResource()
    {
        // Controls.xaml and the palette are sibling dictionaries: a StaticResource into the palette does not
        // resolve (and one into a keyed style of its own is fine, so only the colour keys are checked).
        var text = Read("Themes", "Controls.xaml");

        Regex.Matches(text, @"StaticResource\s+([A-Za-z]+(?:Brush|Color))\b")
            .Select(m => m.Groups[1].Value)
            .Should().BeEmpty("the palette is swapped at runtime, so styles must read it with DynamicResource");
    }

    [Theory]
    [InlineData("Dark", true)]
    [InlineData("Light", false)]
    public void DialogFrame_TakesTheCaptionButtonsOfItsTheme(string theme, bool dark)
    {
        // The native caption is painted in the dialog's card colour (6.76); its buttons must be readable on it.
        var hex = Regex.Match(Read("Themes", theme + ".xaml"), @"x:Key=""CardColor"">#FF([0-9A-F]{6})<").Groups[1].Value;
        var card = System.Windows.Media.Color.FromRgb(
            Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..], 16));

        Controls.TitleBarTheme.IsDark(card).Should().Be(dark);
    }

    [Fact]
    public void DialogFrame_ColorRef_IsRedInTheLowByte() =>
        Controls.TitleBarTheme.ColorRef(System.Windows.Media.Color.FromRgb(0x12, 0x34, 0x56)).Should().Be(0x563412);
}
