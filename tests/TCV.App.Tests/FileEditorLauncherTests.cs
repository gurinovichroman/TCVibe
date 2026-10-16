using FluentAssertions;
using TCV.App.Services;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class FileEditorLauncherTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ToolFor_NoCommand_LeavesTheFileToWindows(string command)
    {
        FileEditorLauncher.ToolFor(new ExternalTool { Command = command, Arguments = "%F" }).Should().BeNull();
    }

    [Fact]
    public void ToolFor_Null_LeavesTheFileToWindows()
    {
        FileEditorLauncher.ToolFor(null).Should().BeNull();
    }

    [Theory]
    [InlineData("", "%F")]
    [InlineData("-n", "-n %F")]
    [InlineData("--wait %F", "--wait %F")]
    [InlineData("\"%F\" -multiInst", "\"%F\" -multiInst")]
    public void ToolFor_ArgumentsWithoutTheFile_GetItAtTheEnd(string arguments, string expected)
    {
        var tool = FileEditorLauncher.ToolFor(new ExternalTool { Command = "notepad++", Arguments = arguments });

        tool!.Arguments.Should().Be(expected);
    }

    [Fact]
    public void ToolFor_KeepsTheProgramAndTheTerminalSwitch()
    {
        var tool = FileEditorLauncher.ToolFor(new ExternalTool { Command = "  nvim ", InTerminal = true });

        tool!.Command.Should().Be("nvim");
        tool.InTerminal.Should().BeTrue();
    }

    [Fact]
    public void Settings_WrittenBeforeTheEditorExisted_LoadWithTheWindowsAssociation()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "ExternalTools": { "DefaultEditor": "VS Code" } }""")!;
        settings.Normalize();

        settings.ExternalTools.FileEditor.Command.Should().BeEmpty();
        settings.Window.Should().BeNull();
    }
}

public class WindowPlacementSettingsTests
{
    [Fact]
    public void IsUsable_AWindowSomeoneSized_IsRestored()
    {
        new WindowPlacementSettings { Left = 100, Top = 80, Right = 1500, Bottom = 1000 }
            .IsUsable(200, 150).Should().BeTrue();
    }

    [Fact]
    public void IsUsable_OnAMonitorLeftOfThePrimary_IsRestored()
    {
        new WindowPlacementSettings { Left = -1900, Top = 40, Right = -100, Bottom = 1000 }
            .IsUsable(200, 150).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]          // never written
    [InlineData(100, 100, 50, 900)]   // turned inside out
    [InlineData(0, 0, 120, 900)]      // narrower than any window
    [InlineData(0, 0, 90_000, 900)]   // a hand-edited typo
    public void IsUsable_Nonsense_FallsBackToTheDesignSize(int left, int top, int right, int bottom)
    {
        new WindowPlacementSettings { Left = left, Top = top, Right = right, Bottom = bottom }
            .IsUsable(200, 150).Should().BeFalse();
    }
}
