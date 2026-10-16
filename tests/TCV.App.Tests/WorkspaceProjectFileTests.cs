using System.IO;
using FluentAssertions;
using TCV.App.Settings;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

public sealed class WorkspaceProjectFileTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"tcv-proj-{Guid.NewGuid():N}");

    public WorkspaceProjectFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string ProjectPath => Path.Combine(_dir, "demo.tcvproj");

    private Workspace Sample() => new()
    {
        Name = "demo",
        Icon = "🚀",
        Color = "#4F9CF9",
        Group = "Work",
        SplitVertical = true,
        ActiveLeft = false,
        TerminalCommand = "npm run dev",
        TerminalDirectory = Path.Combine(_dir, "app"),
        Left = new WorkspacePanelState
        {
            ActiveTabIndex = 1,
            Tabs =
            {
                new TabState { Path = Path.Combine(_dir, "docs") },
                new TabState { Path = Path.Combine(_dir, "src") },
            },
        },
        Right = new WorkspacePanelState
        {
            Tabs = { new TabState { Path = Path.Combine(_dir, "tests") } },
        },
    };

    [Fact]
    public void RoundTrip_PreservesIdentityAndRoots()
    {
        WorkspaceProjectFile.Save(ProjectPath, WorkspaceProjectFile.FromWorkspace(Sample(), ProjectPath));

        var loaded = WorkspaceProjectFile.Load(ProjectPath);
        loaded.Should().NotBeNull();

        var ws = WorkspaceProjectFile.ToWorkspace(loaded!, ProjectPath);
        ws.Name.Should().Be("demo");
        ws.Icon.Should().Be("🚀");
        ws.Color.Should().Be("#4F9CF9");
        ws.Group.Should().Be("Work");
        ws.SplitVertical.Should().BeTrue();
        ws.ActiveLeft.Should().BeFalse();
        ws.TerminalCommand.Should().Be("npm run dev");
        ws.TerminalDirectory.Should().Be(Path.Combine(_dir, "app"));
    }

    [Fact]
    public void ActiveTabPath_BecomesTheRoot_NotTheFirstTab()
    {
        // The definition records where the project starts; "src" was the active tab, "docs" was not.
        var def = WorkspaceProjectFile.FromWorkspace(Sample(), ProjectPath);

        def.LeftRoot.Should().Be("src");
        def.RightRoot.Should().Be("tests");
    }

    [Fact]
    public void PathsAreStoredRelative_WithForwardSlashes()
    {
        var def = WorkspaceProjectFile.FromWorkspace(Sample(), ProjectPath);

        // A committed file has to read cleanly in a diff and resolve on any checkout directory.
        def.LeftRoot.Should().Be("src");
        def.TerminalDirectory.Should().Be("app");
        Path.IsPathRooted(def.LeftRoot).Should().BeFalse();
    }

    [Fact]
    public void NestedPaths_UseForwardSlashes()
    {
        var ws = new Workspace
        {
            Name = "nested",
            Left = new WorkspacePanelState
            {
                Tabs = { new TabState { Path = Path.Combine(_dir, "src", "app", "core") } },
            },
        };

        WorkspaceProjectFile.FromWorkspace(ws, ProjectPath).LeftRoot.Should().Be("src/app/core");
    }

    [Fact]
    public void RelativeRoots_ResolveAgainstTheProjectFile_NotTheCurrentDirectory()
    {
        // This is the whole point of the definition layer: clone anywhere, open, and it works.
        var definition = new WorkspaceDefinition { Name = "demo", LeftRoot = "src" };

        var ws = WorkspaceProjectFile.ToWorkspace(definition, ProjectPath);

        ws.Left.Tabs.Should().ContainSingle();
        ws.Left.Tabs[0].Path.Should().Be(Path.Combine(_dir, "src"));
    }

    [Fact]
    public void MovingTheProjectFile_Repoints_TheRoots()
    {
        var otherDir = Path.Combine(_dir, "checkout-2");
        Directory.CreateDirectory(otherDir);
        var definition = new WorkspaceDefinition { Name = "demo", LeftRoot = "src" };

        var ws = WorkspaceProjectFile.ToWorkspace(definition, Path.Combine(otherDir, "demo.tcvproj"));

        ws.Left.Tabs[0].Path.Should().Be(Path.Combine(otherDir, "src"));
    }

    [Fact]
    public void ParentRelativePaths_Survive()
    {
        var sub = Path.Combine(_dir, "tools");
        Directory.CreateDirectory(sub);
        var ws = new Workspace
        {
            Name = "up",
            Left = new WorkspacePanelState { Tabs = { new TabState { Path = _dir } } },
        };

        var def = WorkspaceProjectFile.FromWorkspace(ws, Path.Combine(sub, "up.tcvproj"));
        def.LeftRoot.Should().Be("..");

        WorkspaceProjectFile.ToWorkspace(def, Path.Combine(sub, "up.tcvproj"))
            .Left.Tabs[0].Path.Should().Be(_dir);
    }

    [Fact]
    public void AbsolutePathInFile_IsKeptAbsolute()
    {
        // A root on another drive cannot be made relative — resolving it must not invent a path
        // under the project directory.
        var definition = new WorkspaceDefinition { Name = "demo", LeftRoot = @"C:\elsewhere\repo" };

        WorkspaceProjectFile.ToWorkspace(definition, ProjectPath)
            .Left.Tabs[0].Path.Should().Be(@"C:\elsewhere\repo");
    }

    [Fact]
    public void EmptyRoot_ProducesNoTabs_RatherThanATabAtTheProjectDirectory()
    {
        var ws = WorkspaceProjectFile.ToWorkspace(new WorkspaceDefinition { Name = "x" }, ProjectPath);

        ws.Left.Tabs.Should().BeEmpty();
        ws.Right.Tabs.Should().BeEmpty();
    }

    [Fact]
    public void UnnamedDefinition_TakesItsNameFromTheFile()
    {
        var ws = WorkspaceProjectFile.ToWorkspace(new WorkspaceDefinition(), ProjectPath);

        ws.Name.Should().Be("demo");
    }

    [Fact]
    public void MissingFile_LoadsNull()
    {
        WorkspaceProjectFile.Load(Path.Combine(_dir, "nope.tcvproj")).Should().BeNull();
    }

    [Fact]
    public void CorruptFile_LoadsNull_RatherThanThrowing()
    {
        // The file is committed and hand-editable — a bad merge must not take the app down.
        File.WriteAllText(ProjectPath, "{ not json ]");

        WorkspaceProjectFile.Load(ProjectPath).Should().BeNull();
    }

    [Fact]
    public void SessionOnlyState_DoesNotLeakIntoTheDefinition()
    {
        // ADR-0011: tab sets and pinning are local session state, not shareable project data.
        var json = File.ReadAllText(WriteSample());

        json.Should().NotContain("Tabs");
        json.Should().NotContain("Pinned");
        json.Should().NotContain("LastUsedUtc");
    }

    [Fact]
    public void Merge_KeepsTheSessionLayer_AndTakesTheSharedHalfFromTheFile()
    {
        // ADR-0011: opening a project file must not cost you the tab set and view state that the
        // definition deliberately does not carry.
        var session = Sample();
        session.Pinned = true;
        var incoming = WorkspaceProjectFile.ToWorkspace(
            new WorkspaceDefinition
            {
                Name = "demo",
                Icon = "🐛",
                Color = "#FF0000",
                Group = "OSS",
                LeftRoot = "src",
                TerminalCommand = "dotnet watch",
            },
            ProjectPath);

        var merged = WorkspaceProjectFile.MergeIntoSession(incoming, session);

        merged.Icon.Should().Be("🐛");
        merged.Color.Should().Be("#FF0000");
        merged.Group.Should().Be("OSS");
        merged.TerminalCommand.Should().Be("dotnet watch");
        merged.Pinned.Should().BeTrue();                       // local preference, not shared
        merged.Left.Tabs.Should().HaveCount(2);                // tab set survives
        merged.Left.ViewMode.Should().Be(session.Left.ViewMode);
    }

    [Fact]
    public void Merge_ActivatesTheDefinitionsRoot_ReusingAnAlreadyOpenTab()
    {
        var session = Sample();
        session.Left.ActiveTabIndex = 0;   // "docs" is active, "src" is the second tab
        var incoming = WorkspaceProjectFile.ToWorkspace(
            new WorkspaceDefinition { Name = "demo", LeftRoot = "src" }, ProjectPath);

        var merged = WorkspaceProjectFile.MergeIntoSession(incoming, session);

        merged.Left.Tabs.Should().HaveCount(2);
        merged.Left.Tabs[merged.Left.ActiveTabIndex].Path.Should().Be(Path.Combine(_dir, "src"));
    }

    [Fact]
    public void Merge_AddsTheRootAsATab_WhenItIsNotOpenYet()
    {
        // The root moved (or the file is new to this machine) — it has to become reachable without
        // throwing away the tabs that are already there.
        var session = Sample();
        var incoming = WorkspaceProjectFile.ToWorkspace(
            new WorkspaceDefinition { Name = "demo", LeftRoot = "moved" }, ProjectPath);

        var merged = WorkspaceProjectFile.MergeIntoSession(incoming, session);

        merged.Left.Tabs.Should().HaveCount(3);
        merged.Left.ActiveTabIndex.Should().Be(0);
        merged.Left.Tabs[0].Path.Should().Be(Path.Combine(_dir, "moved"));
    }

    [Fact]
    public void Merge_WithNoRootForAPanel_LeavesThatPanelAlone()
    {
        var session = Sample();
        var incoming = WorkspaceProjectFile.ToWorkspace(
            new WorkspaceDefinition { Name = "demo", LeftRoot = "src" }, ProjectPath);

        var merged = WorkspaceProjectFile.MergeIntoSession(incoming, session);

        merged.Right.Tabs.Should().ContainSingle();
        merged.Right.Tabs[0].Path.Should().Be(Path.Combine(_dir, "tests"));
    }

    private string WriteSample()
    {
        WorkspaceProjectFile.Save(ProjectPath, WorkspaceProjectFile.FromWorkspace(Sample(), ProjectPath));
        return ProjectPath;
    }
}
