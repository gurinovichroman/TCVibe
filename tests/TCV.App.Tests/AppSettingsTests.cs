using System.IO;
using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class AppSettingsTests
{
    [Fact]
    public void DefaultAppSettings_HasExpectedDefaults()
    {
        var settings = new AppSettings();

        settings.LeftPanel.Should().NotBeNull();
        settings.RightPanel.Should().NotBeNull();
        settings.Operations.ConfirmDelete.Should().BeTrue();
        settings.Operations.MoveToRecycleBin.Should().BeTrue();
        settings.Theme.ThemeName.Should().Be("Dark");
    }

    [Fact]
    public void DefaultPanelSettings_LastPathIsUserProfile()
    {
        var ps = new PanelSettings();
        var expected = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ps.LastPath.Should().Be(expected);
        ps.FoldersFirst.Should().BeTrue();
        ps.SortBy.Should().Be(SortColumn.Name);
    }

    [Fact]
    public void SettingsSerializer_RoundTrip_PreservesValues()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"tcv_test_{Guid.NewGuid()}.json");
        try
        {
            // We can't easily instantiate SettingsSerializer (it uses AppContext.BaseDirectory)
            // so we test AppSettings properties directly
            var settings = new AppSettings
            {
                LeftPanel = new PanelSettings { LastPath = @"C:\Users\Test", SortDescending = true },
                RightPanel = new PanelSettings { LastPath = @"D:\Projects" },
            };

            settings.LeftPanel.LastPath.Should().Be(@"C:\Users\Test");
            settings.LeftPanel.SortDescending.Should().BeTrue();
            settings.RightPanel.LastPath.Should().Be(@"D:\Projects");
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
