using System.IO;
using FluentAssertions;
using TCV.App.Settings;
using TCV.App.Workspaces;
using TCV.Search;

namespace TCV.App.Tests;

/// <summary>Covers the exportable settings bundle added in task 6.20.</summary>
public class SettingsBundleTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-bundle-" + Guid.NewGuid().ToString("N"));

    public SettingsBundleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private static SettingsBundle Sample()
    {
        var settings = new AppSettings();
        settings.Hotkeys["Search"] = "Ctrl+K Ctrl+F";
        settings.ColorRules.Add(new ColorRule { Pattern = "*.cs", Foreground = "#569CD6" });
        settings.LeftPanel.Columns = ColumnCatalogLayout();

        return new SettingsBundle
        {
            ExportedUtc = new DateTime(2026, 7, 27),
            Settings = settings,
            Workspaces = [new Workspace { Name = "tcv", Pinned = true }],
            SearchPresets = [new SearchPreset { Name = "todo", ContentText = "TODO" }],
        };

        static List<ColumnLayout> ColumnCatalogLayout() =>
            [new ColumnLayout { Id = "name", Width = 0, Visible = true },
             new ColumnLayout { Id = "loc", Width = 70, Visible = true }];
    }

    [Fact]
    public void ToJson_FromJson_RoundTripsEverySection()
    {
        var restored = SettingsBundle.FromJson(Sample().ToJson());

        restored.Should().NotBeNull();
        restored!.Settings!.Hotkeys["Search"].Should().Be("Ctrl+K Ctrl+F");
        restored.Settings.ColorRules.Should().ContainSingle(r => r.Pattern == "*.cs");
        restored.Settings.LeftPanel.Columns.Should().Contain(c => c.Id == "loc" && c.Visible);
        restored.Workspaces.Should().ContainSingle(w => w.Name == "tcv" && w.Pinned);
        restored.SearchPresets.Should().ContainSingle(p => p.Name == "todo");
    }

    [Fact]
    public void FromJson_Garbage_ReturnsNull()
    {
        SettingsBundle.FromJson("not json at all {").Should().BeNull();
    }

    [Fact]
    public void FromJson_JsonWithNoKnownSection_IsNotABundle()
    {
        // Most likely the user picked the wrong file; importing "nothing" would look like a no-op bug.
        SettingsBundle.FromJson("""{"somethingElse": 1}""").Should().BeNull();
    }

    [Fact]
    public void FromJson_NewerVersion_IsRejectedRatherThanHalfRead()
    {
        var json = $$$"""{"version": {{{SettingsBundle.CurrentVersion + 1}}}, "settings": {}}""";

        SettingsBundle.FromJson(json).Should().BeNull();
    }

    [Fact]
    public void FromJson_PartialBundle_KeepsTheSectionsItHas()
    {
        var restored = SettingsBundle.FromJson("""{"version": 1, "searchPresets": [{"name": "p"}]}""");

        restored.Should().NotBeNull();
        restored!.Settings.Should().BeNull();
        restored.SearchPresets.Should().ContainSingle();
    }

    [Fact]
    public void Write_Read_RoundTripsThroughAFile()
    {
        var path = Path.Combine(_dir, "export.tcvsettings");

        Sample().Write(path).Should().BeTrue();
        var restored = SettingsBundle.Read(path);

        restored.Should().NotBeNull();
        restored!.Workspaces.Should().ContainSingle();
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        SettingsBundle.Read(Path.Combine(_dir, "nope.tcvsettings")).Should().BeNull();
    }

    [Fact]
    public void Write_UnwritablePath_ReturnsFalseInsteadOfThrowing()
    {
        // A directory path is never writable as a file.
        Sample().Write(_dir).Should().BeFalse();
    }
}
