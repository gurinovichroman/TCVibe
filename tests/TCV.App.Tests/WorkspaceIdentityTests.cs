using FluentAssertions;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// What has to survive re-saving the panels under an existing name (Ctrl+Alt+W): the workspace's
/// own attributes, as opposed to the layout snapshot that is being overwritten on purpose.
/// </summary>
public class WorkspaceIdentityTests
{
    private static Workspace Stored() => new()
    {
        Name = "work",
        Color = "#4F9CF9",
        Icon = "🚀",
        Group = "Work",
        Pinned = true,
        TerminalCommand = "npm run dev",
        TerminalDirectory = @"C:\src\app",
    };

    [Fact]
    public void ReSaving_KeepsAppearanceFilingAndTerminal()
    {
        var captured = new Workspace { Name = "work", SplitVertical = true };

        captured.CopyIdentityFrom(Stored());

        captured.Color.Should().Be("#4F9CF9");
        captured.Icon.Should().Be("🚀");
        captured.Group.Should().Be("Work");            // the sidebar filing must not be lost
        captured.Pinned.Should().BeTrue();
        captured.TerminalCommand.Should().Be("npm run dev");
        captured.TerminalDirectory.Should().Be(@"C:\src\app");
    }

    [Fact]
    public void ReSaving_DoesNotTouchTheCapturedLayout()
    {
        var captured = new Workspace
        {
            Name = "work",
            SplitVertical = true,
            ActiveLeft = false,
            Left = new WorkspacePanelState { Tabs = { new Settings.TabState { Path = @"C:\new" } } },
        };

        captured.CopyIdentityFrom(Stored());

        captured.SplitVertical.Should().BeTrue();
        captured.ActiveLeft.Should().BeFalse();
        captured.Left.Tabs.Should().ContainSingle();
    }

    [Fact]
    public void FirstSave_HasNothingToCopy()
    {
        var captured = new Workspace { Name = "new", Color = "#FF0000" };

        captured.CopyIdentityFrom(null);

        captured.Color.Should().Be("#FF0000");
    }
}
