using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using TCV.App.Services;
using TCV.App.Viewer;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>The viewer stepping through the panel's files with N and P (6.41).</summary>
public class ViewerBrowseTests
{
    private static IFileEntry FileRow(string path, long size = 10) =>
        new FileEntry(new VfsPath(path), new VfsPath(path).FileName, false, size, new DateTime(2026, 1, 1), FileAttributes.Normal);

    private static IFileEntry FolderRow(string path) =>
        new FileEntry(new VfsPath(path), new VfsPath(path).FileName, true, 0, new DateTime(2026, 1, 1), FileAttributes.Directory);

    // ── The list ──────────────────────────────────────────────────────────────

    [Fact]
    public void Create_PanelRowsWithFoldersAndParent_KeepsTheFilesInPanelOrder()
    {
        var b = FileRow("/d/b.txt");
        var rows = new IFileEntry[]
        {
            new FileEntry(new VfsPath(".."), "..", true, 0, default, FileAttributes.Directory),
            FolderRow("/d/sub"), FileRow("/d/c.txt"), b, FolderRow("/d/zzz"), FileRow("/d/a.txt"),
        };

        var list = ViewerFileList.Create(rows, b);

        list.Files.Select(f => f.Name).Should().Equal("c.txt", "b.txt", "a.txt");
        list.Current.Should().BeSameAs(b);
        list.Neighbour(+1)!.Name.Should().Be("a.txt");
        list.Neighbour(-1)!.Name.Should().Be("c.txt");
    }

    [Fact]
    public void Neighbour_PastEitherEnd_IsNull()
    {
        var first = FileRow("/d/a.txt");
        var last = FileRow("/d/b.txt");

        ViewerFileList.Create([first, last], first).Neighbour(-1).Should().BeNull();
        ViewerFileList.Create([first, last], last).Neighbour(+1).Should().BeNull("N on the last file does not wrap");
    }

    [Fact]
    public void Create_CurrentNotAmongTheRows_StandsAlone()
    {
        var shown = FileRow("/d/a.txt");
        var other = FileRow("/elsewhere/x.txt");

        var list = ViewerFileList.Create([shown, FileRow("/d/b.txt")], other);

        list.Files.Should().ContainSingle().Which.Should().BeSameAs(other);
        list.Neighbour(+1).Should().BeNull();
    }

    [Fact]
    public void Create_SamePathInAnotherCase_IsTheCurrentFile()
    {
        var list = ViewerFileList.Create([FileRow("/d/a.txt"), FileRow("/d/B.TXT"), FileRow("/d/c.txt")], FileRow("/d/b.txt"));

        list.Position.Should().Be(1);
    }

    [Fact]
    public void MoveTo_ListedFile_BecomesCurrent()
    {
        var a = FileRow("/d/a.txt");
        var c = FileRow("/d/c.txt");
        var list = ViewerFileList.Create([a, FileRow("/d/b.txt"), c], a);

        list.MoveTo(c);

        list.Current.Should().BeSameAs(c);
        list.Neighbour(+1).Should().BeNull();
    }

    // ── The viewer ────────────────────────────────────────────────────────────

    private sealed class CountingSource(IViewerSource inner) : IViewerSource
    {
        public bool Disposed { get; private set; }
        public long Length => inner.Length;

        public Task<byte[]> ReadAsync(long offset, int count, IProgress<long>? progress, CancellationToken ct) =>
            inner.ReadAsync(offset, count, progress, ct);

        public void Dispose()
        {
            Disposed = true;
            inner.Dispose();
        }
    }

    private sealed class PanelFolder
    {
        public InMemoryFileSystem Fs { get; } = new();
        public List<IFileEntry> Rows { get; } = [];
        public List<CountingSource> Opened { get; } = [];
        public Mock<IDisposable> Lease { get; } = new();

        public PanelFolder Add(string path, string content)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            Fs.AddFile(path, bytes);
            Rows.Add(FileRow(path, bytes.Length));
            return this;
        }

        /// <summary>Opens the viewer on row <paramref name="index"/> the way F3 does, with the panel's rows for N/P.</summary>
        public FileViewerDialogViewModel Open(int index)
        {
            var entry = Rows[index];
            var siblings = new ViewerSiblings(ViewerFileList.Create(Rows, entry), OpenSource, Lease.Object);
            var data = Fs.ReadFile(entry.Path);
            return new FileViewerDialogViewModel(entry.Name, data, false, data.Length, null, siblings.Open(entry), siblings);
        }

