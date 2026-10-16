using System.Windows.Input;
using FluentAssertions;
using TCV.App.Commands;
using TCV.App.Converters;
using TCV.App.Settings;
using TCV.App.ViewModels;

namespace TCV.App.Tests;

/// <summary>
/// INV-05: an action is a command id in three places. The dispatcher (MainWindow.TryExecuteCommand)
/// cannot be unit-tested, but the other two — the gesture and the palette row — can, and a command
/// that is in the palette without a dispatcher case is the failure that shows up as "F1 does nothing".
/// </summary>
public class WorkspaceCommandWiringTests
{
    private static readonly string[] PaletteOnlyOrBound =
    {
        "WorkspaceProperties", "WorkspacePin",
        "WorkspaceDelete", "DuplicateTab", "CloseOtherTabs", "ToggleLockTab", "RenameTab",
        "BookmarksManager",
    };

    [Theory]
    [InlineData(1, Key.D1)] [InlineData(2, Key.D2)] [InlineData(3, Key.D3)]
    [InlineData(4, Key.D4)] [InlineData(5, Key.D5)] [InlineData(6, Key.D6)]
    [InlineData(7, Key.D7)] [InlineData(8, Key.D8)] [InlineData(9, Key.D9)]
    public void AltDigit_IsBoundToTheNthWorkspace_AndListedInThePalette(int n, Key key)
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(key, ModifierKeys.Alt).Should().Be($"Workspace{n}");
        CommandCatalog.All.Select(c => c.Id).Should().Contain($"Workspace{n}");
    }

    [Fact]
    public void NewWorkspaceAndTabCommands_AreInThePalette()
    {
        CommandCatalog.All.Select(c => c.Id).Should().Contain(PaletteOnlyOrBound);
    }

    [Fact]
    public void PropertiesAndBookmarkManager_HaveGesturesThatCollideWithNothing()
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(Key.R, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("WorkspaceProperties");
        hotkeys.GetCommandId(Key.D, ModifierKeys.Control | ModifierKeys.Alt).Should().Be("BookmarksManager");
    }

    [Fact]
    public void EveryPaletteRow_HasATitleInBothLanguages()
    {
        foreach (var command in CommandCatalog.All)
        {
            command.Title("en").Should().NotBeNullOrWhiteSpace(command.Id);
            command.Title("ru").Should().NotBeNullOrWhiteSpace(command.Id);
        }
    }
}

public class WorkspaceSidebarConverterTests
{
    [Theory]
    [InlineData("Work", "Work", true)]
    [InlineData("Work", "work", true)]
    [InlineData("Work", "Play", false)]
    [InlineData("Work", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void IsSame_ComparesWorkspaceNamesCaseInsensitively(string? name, string? active, bool expected)
    {
        WorkspaceIsActiveConverter.IsSame(name, active).Should().Be(expected);
    }

    [Fact]
    public void ActiveConverter_AnswersInTheTypeTheBoundPropertyWants()
    {
        var converter = WorkspaceIsActiveConverter.Instance;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var mine = new object[] { "Work", "work" };
        var other = new object[] { "Work", "Play" };

        converter.Convert(mine, typeof(System.Windows.Visibility), null!, culture)
            .Should().Be(System.Windows.Visibility.Visible);
        converter.Convert(other, typeof(System.Windows.Visibility), null!, culture)
            .Should().Be(System.Windows.Visibility.Hidden, "hidden keeps the names aligned");
        converter.Convert(mine, typeof(System.Windows.FontWeight), null!, culture)
            .Should().Be(System.Windows.FontWeights.SemiBold);
        converter.Convert(other, typeof(System.Windows.FontWeight), null!, culture)
            .Should().Be(System.Windows.FontWeights.Normal);
        converter.Convert(mine, typeof(bool), null!, culture).Should().Be(true);
    }

    [Fact]
    public void ColorConverter_TurnsHexIntoABrush_AndBlankOrBrokenIntoNothing()
    {
        var converter = WorkspaceColorToBrushConverter.Instance;

        var brush = converter.Convert("#4F9CF9", typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture)
            as System.Windows.Media.SolidColorBrush;
        brush.Should().NotBeNull();
        brush!.Color.R.Should().Be(0x4F);

        converter.Convert("", typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeNull();
        converter.Convert("banana", typeof(System.Windows.Media.Brush), null!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeNull();
    }
}

public class TabBranchToolTipTests
{
    // Headless tests have no string dictionary: LocalizationManager.Get returns "[key]", which is
    // enough to tell which of the two texts was chosen.

    [Fact]
    public void NoBranch_HasNoToolTip()
    {
        new TabViewModel("/C:/a").BranchToolTip.Should().BeEmpty();
    }

    [Theory]
    [InlineData("main")]
    [InlineData("feature/login")]
    [InlineData("abcdef12")]
    public void ANamedBranch_GetsTheBranchText(string branch)
    {
        TabViewModel.DescribeBranch(branch).Should().Contain("Tab_BranchTip");
    }

    [Theory]
    [InlineData("a1b2c3d")]
    [InlineData("0123456")]
    public void AnAbbreviatedCommitId_GetsTheDetachedText(string branch)
    {
        TabViewModel.DescribeBranch(branch).Should().Contain("Tab_DetachedTip");
    }

    [Fact]
    public void ChangingTheBranch_RaisesTheToolTipChange()
    {
        var tab = new TabViewModel("/C:/a");
        var raised = new List<string?>();
        tab.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        tab.Branch = "main";

        raised.Should().Contain(nameof(TabViewModel.BranchToolTip));
        tab.BranchToolTip.Should().NotBeEmpty();
    }
}

public class WorkspaceFileNameTests
{
    [Theory]
    [InlineData("api", "api")]
    [InlineData("api/v1", "api_v1")]
    [InlineData("a:b*c?", "a_b_c_")]
    [InlineData("   ", "workspace")]
    public void SafeFileName_ReplacesWhatWindowsForbids(string name, string expected)
    {
        WorkspaceCommands.SafeFileName(name).Should().Be(expected);
    }
}

public class StartupOptionsGuestTests
{
    [Fact]
    public void SingleDashName_IsAWorkspaceName_NotAFlag()
    {
        StartupOptions.Parse(new[] { "--workspace", "-x" }).WorkspaceName.Should().Be("-x");
    }

    [Fact]
    public void InlineForm_HandlesANameThatStartsWithADash()
    {
        StartupOptions.Parse(new[] { "--workspace=--weird" }).WorkspaceName.Should().Be("--weird");
    }

    [Fact]
    public void AnotherWorkspaceFlag_IsNotTakenAsTheName()
    {
        StartupOptions.Parse(new[] { "--workspace", "-w" }).IsSecondaryWindow.Should().BeFalse();
    }

    [Fact]
    public void DowngradeToPrimary_MakesEverythingThatReadsTheOptionsSeeAPrimaryWindow()
    {
        var options = StartupOptions.Parse(new[] { "--workspace", "gone" });
        options.IsSecondaryWindow.Should().BeTrue();

        options.DowngradeToPrimary();

        options.IsSecondaryWindow.Should().BeFalse("the MCP server and the settings guards read the same object");
    }
}
