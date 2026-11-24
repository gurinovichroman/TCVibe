using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;
using TCV.App.Workspaces;

namespace TCV.App.Tests;

/// <summary>
/// A workspace's picture travelling in a project file and a settings bundle, where the file may come from
/// anywhere; the store's memory that lets a menu show pictures without the disk; and what the properties
/// window marks among the pictures found.
/// </summary>
public sealed class WorkspacePictureTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tcv-pictx-{Guid.NewGuid():N}");

    public WorkspacePictureTransferTests()
    {
        Directory.CreateDirectory(_root);
        Here = new WorkspaceIconStore(HereFolder);
        There = new WorkspaceIconStore(ThereFolder);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string HereFolder => Path.Combine(_root, "data", "here");

    private string ThereFolder => Path.Combine(_root, "data", "there");

    /// <summary>This machine's store.</summary>
    private WorkspaceIconStore Here { get; }

    /// <summary>The store of the machine the file goes to.</summary>
    private WorkspaceIconStore There { get; }

    private static BitmapSource Noise(int side, int seed)
    {
        var pixels = new byte[side * side * 4];
        new Random(seed).NextBytes(pixels);
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, pixels, side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte[] Png(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>The start of a PNG saying what a header can say, whatever follows it.</summary>
    private static byte[] PngHeader(int width, int height, int length = 64)
    {
        var png = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(png, 0);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), (uint)height);
        return png;
    }

    private async Task<string> StoredPicture(int seed = 1) =>
        (await Here.SaveAsync(Noise(WorkspaceIconStore.PixelSize, seed)))!;

    private int FilesUnderRootOutsideTheStores() =>
        Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
            .Count(f => !f.StartsWith(HereFolder, StringComparison.OrdinalIgnoreCase)
                        && !f.StartsWith(ThereFolder, StringComparison.OrdinalIgnoreCase));

    // ── 6.89: the worked-out name is not stored ──────────────────────────────

    [Fact]
    public void DisplayName_IsNotWritten_AndAnOldFileWithItStillReads()
    {
        var path = Path.Combine(_root, "workspaces.json");
        File.WriteAllText(path, """[{ "Name": "api", "Icon": "🚀", "DisplayName": "🚀 stale" }]""");
        var store = new WorkspaceStore(path);

        store.Get("api")!.DisplayName.Should().Be("🚀 api", "it is worked out, the file's copy means nothing");

        store.Touch("api", DateTime.UtcNow).Should().BeTrue();

        File.ReadAllText(path).Should().NotContain("DisplayName");
        store.Get("api")!.Icon.Should().Be("🚀");
    }

    [Fact]
    public void DisplayName_IsNotWrittenToASettingsBundle()
    {
        new SettingsBundle { Workspaces = [new Workspace { Name = "api", Icon = "🚀" }] }
            .ToJson().Should().NotContain("DisplayName");
    }

    // ── There and back ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProjectFile_CarriesThePicture_ToAnotherMachine()
    {
        var name = await StoredPicture();
        var path = Path.Combine(_root, "demo.tcvproj");
        var definition = WorkspaceProjectFile.FromWorkspace(new Workspace { Name = "demo", IconImage = name }, path);
        definition.Picture = Here.ReadForTransfer(name);

        WorkspaceProjectFile.Save(path, definition);
        var imported = await There.ImportAsync(WorkspaceProjectFile.Load(path)!.Picture);

        imported.Should().Be(name, "the same picture is the same name, whichever machine it comes back from");
        There.Load(imported).Should().NotBeNull();
        File.ReadAllBytes(Path.Combine(ThereFolder, name)).Should().Equal(File.ReadAllBytes(Path.Combine(HereFolder, name)));
    }

    [Fact]
    public async Task ADrawnPicture_ComesBackUnderItsName()
    {
        // The real path: drawn into premultiplied pixels, encoded, decoded, encoded again.
        var source = Path.Combine(_root, "logo.png");
        await File.WriteAllBytesAsync(source, Png(Noise(100, 7)));
        var drawn = await WorkspaceIconStore.RenderAsync(source);
        var name = await Here.SaveAsync(drawn!.Bitmap);

        drawn.FileName.Should().Be(name);
        (await There.ImportAsync(Here.ReadForTransfer(name))).Should().Be(name);
    }

    [Fact]
    public void ProjectFile_WithoutAPicture_WritesNoSuchField()
    {
        var path = Path.Combine(_root, "plain.tcvproj");

        WorkspaceProjectFile.Save(path, WorkspaceProjectFile.FromWorkspace(new Workspace { Name = "plain" }, path));

        File.ReadAllText(path).Should().NotContain("Picture");
    }

    [Fact]
    public void Merge_TakesTheFilesPicture_AndAFileWithoutOneLeavesTheLocalPicture()
    {
        var session = new Workspace { Name = "demo", IconImage = "local.png" };

        WorkspaceProjectFile.MergeIntoSession(new Workspace { Name = "demo" }, session).IconImage.Should().Be("local.png");
        WorkspaceProjectFile.MergeIntoSession(new Workspace { Name = "demo", IconImage = "file.png" }, session).IconImage.Should().Be("file.png");
    }

    [Fact]
    public async Task SettingsBundle_CarriesThePictures_ToAnotherMachine()
    {
        var first = await StoredPicture(1);
        var second = await StoredPicture(2);
        var bundle = new SettingsBundle
        {
            Workspaces =
            [
                new Workspace { Name = "a", IconImage = first },
                new Workspace { Name = "b", IconImage = second },
                new Workspace { Name = "same as a", IconImage = first },
                new Workspace { Name = "none" },
            ],
        };
        var exported = Here.ReadAllForTransfer(bundle.Workspaces.Select(w => w.IconImage));
        bundle.WorkspacePictures = exported.Pictures;

        exported.LeftOut.Should().Be(0);
        exported.Pictures.Should().HaveCount(2, "a picture two workspaces wear goes once");

        var restored = SettingsBundle.FromJson(bundle.ToJson())!;
        (await There.ImportAllAsync(restored.Workspaces!, restored.WorkspacePictures)).Should().Be(0);
        var store = new WorkspaceStore(Path.Combine(_root, "there.json"));
        store.SaveImported(restored.Workspaces!).Should().Be(4);

        There.Load(store.Get("a")!.IconImage).Should().NotBeNull();
        There.Load(store.Get("b")!.IconImage).Should().NotBeNull();
        store.Get("same as a")!.IconImage.Should().Be(store.Get("a")!.IconImage);
        store.Get("none")!.IconImage.Should().BeEmpty();
    }

    [Fact]
    public async Task SettingsBundle_APictureNotCarried_StaysOnlyIfItIsOnThisDisk()
    {
        var name = await StoredPicture();
        var here = new Workspace { Name = "here", IconImage = name };
        var elsewhere = new Workspace { Name = "elsewhere", IconImage = "0123456789abcdef0123.png" };

        (await Here.ImportAllAsync([here, elsewhere, null], pictures: null)).Should().Be(0);

        here.IconImage.Should().Be(name, "a bundle from before pictures travelled, restored on the same machine");
        elsewhere.IconImage.Should().BeEmpty();
        WorkspacePicture.WantsSearch(elsewhere).Should().BeTrue("its project may be searched again");
    }

    [Fact]
    public async Task ANoisyPicture_TheWorstTheStoreCanDraw_FitsTheLimit()
    {
        var name = await StoredPicture();

        new FileInfo(Path.Combine(HereFolder, name)).Length.Should().BeLessThanOrEqualTo(WorkspacePictureData.MaxBytes);
        Here.ReadForTransfer(name).Should().NotBeNull();
    }

    [Fact]
    public void ReadForTransfer_AFileOverTheLimit_StaysOut()
    {
        Directory.CreateDirectory(HereFolder);
        var name = "0123456789abcdef0123.png";
        File.WriteAllBytes(Path.Combine(HereFolder, name), PngHeader(64, 64, WorkspacePictureData.MaxBytes + 1));

        Here.ReadForTransfer(name).Should().BeNull();
        Here.ReadForTransfer("0123456789abcdef9999.png").Should().BeNull("no such file");
    }

    [Fact]
    public void ReadAllForTransfer_OverTheBundleLimit_LeavesTheRestOutAndSaysHowMany()
    {
        Directory.CreateDirectory(HereFolder);
        var names = Enumerable.Range(0, 60).Select(i => $"{i:x20}.png").ToList();
        foreach (var name in names)
        {
            File.WriteAllBytes(Path.Combine(HereFolder, name), PngHeader(64, 64, WorkspacePictureData.MaxBytes));
        }

        var exported = Here.ReadAllForTransfer(names);

        exported.Pictures.Values.Sum(v => v.Length).Should().BeLessThanOrEqualTo(WorkspacePictureData.MaxBundleChars);
        exported.LeftOut.Should().BeGreaterThan(0);
        (exported.Pictures.Count + exported.LeftOut).Should().Be(names.Count);
        exported.Pictures.Should().ContainKey(names[0], "the ones given first go first");
    }

    // ── A file from elsewhere ────────────────────────────────────────────────

    [Theory]
    [InlineData(@"..\evil.png")]
    [InlineData("../evil.png")]
    [InlineData(@"..\..\..\Windows\evil.png")]
    [InlineData(@"C:\Windows\evil.png")]
    [InlineData(@"\\server\share\evil.png")]
    [InlineData("..")]
    [InlineData("CON")]
    [InlineData("0123456789abcdef0123.png:stream")]
    [InlineData("0123456789abcdef012g.png")]
    [InlineData("0123456789abcdef0123.exe")]
    [InlineData("")]
    public async Task AHarmfulName_NeverReachesAPath(string name)
    {
        // Such a file stands beside the store: a name that leaves it would find this one.
        Directory.CreateDirectory(HereFolder);
        File.WriteAllBytes(Path.Combine(HereFolder, "..", "evil.png"), Png(Noise(64, 1)));

        WorkspaceIconStore.IsStoredName(name).Should().BeFalse();
        Here.Load(name).Should().BeNull();
        (await Here.LoadAsync(name)).Should().BeNull();
        Here.ReadForTransfer(name).Should().BeNull();
    }

    [Theory]
    [InlineData(@"..\..\evil.png")]
    [InlineData(@"C:\Windows\evil.png")]
    [InlineData("CON")]
    public async Task SettingsBundle_AHarmfulName_IsOnlyAKey_AndWritesNothingOutsideTheStore(string name)
    {
        var picture = Convert.ToBase64String(Png(Noise(64, 3)));
        var carried = new Workspace { Name = "carried", IconImage = name };
        var bare = new Workspace { Name = "bare", IconImage = name };
        var before = FilesUnderRootOutsideTheStores();

        (await There.ImportAllAsync([carried], new Dictionary<string, string> { [name] = picture })).Should().Be(0);
        await There.ImportAllAsync([bare], pictures: null);

        WorkspaceIconStore.IsStoredName(carried.IconImage).Should().BeTrue("named by its content");
        File.Exists(Path.Combine(ThereFolder, carried.IconImage)).Should().BeTrue();
        bare.IconImage.Should().BeEmpty();
        FilesUnderRootOutsideTheStores().Should().Be(before);
    }

    [Fact]
    public void SaveImported_APictureNamedAsTheStoreNeverNamesOne_IsDropped()
    {
        var store = new WorkspaceStore(Path.Combine(_root, "imported.json"));

        store.SaveImported([
            new Workspace { Name = "evil", IconImage = @"..\..\evil.png" },
            new Workspace { Name = "good", IconImage = "0123456789abcdef0123.png" },
        ]);

        store.Get("evil")!.IconImage.Should().BeEmpty();
        store.Get("good")!.IconImage.Should().Be("0123456789abcdef0123.png");
    }

    public static TheoryData<string, string> RefusedText()
    {
        var data = new TheoryData<string, string>
        {
            { "not base64", "!!! not base64 !!!" },
            { "not a PNG", Convert.ToBase64String("just some text, long enough to have a header at all"u8.ToArray()) },
            { "a picture of another format", Convert.ToBase64String(Gif()) },
            { "a PNG of another size", Convert.ToBase64String(Png(Noise(32, 1))) },
            { "a header claiming gigapixels", Convert.ToBase64String(PngHeader(30000, 30000)) },
            { "a header with nothing behind it", Convert.ToBase64String(PngHeader(64, 64)) },
            { "over the limit", Convert.ToBase64String(PngHeader(64, 64, WorkspacePictureData.MaxBytes + 1)) },
            { "empty", string.Empty },
        };
        return data;

        static byte[] Gif()
        {
            var encoder = new GifBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(Noise(64, 1)));
            using var buffer = new MemoryStream();
            encoder.Save(buffer);
            return buffer.ToArray();
        }
    }

    [Theory]
    [MemberData(nameof(RefusedText))]
    public async Task ImportAsync_RefusesWhatIsNotAStoredPicture_AndWritesNothing(string why, string text)
    {
        (await There.ImportAsync(text)).Should().BeNull(why);

        Directory.Exists(ThereFolder).Should().BeFalse(why);
    }

    [Fact]
    public async Task SettingsBundle_ARefusedPicture_IsCounted_AndTheWorkspaceGoesOnWithout()
    {
        var workspace = new Workspace { Name = "a", IconImage = "x" };

        (await There.ImportAllAsync([workspace], new Dictionary<string, string> { ["x"] = "!!!" })).Should().Be(1);

        workspace.IconImage.Should().BeEmpty();
    }

    [Fact]
    public async Task ImportAsync_KeepsThePixels_NotWhatAFilePutAfterThem()
    {
        var clean = Png(Noise(64, 5));
        var cleanName = await There.ImportAsync(Convert.ToBase64String(clean));
        var padded = clean.Concat("MZ payload after the end of the picture"u8.ToArray()).ToArray();

        var name = await There.ImportAsync(Convert.ToBase64String(padded));

        name.Should().Be(cleanName);
        File.ReadAllBytes(Path.Combine(ThereFolder, name!)).AsSpan().IndexOf("MZ payload"u8).Should().Be(-1);
    }

    // ── The store's memory ───────────────────────────────────────────────────

    [Fact]
    public async Task Peek_AsksOnlyTheMemory()
    {
        var name = await StoredPicture();
        var store = new WorkspaceIconStore(HereFolder);

        store.Peek(name).Should().BeNull("not read yet, and asking must not read it");
        store.Peek(null).Should().BeNull();

        (await store.LoadAsync(name)).Should().NotBeNull();
        File.Delete(Path.Combine(HereFolder, name));

        store.Peek(name).Should().NotBeNull("a content-named file does not change: it is not read again");
    }

    [Fact]
    public async Task LoadAsync_AFileThatIsNotThere_IsNull()
    {
        (await Here.LoadAsync("0123456789abcdef0123.png")).Should().BeNull();
    }

    [Fact]
    public async Task PreloadAsync_PutsThePicturesInMemory_AndPassesOverTheRest()
    {
        var name = await StoredPicture();
        var store = new WorkspaceIconStore(HereFolder);

        await store.PreloadAsync([name, null, string.Empty, @"..\evil.png", "0123456789abcdef0123.png"]);

        store.Peek(name).Should().NotBeNull();
        store.Peek("0123456789abcdef0123.png").Should().BeNull();
    }

    [Fact]
    public void Load_DecodesAtTheStoredSize_WhateverLiesInTheFolder()
    {
        Directory.CreateDirectory(HereFolder);
        var name = "0123456789abcdef0123.png";
        File.WriteAllBytes(Path.Combine(HereFolder, name), Png(Noise(200, 9)));

        ((BitmapSource)Here.Load(name)!).PixelWidth.Should().Be(WorkspaceIconStore.PixelSize);
    }

    // ── The properties window ────────────────────────────────────────────────

    [Fact]
    public async Task PropertiesWindow_MarksAmongTheFound_ThePictureTheWorkspaceWears()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var logo = Path.Combine(repo, "logo.png");
        var icon = Path.Combine(repo, "icon.png");
        await File.WriteAllBytesAsync(logo, Png(Noise(80, 11)));
        await File.WriteAllBytesAsync(icon, Png(Noise(80, 12)));

        // What the search does: the picture drawn and stored before the window is ever opened.
        var worn = await Here.SaveAsync((await WorkspaceIconStore.RenderAsync(logo))!.Bitmap);

        var vm = new WorkspacePropertiesDialogViewModel(
            new Workspace { Name = "api", IconImage = worn! }, ["api"], [],
            pictures: new WorkspacePictureContext(Here, repo, _ => null));
        SpinWait.SpinUntil(() => vm.Candidates.Count == 2, TimeSpan.FromSeconds(30)).Should().BeTrue();

        vm.Candidates.Single(c => c.IsSelected).Path.Should().Be(logo);

        vm.RemovePictureCommand.Execute(null);

        vm.Candidates.Should().OnlyContain(c => !c.IsSelected);
    }

    [Fact]
    public async Task PropertiesWindow_ThePreviewDropsTheIconBesideAPicture()
    {
        var name = await StoredPicture();

        var vm = new WorkspacePropertiesDialogViewModel(
            new Workspace { Name = "api", Icon = "🚀", IconImage = name }, ["api"], [],
            pictures: new WorkspacePictureContext(Here, null, _ => null));

        vm.PreviewName.Should().Be("api");

        vm.RemovePictureCommand.Execute(null);

        vm.PreviewName.Should().Be("🚀 api");
    }
}
