using System.IO;
using FluentAssertions;
using TCV.Ai.Agents;
using TCV.App.Agents;
using TCV.App.Settings;
using TCV.App.ViewModels;
using TCV.App.Workspaces;
using TCV.Git;
using Xunit;

namespace TCV.App.Tests;

/// <summary>
/// The logic behind the window's chrome of ADR-0030: the rail's tags, the list's icons, origin marks
/// and git letters, the agent strip, the command line, and bringing old settings up to the design.
/// </summary>
public class ShellChromeTests
{
    // ── Workspace tags ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("TotalCommanderVibe", "TCV")]
    [InlineData("my-cool-web-app", "MCW")]
    [InlineData("agent-toolkit", "AT")]
    [InlineData("personal-site", "PS")]
    [InlineData("design system", "DS")]
    [InlineData("api-v2", "AV2")]
    [InlineData("Project2", "P2")]
    [InlineData("HTTPServer", "HS")]
    [InlineData("api", "API")]
    [InlineData("payments", "PAY")]
    [InlineData("x", "X")]
    [InlineData("TCV", "TCV")]
    [InlineData("  ", "?")]
    [InlineData(null, "?")]
    public void WorkspaceTag_For_TakesTheInitialsOfTheFirstThreeWords(string? name, string expected)
    {
        WorkspaceTag.For(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("TotalCommanderVibe", "Total|Commander|Vibe")]
    [InlineData("HTTPServer", "HTTP|Server")]
    [InlineData("my_cool.app", "my_|cool.|app")]
    [InlineData("agent-toolkit", "agent-toolkit")]   // WPF breaks after a hyphen itself
    [InlineData("api v2", "api v2")]
    [InlineData("TCV", "TCV")]
    [InlineData("", "")]
    public void WorkspaceTag_WithBreaks_BreaksBetweenWords_NotInsideThem(string name, string expected)
    {
        WorkspaceTag.WithBreaks(name).Should().Be(expected.Replace('|', WorkspaceTag.BreakHere));
    }

    [Theory]
    [InlineData("TotalCommanderVibe", "T")]
    [InlineData("jackpot", "J")]
    [InlineData("  ", "?")]
    public void WorkspaceTag_Monogram_IsOneLetter(string name, string expected)
    {
        WorkspaceTag.Monogram(name).Should().Be(expected);
    }

    [Fact]
    public void WorkspaceColor_PlateFor_IsTheWorkspacesOwnColour()
    {
        WorkspaceColor.PlateFor("#4f9cf9", "api").Should().Be("#4F9CF9");
    }

    [Fact]
    public void WorkspaceColor_PlateFor_WithoutAColour_IsAPresetTheNameKeeps()
    {
        var plate = WorkspaceColor.PlateFor("", "Jackpot");

        WorkspaceColor.Presets.Should().Contain(plate);
        plate.Should().NotBe(WorkspaceColor.Presets[^1], "a grey plate reads as disabled");
        WorkspaceColor.PlateFor(null, "JACKPOT").Should().Be(plate, "the same name, the same plate");
        new[] { "Picling", "RepeatMe", "Parsers", "DocPilot", "Issuecraft", "Jackpot" }
            .Select(n => WorkspaceColor.PlateFor("", n)).Distinct().Count()
            .Should().BeGreaterThan(1, "names spread over the presets");
    }

    [Theory]
    [InlineData("#F5B000", true)]    // amber: white would not read
    [InlineData("#FFFFFF", true)]
    [InlineData("#3E63DD", false)]   // indigo: white reads
    [InlineData("#E5484D", false)]
    [InlineData("#46A758", false)]   // the presets' green and blue keep white letters
    [InlineData("#0090FF", false)]
    public void WorkspaceColor_WantsDarkText_OnLightPlates(string hex, bool dark)
    {
        WorkspaceColor.TryParseRgb(hex, out var r, out var g, out var b).Should().BeTrue();
        WorkspaceColor.WantsDarkText(r, g, b).Should().Be(dark);
    }

    // ── Rail scroll fades ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 300, 300, 0, 0)]       // everything fits: no fade
    [InlineData(0, 300, 600, 0, 1)]       // at the top: only the bottom says "more"
    [InlineData(300, 300, 600, 1, 0)]     // at the bottom: only the top
    [InlineData(150, 300, 600, 1, 1)]     // in the middle: both
    [InlineData(6, 300, 600, 0.25, 1)]    // the top fade grows with what has scrolled away
    [InlineData(0, 0, 600, 0, 0)]         // not laid out yet
    public void ScrollFade_Edges_FadeAsMuchAsIsHidden_FullPastTheLength(
        double offset, double viewport, double extent, double top, double bottom)
    {
        Controls.ScrollFade.Edges(offset, viewport, extent, 24).Should().Be((top, bottom));
    }

    // ── File icons ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cs", false, FileIconKind.Code)]
    [InlineData("XAML", false, FileIconKind.Code)]
    [InlineData("json", false, FileIconKind.Code)]
    [InlineData("csproj", false, FileIconKind.Project)]
    [InlineData("props", false, FileIconKind.Project)]
    [InlineData("md", false, FileIconKind.Document)]
    [InlineData("sln", false, FileIconKind.File)]
    [InlineData("", false, FileIconKind.File)]
    [InlineData("cs", true, FileIconKind.Folder)]
    [InlineData("zip", false, FileIconKind.Archive)]
    [InlineData("7Z", false, FileIconKind.Archive)]
    [InlineData("csv", false, FileIconKind.Spreadsheet)]
    [InlineData("xlsx", false, FileIconKind.Spreadsheet)]
    [InlineData("pdf", false, FileIconKind.Pdf)]
    [InlineData("docx", false, FileIconKind.Document)]
    [InlineData("pptx", false, FileIconKind.Presentation)]
    [InlineData("png", false, FileIconKind.Image)]
    [InlineData("mp4", false, FileIconKind.Video)]
    [InlineData("flac", false, FileIconKind.Audio)]
    [InlineData("exe", false, FileIconKind.Executable)]
    [InlineData("dll", false, FileIconKind.Binary)]
    [InlineData("ps1", false, FileIconKind.Script)]
    [InlineData("env", false, FileIconKind.Config)]
    [InlineData("woff2", false, FileIconKind.Font)]
    [InlineData("key", false, FileIconKind.Key)]
    [InlineData("sqlite", false, FileIconKind.Database)]
    [InlineData("iso", false, FileIconKind.DiskImage)]
    public void FileIconClass_For_PicksTheIconByExtension(string ext, bool isDirectory, FileIconKind expected)
    {
        FileIconClass.For(ext, isDirectory).Should().Be(expected);
    }

    [Theory]
    [InlineData("pdf", false, "PDF")]
    [InlineData("docx", false, "DOCX")]
    [InlineData("sln", false, "SLN")]       // no icon of its own
    [InlineData("Bak", false, "BAK")]
    [InlineData("zip", false, null)]        // has its own icon
    [InlineData("md", false, null)]         // plain text keeps the text page
    [InlineData("cs", false, null)]
    [InlineData("backup", false, null)]     // too long for the badge
    [InlineData("", false, null)]
    [InlineData("pdf", true, null)]
    public void FileIconClass_BadgeFor_PrintsTheExtensionOfFilesWithoutAnIcon(string ext, bool isDirectory, string? expected)
    {
        FileIconClass.BadgeFor(ext, isDirectory).Should().Be(expected);
    }

    [Theory]
    [InlineData(false, "Visible", "Collapsed")]
    [InlineData(true, "Collapsed", "Visible")]
    public void AppearanceResources_SystemFileIcons_SwapsTheLineIconsForWindowsOnes(bool system, string line, string windows)
    {
        var settings = new AppSettings();
        settings.Theme.SystemFileIcons = system;

        var resources = AppearanceResources.Build(settings);

        resources[AppearanceResources.LineFileIconVisibilityKey].ToString().Should().Be(line);
        resources[AppearanceResources.SystemFileIconVisibilityKey].ToString().Should().Be(windows);
    }

    [Fact]
    public void FileIconClass_EveryKind_HasItsOwnIconInTheList()
    {
        // A kind added to the enum without a trigger in FileListControl.xaml would fall back to the plain page.
        var xaml = File.ReadAllText(Path.Combine(SourceTree.AppDirectory, "Controls", "FileListControl.xaml"));

        foreach (var kind in Enum.GetValues<FileIconKind>().Where(k => k != FileIconKind.File))
        {
            xaml.Should().Contain($"{{x:Static vm:FileIconKind.{kind}}}", "kind {0} needs its icon", kind);
        }
    }

    // ── Git letters ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(" M", "M")]
    [InlineData("M ", "M")]
    [InlineData("MM", "M")]
    [InlineData("A ", "A")]
    [InlineData("AM", "A")]
    [InlineData("??", "?")]
    [InlineData("●", "M")]
    [InlineData("UU", "U")]
    [InlineData("", "")]
    public void GitCellText_Short_IsOneLetter(string label, string expected)
    {
        GitCellText.Short(label).Should().Be(expected);
    }

    // ── Origin marks ─────────────────────────────────────────────────────────

    private const string Root = @"C:\repo";

    [Fact]
    public void FileOriginIndex_WithoutAnyAgentVerdict_MarksNothing()
    {
        var index = FileOriginIndex.Build(Root, [("a.txt", ChangeOrigin.External), ("b.txt", ChangeOrigin.Manager)]);

        index.IsEmpty.Should().BeTrue();
        index.Lookup(@"C:\repo\a.txt", false).Mark.Should().Be(FileOriginMark.None);
    }

    [Fact]
    public void FileOriginIndex_Files_CarryTheirOwnVerdict()
    {
        var index = FileOriginIndex.Build(Root,
        [
            ("src/App.cs", ChangeOrigin.Agent),
            ("README.md", ChangeOrigin.Manager),
            ("notes.md", ChangeOrigin.External),
        ]);

        index.Lookup(@"C:\repo\src\App.cs", false).Mark.Should().Be(FileOriginMark.Agent);
        index.Lookup(@"C:\repo\README.md", false).Mark.Should().Be(FileOriginMark.Human);
        index.Lookup(@"C:\repo\notes.md", false).Mark.Should().Be(FileOriginMark.Unknown);
        index.Lookup(@"C:\repo\other.md", false).Mark.Should().Be(FileOriginMark.None);
    }

    [Fact]
    public void FileOriginIndex_Folders_CarryTheStrongestMarkBelowThem()
    {
        var index = FileOriginIndex.Build(Root,
        [
            ("src/a/b.cs", ChangeOrigin.External),
            ("src/c.cs", ChangeOrigin.Agent),
            ("docs/d.md", ChangeOrigin.Manager),
        ]);

        index.Lookup(@"C:\repo\src", true).Mark.Should().Be(FileOriginMark.Agent);
        index.Lookup(@"C:\repo\src\a", true).Mark.Should().Be(FileOriginMark.Unknown);
        index.Lookup(@"C:\repo\docs", true).Mark.Should().Be(FileOriginMark.Human);
        // A file and a folder of the same name are different rows.
        index.Lookup(@"C:\repo\src", false).Mark.Should().Be(FileOriginMark.None);
    }

    [Fact]
    public void FileOriginIndex_ALoggedAgentWrite_BeatsAGuessForTheTooltip()
    {
        var index = FileOriginIndex.Build(Root, [("src/a.cs", ChangeOrigin.Agent), ("src/b.cs", ChangeOrigin.Supervised)]);

        index.Lookup(@"C:\repo\src", true).Origin.Should().Be(ChangeOrigin.Supervised);
    }

    [Fact]
    public void FileOriginIndex_OutsideTheRepository_MarksNothing()
    {
        var index = FileOriginIndex.Build(Root, [("a.txt", ChangeOrigin.Agent)]);

        index.Lookup(@"C:\repository\a.txt", false).Mark.Should().Be(FileOriginMark.None);
        index.Lookup(@"D:\a.txt", false).Mark.Should().Be(FileOriginMark.None);
    }

    // ── The agent strip ──────────────────────────────────────────────────────

    private static readonly DateTime Now = new(2026, 10, 12, 10, 0, 0, DateTimeKind.Utc);

    private static AgentChangeRow Row(string path, ChangeOrigin origin, string actor = "", DateTime? at = null) =>
        new(path, Root + "\\" + path, GitFileStatus.Modified, "M", false, origin, actor, at);

    private static AgentSession Session(string root, DateTime started, DateTime? ended, string tool = "Claude Code") =>
        new("s", tool, root, started, ended, string.Empty);

    [Fact]
    public void AgentRunSummary_FinishedSession_CountsTheFilesItIsCreditedWith()
    {
        var summary = AgentRunSummary.Build(Root,
            [Row("a.cs", ChangeOrigin.Agent), Row("b.cs", ChangeOrigin.Agent), Row("c.cs", ChangeOrigin.Manager)],
            [Session(Root, Now.AddHours(-1), Now.AddMinutes(-2))]);

        summary.Should().NotBeNull();
        summary!.Tool.Should().Be("Claude Code");
        summary.IsRunning.Should().BeFalse();
        summary.FileCount.Should().Be(2);
        summary.AtUtc.Should().Be(Now.AddMinutes(-2));
        summary.Confirmed.Should().BeFalse("a change in the session's window is a guess");
    }

    [Fact]
    public void AgentRunSummary_EveryChangeLogged_IsConfirmed()
    {
        var summary = AgentRunSummary.Build(Root,
            [Row("a.cs", ChangeOrigin.Supervised, "claude", Now)],
            []);

        summary!.Confirmed.Should().BeTrue();
        summary.Tool.Should().Be("claude");
    }

    [Fact]
    public void AgentRunSummary_FinishedSessionWithNothingLeft_IsNotShown()
    {
        AgentRunSummary.Build(Root, [Row("a.cs", ChangeOrigin.Manager)], [Session(Root, Now.AddHours(-1), Now)])
            .Should().BeNull();
    }

    [Fact]
    public void AgentRunSummary_RunningSession_IsShownBeforeItChangedAnything()
    {
        var summary = AgentRunSummary.Build(Root, [], [Session(Root + @"\src", Now.AddMinutes(-5), null)]);

        summary!.IsRunning.Should().BeTrue();
        summary.FileCount.Should().Be(0);
    }

    [Fact]
    public void AgentRunSummary_ASessionInAnotherCheckout_DoesNotCount()
    {
        AgentRunSummary.Build(Root, [], [Session(@"C:\other", Now.AddMinutes(-5), null)]).Should().BeNull();
    }

    [Theory]
    [InlineData(30, TimeAgoUnit.JustNow, 0)]
    [InlineData(120, TimeAgoUnit.Minutes, 2)]
    [InlineData(3 * 3600 + 5, TimeAgoUnit.Hours, 3)]
    [InlineData(2 * 86400, TimeAgoUnit.Days, 2)]
    public void RelativeTime_Describe_PicksTheUnit(int seconds, TimeAgoUnit unit, int count)
    {
        RelativeTime.Describe(TimeSpan.FromSeconds(seconds)).Should().Be((unit, count));
    }

    // ── Command line ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("cd src", "src")]
    [InlineData("cd ..", "..")]
    [InlineData("cd..", "..")]
    [InlineData("CD /d D:\\work", "D:\\work")]
    [InlineData("chdir \"My Folder\"", "My Folder")]
    [InlineData("D:", "D:\\")]
    public void CommandLine_ChangeDirectory_IsRecognised(string text, string expected)
    {
        CommandLine.TryParseChangeDirectory(text, out var target).Should().BeTrue();
        target.Should().Be(expected);
    }

    [Theory]
    [InlineData("cd")]
    [InlineData("dotnet build")]
    [InlineData("cdk deploy")]
    [InlineData("cd src && npm test")]
    [InlineData("cd \"My Folder\" & dir")]
    [InlineData("cd > out.txt")]
    public void CommandLine_EverythingElse_RunsInATerminal(string text)
    {
        CommandLine.TryParseChangeDirectory(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData(@"C:\repo\src", "..", @"C:\repo")]
    [InlineData(@"C:\repo", "docs", @"C:\repo\docs")]
    [InlineData(@"C:\repo", @"D:\x", @"D:\x")]
    [InlineData(@"C:\repo\src", @"\tmp", @"C:\tmp")]
    [InlineData(@"D:\repo\src", "/", @"D:\")]
    [InlineData(@"D:\repo", "src/app", @"D:\repo\src\app")]
    public void CommandLine_ResolveAgainst_JoinsRelativePaths(string folder, string target, string expected)
    {
        CommandLine.ResolveAgainst(folder, target).Should().Be(expected);
    }

    // ── Old settings meet the design ─────────────────────────────────────────

    private static List<ColumnLayout> OldDefaultColumns() =>
    [
        new() { Id = "name", Visible = true }, new() { Id = "ext", Width = 50, Visible = true },
        new() { Id = "size", Width = 80, Visible = true }, new() { Id = "date", Width = 130, Visible = true },
        new() { Id = "attr", Width = 60, Visible = true }, new() { Id = "git", Width = 44, Visible = false },
    ];

    [Fact]
    public void ApplyDesignDefaults_OldDefaults_MoveToTheDesignsOnce()
    {
        var settings = new AppSettings { DesignVersion = 0, ShowWorkspaceSidebar = false, ShowFolderTree = false };
        settings.LeftPanel.Columns = OldDefaultColumns();
        settings.Theme.PanelFontSize = 12;

        settings.ApplyDesignDefaults();

        settings.LeftPanel.Columns.Should().BeEmpty("an empty layout is the catalog's default");
        settings.Theme.PanelFontSize.Should().Be(AppearanceResources.DefaultPanelFontSize);
        settings.ShowWorkspaceSidebar.Should().BeTrue();
        settings.ShowFolderTree.Should().BeTrue();
        settings.DesignVersion.Should().Be(AppSettings.CurrentDesignVersion);

        // Once brought up, the user's later choices stay.
        settings.ShowFolderTree = false;
        settings.ApplyDesignDefaults();
        settings.ShowFolderTree.Should().BeFalse();
    }

    [Fact]
    public void ApplyDesignDefaults_ValuesTheUserChose_Stay()
    {
        var settings = new AppSettings { DesignVersion = 0 };
        var mine = new List<ColumnLayout> { new() { Id = "name", Visible = true }, new() { Id = "type", Width = 90, Visible = true } };
        settings.RightPanel.Columns = mine;
        settings.Theme.PanelFontSize = 14;

        settings.ApplyDesignDefaults();

        settings.RightPanel.Columns.Should().BeSameAs(mine);
        settings.Theme.PanelFontSize.Should().Be(14);
    }

    [Fact]
    public void DesignVersion_FreshProfile_IsCurrent_SoTheFirstSessionsChoicesStay()
    {
        var settings = new AppSettings { ShowWorkspaceSidebar = false, ShowFolderTree = false };

        settings.Normalize();
        var copy = SettingsCopy.Of(settings);

        settings.ShowWorkspaceSidebar.Should().BeFalse();
        copy.ShowWorkspaceSidebar.Should().BeFalse();
        copy.ShowFolderTree.Should().BeFalse();
    }

    [Fact]
    public void DesignVersion_FileWrittenBeforeIt_ReadsAsZero()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ShowWorkspaceSidebar\":false}")!;
        old.DesignVersion.Should().Be(0);

        old.Normalize();
        old.ShowWorkspaceSidebar.Should().BeTrue();
        old.DesignVersion.Should().Be(AppSettings.CurrentDesignVersion);
    }
}