        private IViewerSource OpenSource(IFileEntry entry)
        {
            var source = new CountingSource(ViewerLauncher.CreateSource(Fs, entry, extractOnce: false));
            Opened.Add(source);
            return source;
        }
    }

    private static PanelFolder ThreeFiles() => new PanelFolder()
        .Add("/d/a.txt", "first file")
        .Add("/d/b.cs", "class B { }")
        .Add("/d/c.md", "# Third");

    [Fact]
    public async Task OpenNextFile_ShowsTheNextFileOfThePanel()
    {
        var folder = ThreeFiles();
        var vm = folder.Open(0);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await vm.OpenNextFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("b.cs");
        changed.Should().Contain(string.Empty, "the title and everything else bound to the file is refreshed");
        vm.TextContent.Should().Be("class B { }");
        vm.Mode.Should().Be(ViewerMode.Code, "the mode is chosen for the new file, as on open");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task OpenPreviousFile_AfterNext_ReturnsAndDisposesWhatItLeft()
    {
        var folder = ThreeFiles();
        var vm = folder.Open(1);

        await vm.OpenNextFileCommand.ExecuteAsync(null);
        await vm.OpenPreviousFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("b.cs");
        folder.Opened.Should().HaveCount(3);
        folder.Opened[0].Disposed.Should().BeTrue("the first file's source went with the first step");
        folder.Opened[1].Disposed.Should().BeTrue();
        folder.Opened[2].Disposed.Should().BeFalse("it is the one in view");
    }

    [Fact]
    public async Task OpenPreviousFile_AtTheFirstFile_StaysAndSaysSo()
    {
        var vm = ThreeFiles().Open(0);

        await vm.OpenPreviousFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("a.txt");
        vm.FindStatus.Should().Contain("Viewer_FirstFile");
    }

    [Fact]
    public async Task OpenNextFile_AtTheLastFile_StaysAndSaysSo()
    {
        var vm = ThreeFiles().Open(2);

        await vm.OpenNextFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("c.md");
        vm.FindStatus.Should().Contain("Viewer_LastFile");
    }

    [Fact]
    public async Task OpenNextFile_FileGoneSinceF3_ReportsItAndTheNextPressGoesPast()
    {
        var folder = ThreeFiles();
        folder.Rows.Insert(1, FileRow("/d/deleted.txt"));   // listed by the panel, gone from the disk
        var vm = folder.Open(0);

        await vm.OpenNextFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("a.txt");
        vm.FindStatus.Should().Contain("Viewer_ReadFailed");

        await vm.OpenNextFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("b.cs");
        folder.Opened[1].Disposed.Should().BeTrue("the source of the file that could not be read is not kept");
    }

    [Fact]
    public async Task OpenNextFile_ResetsWhatBelongedToTheOldFile()
    {
        var folder = ThreeFiles();
        var vm = folder.Open(0);
        vm.SelectedEncoding = vm.Encodings.First(e => e.Name == "UTF-16 LE");
        vm.WordWrap = true;
        vm.FindStatus = "\"x\" not found";

        await vm.OpenNextFileCommand.ExecuteAsync(null);

        vm.SelectedEncoding.Should().BeSameAs(vm.Encodings[0], "the next file has an encoding of its own");
        vm.FindStatus.Should().BeEmpty();
        vm.WordWrap.Should().BeTrue("wrapping is a preference, not a property of the file");
        vm.WindowOffset.Should().Be(0);
    }

    [Fact]
    public async Task OpenNextFile_ImageAfterText_OpensAsAnImage()
    {
        var folder = ThreeFiles();
        var png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        folder.Fs.AddFile("/d/dot.png", png);
        folder.Rows.Add(FileRow("/d/dot.png", png.Length));
        var vm = folder.Open(2);

        await vm.OpenNextFileCommand.ExecuteAsync(null);

        vm.FileName.Should().Be("dot.png");
        vm.Mode.Should().Be(ViewerMode.Image);
        vm.HasImage.Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_AfterSteps_ReleasesTheWindowLeaseOnce()
    {
        var folder = ThreeFiles();
        var vm = folder.Open(0);

        await vm.OpenNextFileCommand.ExecuteAsync(null);
        folder.Lease.Verify(l => l.Dispose(), Times.Never, "stepping to another file keeps the file system leased");

        vm.Dispose();

        folder.Lease.Verify(l => l.Dispose(), Times.Once);
        folder.Opened[^1].Disposed.Should().BeTrue();
    }

    [Fact]
    public void OpenNextFile_SingleFile_IsUnavailable()
    {
        var folder = new PanelFolder().Add("/d/only.txt", "alone");

        folder.Open(0).OpenNextFileCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void CreateSource_InsideAnArchive_UnpacksOnce()
    {
        var fs = new InMemoryFileSystem("zip");
        var entry = FileRow("/a.txt");

        ViewerLauncher.CreateSource(fs, entry, extractOnce: true).Should().BeOfType<ExtractedViewerSource>();
        ViewerLauncher.CreateSource(fs, entry, extractOnce: false).Should().BeOfType<VfsViewerSource>();
    }
}

/// <summary>An archive entry is unpacked once for the viewer, not once per window (6.37).</summary>
[Collection("TemporaryFiles")]
public sealed class ExtractedViewerSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-viewer-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previous = TemporaryFiles.Root;
    private readonly byte[] _data = Enumerable.Range(0, 300_000).Select(i => (byte)(i % 251)).ToArray();
    private int _opens;

    public ExtractedViewerSourceTests() => TemporaryFiles.Root = _root;

    public void Dispose()
    {
        TemporaryFiles.Root = _previous;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private sealed class Tracked(byte[] data) : MemoryStream(data)
    {
        public bool Closed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    private ExtractedViewerSource Source(Func<Stream> open)
    {
        var fs = new Mock<IVirtualFileSystem>();
        fs.Setup(f => f.OpenReadAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                _opens++;
                return Task.FromResult(open());
            });
        return new ExtractedViewerSource(fs.Object, new VfsPath("/logs/app.log"), _data.Length);
    }

    private string[] ScratchFiles() =>
        Directory.Exists(_root) ? Directory.GetFiles(_root, "*", SearchOption.AllDirectories) : [];

    [Fact]
    public async Task ReadAsync_StreamThatCannotSeek_IsUnpackedOnceForEveryWindow()
    {
        using var source = Source(() => new ForwardOnly(_data));

        (await source.ReadAsync(200_000, 1000, null, default)).Should().Equal(_data[200_000..201_000]);
        (await source.ReadAsync(0, 100, null, default)).Should().Equal(_data[..100]);
        (await source.ReadAsync(299_950, 100, null, default)).Should().Equal(_data[299_950..]);

        _opens.Should().Be(1, "the tail, the start and a jump back all read the one copy");
        ScratchFiles().Should().ContainSingle().Which.Should().EndWith("app.log");
    }

    [Fact]
    public async Task Dispose_AfterACopy_RemovesTheScratchFile()
    {
        var source = Source(() => new ForwardOnly(_data));
        await source.ReadAsync(0, 10, null, default);

        source.Dispose();

        ScratchFiles().Should().BeEmpty("the copy goes with the viewer window or its move to another file");
        (Directory.Exists(_root) ? Directory.GetDirectories(_root) : []).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_SeekableStream_IsKeptWithoutAnotherCopy()
    {
        Tracked? opened = null;
        var source = Source(() => opened = new Tracked(_data));

        (await source.ReadAsync(250_000, 10, null, default)).Should().Equal(_data[250_000..250_010]);
        (await source.ReadAsync(5, 10, null, default)).Should().Equal(_data[5..15]);

        _opens.Should().Be(1);
        ScratchFiles().Should().BeEmpty("the archive engine's own buffer is already a seekable copy");
        opened!.Closed.Should().BeFalse();

        source.Dispose();

        opened.Closed.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_PastTheEnd_ReturnsWhatIsThere()
    {
        using var source = Source(() => new ForwardOnly(_data));

        (await source.ReadAsync(_data.Length - 10, 50, null, default)).Should().HaveCount(10);
        (await source.ReadAsync(_data.Length + 5, 50, null, default)).Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_AfterDispose_Throws()
    {
        var source = Source(() => new ForwardOnly(_data));
        source.Dispose();

        var read = () => source.ReadAsync(0, 10, null, default);

        await read.Should().ThrowAsync<ObjectDisposedException>();
    }
}
