using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using FluentAssertions;
using TCV.App.Controls;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// The picture of a workspace: which file of the project is its picture, when the project is searched,
/// how the search yields to the user's choice, and how wide the rail opened wide is.
/// </summary>
public sealed class WorkspacePictureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tcv-pic-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Touch(string relative, int bytes = 16)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string Repo()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        return _root;
    }

    // ── Which file is the project's picture ──────────────────────────────────

    [Theory]
    [InlineData(".idea/icon.svg", "logo.png")]
    [InlineData("logo.png", "icon.png")]
    [InlineData("icon.png", "public/favicon.ico")]
    [InlineData("public/favicon.svg", "public/apple-touch-icon.png")]
    [InlineData("assets/logo.svg", "src/app/deep/er/logo.png")]
    [InlineData("logo.png", "logo-banner.png")]
    [InlineData("logo.png", "tests/fixtures/logo.png")]
    [InlineData("favicon.svg", "favicon.ico")]
    [InlineData("src/App/app.ico", "src/App/other.ico")]
    public void Score_PrefersTheFirst(string better, string worse)
    {
        ProjectIconFinder.Score(better).Should().BeGreaterThan(ProjectIconFinder.Score(worse)!.Value);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/screenshot.png")]
    [InlineData("photos/cat.jpg")]
    [InlineData("logo.txt")]
    public void Score_NotAPictureOfTheProject_IsNull(string path)
    {
        ProjectIconFinder.Score(path).Should().BeNull();
    }

    [Fact]
    public void Find_TakesTheLogo_AndLeavesDependenciesAndBuildOutputAlone()
    {
        var root = Repo();
        var logo = Touch(@"src\App\Assets\Logo\app-logo.png");
        var favicon = Touch(@"web\public\favicon.ico");
        Touch(@"node_modules\left-pad\logo.png");
        Touch(@"src\App\bin\Debug\logo.png");
        Touch(@".cache\logo.png");

        var found = ProjectIconFinder.Find(root);

        found.Should().Equal(logo, favicon);
    }

    [Fact]
    public void Find_PutsThePictureTheManifestDeclaresFirst()
    {
        var root = Repo();
        Touch("logo.png");
        var declared = Touch(@"media\extension.png");
        File.WriteAllText(Path.Combine(root, "package.json"), """{ "name": "x", "icon": "media/extension.png" }""");

        ProjectIconFinder.Find(root)[0].Should().Be(declared);
    }

    [Fact]
    public void Find_SkipsEmptyFiles()
    {
        var root = Repo();
        Touch("logo.png", bytes: 0);

        ProjectIconFinder.Find(root).Should().BeEmpty();
    }

    [Fact]
    public void FindRepositoryRoot_WalksUpToTheFolderWithGit()
    {
        var root = Repo();
        var deep = Path.Combine(root, "src", "lib");
        Directory.CreateDirectory(deep);

        ProjectIconFinder.FindRepositoryRoot(deep).Should().Be(root);
    }

    [Fact]
    public void FindRepositoryRoot_TakesAWorktreesGitFile()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, ".git"), "gitdir: elsewhere");

        ProjectIconFinder.FindRepositoryRoot(_root).Should().Be(_root);
    }

    // ── When the project is searched ─────────────────────────────────────────

    [Fact]
    public void AWorkspaceFromBefore_WantsItsPicture()
    {
        var old = JsonSerializer.Deserialize<Workspace>("""{ "Name": "api", "Color": "#4F9CF9" }""")!;

        old.IconImage.Should().BeEmpty();
        old.AutoIconImage.Should().BeTrue();
        WorkspacePicture.WantsSearch(old).Should().BeTrue();
    }

    [Theory]
    [InlineData("🚀", "", true)]
    [InlineData("", "a1.png", true)]
    [InlineData("", "", false)]
    public void AWorkspaceWithAnEmojiAPictureOrADecision_IsNotSearched(string icon, string image, bool auto)
    {
        WorkspacePicture.WantsSearch(new Workspace { Icon = icon, IconImage = image, AutoIconImage = auto })
            .Should().BeFalse();
    }

    [Fact]
    public void ProjectFolder_IsTheActiveTabOfThePanelThatHadFocus_NeverAServer()
    {
        var ws = new Workspace
        {
            ActiveLeft = false,
            Left = new WorkspacePanelState { Tabs = { new TabState { Path = @"C:\left" } } },
            Right = new WorkspacePanelState
            {
                ActiveTabIndex = 1,
                Tabs = { new TabState { Path = @"C:\other" }, new TabState { Path = "sftp://host/srv" } },
            },
        };

        WorkspacePicture.ProjectFolder(ws).Should().Be(@"C:\left");

        ws.Right.ActiveTabIndex = 0;
        WorkspacePicture.ProjectFolder(ws).Should().Be(@"C:\other");
    }

    [Fact]
    public void ActivePanelFolder_IsOnlyTheActiveTabOfThePanelThatHadFocus()
    {
        // The terminal of a workspace not on screen: a server tab there is no folder, and the other
        // panel's folder is somewhere else, not a stand-in.
        var ws = new Workspace
        {
            ActiveLeft = false,
            Left = new WorkspacePanelState { Tabs = { new TabState { Path = @"C:\left" } } },
            Right = new WorkspacePanelState
            {
                ActiveTabIndex = 1,
                Tabs = { new TabState { Path = @"C:\other" }, new TabState { Path = "sftp://host/srv" } },
            },
        };

        WorkspacePicture.ActivePanelFolder(ws).Should().BeNull();

        ws.Right.ActiveTabIndex = 0;
        WorkspacePicture.ActivePanelFolder(ws).Should().Be(@"C:\other");

        ws.Right.ActiveTabIndex = 5;
        WorkspacePicture.ActivePanelFolder(ws).Should().BeNull();
    }

    [Fact]
    public void SetFoundPicture_YieldsToAChoiceMadeMeanwhile()
    {
        Directory.CreateDirectory(_root);
        var store = new WorkspaceStore(Path.Combine(_root, "workspaces.json"));
        store.Save(new Workspace { Name = "bare" });
        store.Save(new Workspace { Name = "decided", AutoIconImage = false });
        store.Save(new Workspace { Name = "emoji", Icon = "🔧" });

        store.SetFoundPicture("bare", "found.png").Should().BeTrue();
        store.SetFoundPicture("decided", "found.png").Should().BeFalse();
        store.SetFoundPicture("emoji", "found.png").Should().BeFalse();

        store.Get("bare")!.IconImage.Should().Be("found.png");
        store.Get("decided")!.IconImage.Should().BeEmpty();
    }

    [Fact]
    public void CopyIdentityFrom_KeepsThePicture()
    {
        var copy = new Workspace();
        copy.CopyIdentityFrom(new Workspace { IconImage = "a1.png", AutoIconImage = false });

        copy.IconImage.Should().Be("a1.png");
        copy.AutoIconImage.Should().BeFalse();
    }

    // ── The properties window ────────────────────────────────────────────────

    [Fact]
    public void PropertiesWindow_RemovingThePicture_StopsTheSearch()
    {
        var vm = new WorkspacePropertiesDialogViewModel(
            new Workspace { Name = "api", IconImage = "a1.png" }, ["api"], [], pictures: Pictures());

        vm.RemovePictureCommand.Execute(null);
        vm.ConfirmCommand.Execute(null);

        vm.Result!.IconImage.Should().BeEmpty();
        vm.Result.AutoIconImage.Should().BeFalse();
    }

    [Fact]
    public void PropertiesWindow_AnEmojiIsAChoiceToo()
    {
        var vm = new WorkspacePropertiesDialogViewModel(new Workspace { Name = "api" }, ["api"], []);

        vm.PickIconCommand.Execute("🚀");
        vm.ConfirmCommand.Execute(null);

        vm.Result!.AutoIconImage.Should().BeFalse();
    }

    [Fact]
    public void PropertiesWindow_LeftAlone_KeepsThePictureAndTheSearch()
    {
        var vm = new WorkspacePropertiesDialogViewModel(new Workspace { Name = "api", IconImage = "a1.png" }, ["api"], []);

        vm.ConfirmCommand.Execute(null);

        vm.Result!.IconImage.Should().Be("a1.png");
        vm.Result.AutoIconImage.Should().BeTrue();
        vm.CanChoosePicture.Should().BeFalse();   // opened without a store: no picture row
    }

    [Fact]
    public void Store_DrawsAPictureIntoASquare_AndKeepsItByContent()
    {
        var store = new WorkspaceIconStore(Path.Combine(_root, "icons"));
        var source = Path.Combine(_root, "wide.png");
        Directory.CreateDirectory(_root);
        var pixels = new byte[40 * 10 * 4];
        Array.Fill(pixels, (byte)200);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(40, 10, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32, null, pixels, 40 * 4)));
        using (var file = File.Create(source))
        {
            encoder.Save(file);
        }

        var drawn = WorkspaceIconStore.RenderAsync(source).GetAwaiter().GetResult();
        var name = store.SaveAsync(drawn!.Bitmap).GetAwaiter().GetResult();
        var again = store.SaveAsync(drawn.Bitmap).GetAwaiter().GetResult();

        drawn.Bitmap.PixelWidth.Should().Be(WorkspaceIconStore.PixelSize);
        drawn.Bitmap.PixelHeight.Should().Be(WorkspaceIconStore.PixelSize);
        drawn.Aspect.Should().Be(4);
        drawn.IsSquarish.Should().BeFalse("a 40×10 wordmark is not taken by the search");
        name.Should().NotBeNull().And.Be(again);
        store.Load(name).Should().NotBeNull();
        store.Load("missing.png").Should().BeNull();
    }

    [Fact]
    public void Store_ANonPicture_IsNotDrawn()
    {
        var notAPicture = Touch("logo.png");

        WorkspaceIconStore.RenderAsync(notAPicture).GetAwaiter().GetResult().Should().BeNull();
    }

    [Fact]
    public void Store_Sweep_LeavesPicturesInUseAndYoungOnes()
    {
        var folder = Path.Combine(_root, "icons");
        Directory.CreateDirectory(folder);
        var used = Path.Combine(folder, "used.png");
        var orphan = Path.Combine(folder, "orphan.png");
        var young = Path.Combine(folder, "young.png");
        foreach (var file in new[] { used, orphan, young })
        {
            File.WriteAllBytes(file, [1]);
        }

        File.SetLastWriteTimeUtc(used, DateTime.UtcNow.AddDays(-3));
        File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-3));

        new WorkspaceIconStore(folder).Sweep(["used.png"], TimeSpan.FromDays(1));

        File.Exists(used).Should().BeTrue();
        File.Exists(young).Should().BeTrue();
        File.Exists(orphan).Should().BeFalse();
    }

    private WorkspacePictureContext Pictures() =>
        new(new WorkspaceIconStore(Path.Combine(_root, "icons")), ProjectFolder: null, PickFile: _ => null);

    // ── The width of the rail opened wide ────────────────────────────────────

    [Theory]
    [InlineData(0, 258, 1000, 258)]    // automatic: reaches the side bar's edge
    [InlineData(0, 64, 1000, 240)]     // no side bar: room for a long name
    [InlineData(320, 258, 1000, 320)]  // as dragged
    [InlineData(90, 258, 1000, 160)]   // never narrower than the least
    [InlineData(900, 258, 1000, 600)]  // nor wider than the most
    [InlineData(500, 258, 400, 400)]   // nor wider than the window gives
    public void RailWidth_Expanded(double stored, double edge, double room, double expected)
    {
        RailWidth.Expanded(stored, edge, room).Should().Be(expected);
    }

    [Theory]
    [InlineData(300, 258, 300, 300)]   // a width of its own is kept
    [InlineData(263, 258, 258, 0)]     // near the side bar's edge it sticks, and follows it again
    [InlineData(240, 64, 240, 0)]      // let go on the automatic width
    [InlineData(100, 258, 160, 160)]   // held at the least
    public void RailWidth_Dragged(double raw, double edge, double width, double stored)
    {
        RailWidth.Dragged(raw, edge, 1000).Should().Be((width, stored));
    }
}
