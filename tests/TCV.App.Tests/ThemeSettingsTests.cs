using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class ThemeSettingsTests
{
    [Fact]
    public void DefaultThemeSettings_IsDark()
    {
        var ts = new ThemeSettings();
        ts.ThemeName.Should().Be("Dark");
        ts.UseSystemTheme.Should().BeFalse();
    }

    [Fact]
    public void ThemeSettings_LanguageDefault_IsEmpty()
    {
        var ts = new ThemeSettings();
        ts.Language.Should().BeEmpty();
    }

    [Fact]
    public void AppSettings_ThemeSettings_HasCorrectDefaults()
    {
        var settings = new AppSettings();
        settings.Theme.ThemeName.Should().Be("Dark");
        settings.Theme.Language.Should().BeEmpty();
    }
}
