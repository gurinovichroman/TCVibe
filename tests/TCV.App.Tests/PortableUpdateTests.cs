using System.IO;
using FluentAssertions;
using TCV.App.Services;
using TCV.App.Settings;

namespace TCV.App.Tests;

/// <summary>
/// The first start of a portable copy unpacked over a pre-Velopack folder (ADR-0031): the old state
/// lies in the root, next to the old binaries, and is copied into data\ — once, without overwriting.
/// </summary>
public sealed class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tcv-legacy-{Guid.NewGuid():N}");
    private string Data => Path.Combine(_root, "data");

    public LegacyDataMigrationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private void Old(string relative, string content = "old")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Run_OldStateInTheRoot_IsCopiedIntoData()
    {
        Old("settings.json");
        Old("settings.json.bak");
        Old("workspaces.json");
        Old("connections.json");
        Old(Path.Combine("workspaces", "Backend.md"), "notes");
        Old(Path.Combine("plugin-settings", "tools", "settings.json"));

        var copied = LegacyDataMigration.Run(_root, Data);

        copied.Should().Be(6, "four files and two folders");
        File.ReadAllText(Path.Combine(Data, "workspaces", "Backend.md")).Should().Be("notes");
        File.Exists(Path.Combine(Data, "settings.json.bak")).Should().BeTrue();
        File.Exists(Path.Combine(Data, "plugin-settings", "tools", "settings.json")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "settings.json")).Should().BeTrue("the old files are copied, not moved");
    }

    [Fact]
    public void Run_DataAlreadyHasSettings_ChangesNothing()
    {
        Directory.CreateDirectory(Data);
        File.WriteAllText(Path.Combine(Data, "settings.json"), "new");
        Old("settings.json");
        Old("workspaces.json");

        LegacyDataMigration.Run(_root, Data).Should().Be(0);

        File.ReadAllText(Path.Combine(Data, "settings.json")).Should().Be("new");
        File.Exists(Path.Combine(Data, "workspaces.json")).Should().BeFalse(
            "a copy that has run in the new layout owns its data; old files must not reappear in it");
    }

    [Fact]
    public void Run_NothingOld_CopiesNothing()
    {
        LegacyDataMigration.Run(_root, Data).Should().Be(0);
    }

    [Fact]
    public void Run_FileAlreadyInData_IsNotOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(Data, "workspaces"));
        File.WriteAllText(Path.Combine(Data, "workspaces", "Backend.md"), "kept");
        Old("workspaces.json");
        Old(Path.Combine("workspaces", "Backend.md"), "old");

        LegacyDataMigration.Run(_root, Data);

        File.ReadAllText(Path.Combine(Data, "workspaces", "Backend.md")).Should().Be("kept");
        File.Exists(Path.Combine(Data, "workspaces.json")).Should().BeTrue();
    }

    [Fact]
    public void Run_BinariesAndLogsInTheRoot_AreNotCopied()
    {
        Old("settings.json");
        Old("TCV.App.dll", "binary");
        Old(Path.Combine("logs", "tcv-20261012.log"));

        LegacyDataMigration.Run(_root, Data);

        File.Exists(Path.Combine(Data, "TCV.App.dll")).Should().BeFalse();
        Directory.Exists(Path.Combine(Data, "logs")).Should().BeFalse();
    }
}

public sealed class SiblingWindowsTests
{
    [Fact]
    public void Pick_SameExeOtherProcess_IsASibling_CaseAndSlashesAside()
    {
        var processes = new (int, string?)[]
        {
            (1, @"C:\Tools\TCV\current\TCV.App.exe"),
            (2, @"c:\tools\tcv\current\tcv.app.exe"),
            (3, @"C:\Tools\TCV-old\current\TCV.App.exe"),
            (4, null),
        };

        SiblingWindows.Pick(processes, selfId: 1, @"C:\Tools\TCV\current\TCV.App.exe")
            .Should().Equal(2);
    }

    [Fact]
    public void Pick_AnotherCopyOfTheProgram_IsNotASibling()
    {
        // Two portable copies update independently: closing the other copy's windows is not ours to do.
        SiblingWindows.Pick([(7, @"D:\Copy2\current\TCV.App.exe")], selfId: 1, @"D:\Copy1\current\TCV.App.exe")
            .Should().BeEmpty();
    }
}
