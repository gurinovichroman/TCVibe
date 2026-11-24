using System.IO;
using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.Sync;
using TCV.App.ViewModels.Dialogs;
using TCV.Archives;
using TCV.Archives.Engines;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using CoreEngine = TCV.Core.PluginSdk.IArchiveEngine;
using EntryInfo = TCV.Core.PluginSdk.ArchiveEntry;

namespace TCV.App.Tests;

/// <summary>
/// A synchronisation with an archive on one side (6.91, 6.42): files go into a ZIP with the folders, in one
/// rewrite; a side that takes no change is not offered as a target; and what is read from an archive comes
/// from one pass over it.
/// </summary>
// A server's files reach a zip through a scratch folder under TemporaryFiles.Root, which the other classes of
// this collection swap and delete: side by side with them, that folder went away under the rewrite.
[Collection("TemporaryFiles")]
public sealed class SyncArchiveTests : IDisposable
{
    private static readonly DateTime Older = new(2026, 3, 1, 10, 0, 0);
    private static readonly DateTime Newer = new(2026, 3, 5, 10, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-sync-arc-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _fs = new(NullLogger<WindowsFileSystem>.Instance);

    private string Disk => Path.Combine(_root, "disk");
    private string ZipPath => Path.Combine(_root, "t.zip");

    public SyncArchiveTests() => Directory.CreateDirectory(Disk);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Put(string relative, string content, DateTime modified)
    {
        var path = Path.Combine(Disk, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        File.SetLastWriteTime(path, modified);
    }

    private void Zip(params (string Name, string Content, DateTime Modified)[] entries)
    {
        using var zip = ZipFile.Open(ZipPath, ZipArchiveMode.Create);
        foreach (var (name, content, modified) in entries)
        {
            var entry = zip.CreateEntry(name);
            entry.LastWriteTime = modified;
            if (!name.EndsWith('/'))
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }
    }

    private Dictionary<string, string> ZipContents()
    {
        using var zip = ZipFile.OpenRead(ZipPath);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e => e.FullName.EndsWith('/') ? string.Empty : new StreamReader(e.Open()).ReadToEnd());
    }

    private static async Task<List<ComparisonRow>> CompareAsync(
        IVirtualFileSystem left, string leftRoot, IVirtualFileSystem right, string rightRoot, ComparisonOptions? options = null)
    {
        var rows = new List<ComparisonRow>();
        await foreach (var row in new DirectoryComparer(left, right, options ?? new ComparisonOptions { TimeTolerance = ComparisonOptions.RemoteTimeTolerance })
                           .CompareAsync(new VfsPath(leftRoot), new VfsPath(rightRoot)))
        {
            rows.Add(row);
        }

        return rows;
    }

    private static IReadOnlyList<SyncPlanItem> Default(IEnumerable<ComparisonRow> rows) =>
        rows.Where(r => r.DefaultDirection != SyncDirection.None).Select(r => new SyncPlanItem(r, r.DefaultDirection)).ToList();

    private static async Task<(SyncRunResult Result, List<OperationLogEntry> Log)> RunAsync(
        IReadOnlyList<SyncPlanItem> plan, SyncEndpoint left, SyncEndpoint right, string logPath)
    {
        var log = new OperationLog(logPath);
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);
        var runner = new SyncPlanRunner(
            NullLogger<CopyOperation>.Instance, NullLogger<DeleteOperation>.Instance, NullLogger<CreateDirectoryOperation>.Instance);

        var result = await runner.RunAsync(plan, left, right, async op =>
        {
            await queue.EnqueueAsync(op);
            return op.State == OperationState.Completed;
        });

        return (result, log.LoadAll().ToList());
    }

    private string LogPath => Path.Combine(_root, "log.json");

    // ── Into a ZIP ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_DiskIntoZip_TheFilesArriveWithTheFolders_InOneRewrite()
    {
        Put("src/deep/a.txt", "AAA", Older);
        Put("b.txt", "BBB", Older);
        Zip(("keep.txt", "KEEP", Older));
        using var archive = new ArchiveFileSystem(ZipPath, new ZipEngine());

        var rows = await CompareAsync(_fs, Disk, archive, "/");
        var plan = Default(rows).Where(p => p.Direction == SyncDirection.ToRight).ToList();
        var (result, log) = await RunAsync(plan, new SyncEndpoint(_fs, Disk, false), new SyncEndpoint(archive, "/", true), LogPath);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        result.FilesCopied.Should().Be(2);
        result.FoldersCreated.Should().Be(2);
        ZipContents().Should().Contain(new Dictionary<string, string>
        {
            ["b.txt"] = "BBB",
            ["src/deep/a.txt"] = "AAA",
            ["keep.txt"] = "KEEP",
        });
        log.Should().ContainSingle("one rewrite is one line in the log")
            .Which.Should().Match<OperationLogEntry>(l => l.Kind == OperationKind.Pack && l.Destination == ZipPath);
    }

    [Fact]
    public async Task Run_IntoASubfolderOfTheZip_LandsThere()
    {
        Put("a.txt", "AAA", Older);
        Zip(("sub/", string.Empty, Older));
        using var archive = new ArchiveFileSystem(ZipPath, new ZipEngine());

        var rows = await CompareAsync(_fs, Disk, archive, "/sub");
        var (result, _) = await RunAsync(Default(rows), new SyncEndpoint(_fs, Disk, false), new SyncEndpoint(archive, "/sub", true), LogPath);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        ZipContents().Should().ContainKey("sub/a.txt");
    }

    [Fact]
    public async Task Run_ReplacingAnEntry_ThenCompareAgain_TheSidesHaveSettled()
    {
        Put("a.txt", "new version", Newer);
        Zip(("a.txt", "old", Older));
        using var archive = new ArchiveFileSystem(ZipPath, new ZipEngine());

        var rows = await CompareAsync(_fs, Disk, archive, "/");
        var (result, _) = await RunAsync(Default(rows), new SyncEndpoint(_fs, Disk, false), new SyncEndpoint(archive, "/", true), LogPath);
        var again = await CompareAsync(_fs, Disk, archive, "/");

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        result.FilesReplaced.Should().Be(1);
        ZipContents()["a.txt"].Should().Be("new version");
        again.Should().BeEmpty("the entry took the source's time");
    }

    [Fact]
    public async Task Run_FromAServerIntoZip_BringsTheFilesKeepingTheirTimes()
    {
        var server = new InMemoryFileSystem("sftp");
        server.AddFile("/srv/a.txt", "AAA"u8.ToArray(), Older);
        Zip(("keep.txt", "KEEP", Older));
        using var archive = new ArchiveFileSystem(ZipPath, new ZipEngine());

        var rows = await CompareAsync(server, "/srv", archive, "/");
        var plan = Default(rows).Where(p => p.Direction == SyncDirection.ToRight).ToList();
        var (result, _) = await RunAsync(plan, new SyncEndpoint(server, "/srv", true), new SyncEndpoint(archive, "/", true), LogPath);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        ZipContents()["a.txt"].Should().Be("AAA");
        (await archive.GetEntryAsync(new VfsPath("/a.txt")))!.LastModified.Should().BeCloseTo(Older, TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Run_FailedRewrite_ReportsIt_AndTheArchiveStaysAsItWas()
    {
        Put("a.txt", "AAA", Older);
        Zip(("keep.txt", "KEEP", Older));
        var before = File.ReadAllBytes(ZipPath);
        using var archive = new ArchiveFileSystem(ZipPath, new ZipEngine());
        var rows = await CompareAsync(_fs, Disk, archive, "/");
        var plan = Default(rows).Where(p => p.Direction == SyncDirection.ToRight).ToList();

        File.Delete(Path.Combine(Disk, "a.txt"));   // gone by the time the rewrite wants it
        var (result, log) = await RunAsync(plan, new SyncEndpoint(_fs, Disk, false), new SyncEndpoint(archive, "/", true), LogPath);

        result.Succeeded.Should().BeFalse();
        result.FilesCopied.Should().Be(0);
        File.ReadAllBytes(ZipPath).Should().Equal(before);
        log.Single().Outcome.Should().Be(OperationOutcome.Failed);
    }

    // ── A side that takes no change ──────────────────────────────────────────

    private async Task<CompareDirectoriesDialogViewModel> CompareInWindowAsync(IVirtualFileSystem left, IVirtualFileSystem right, string rightRoot)
    {
        var vm = new CompareDirectoriesDialogViewModel(new SyncEndpoint(left, Disk, false), new SyncEndpoint(right, rightRoot, true));
        await vm.CompareCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task Window_ScratchCopyOnTheRight_NoRowPointsAtIt()
    {
        Put("only-disk.txt", "x", Older);
        Zip(("only-zip.txt", "y", Older));
        using var scratch = new ArchiveFileSystem(ZipPath, new ZipEngine(), isScratchCopy: true);

        var vm = await CompareInWindowAsync(_fs, scratch, "/");

        vm.Rows.Single(r => r.Name == "only-disk.txt").Direction.Should().Be(SyncDirection.None);
        vm.Rows.Single(r => r.Name == "only-disk.txt").IsChecked.Should().BeFalse();
        vm.Rows.Single(r => r.Name == "only-zip.txt").Direction.Should().Be(SyncDirection.ToLeft);
        vm.StatusText.Should().Contain("[Cmp_RightTakesNoCopies]");
    }

    [Fact]
    public async Task Window_FormatThatCannotBeWritten_AllToItTicksNothing_AndTheArrowSkipsIt()
    {
        Put("only-disk.txt", "x", Older);
        var tar = Path.Combine(_root, "t.tar");
        new TarEngine().Pack(tar, [(Path.Combine(Disk, "only-disk.txt"), "only-tar.txt")]);
        using var readOnly = new ArchiveFileSystem(tar, new TarEngine());

        var vm = await CompareInWindowAsync(_fs, readOnly, "/");
        vm.SelectToRightCommand.Execute(null);

        vm.Plan.Should().BeEmpty();

        var row = vm.Rows.Single(r => r.Name == "only-tar.txt");
        vm.SelectDefaultsCommand.Execute(null);
        row.Direction.Should().Be(SyncDirection.ToLeft);
        row.CycleDirectionCommand.Execute(null);
        row.Direction.Should().Be(SyncDirection.None, "the only other way points at the archive");
    }

    [Fact]
    public async Task Window_WritableZip_StillTakesCopies()
    {
        Put("only-disk.txt", "x", Older);
        Zip(("keep.txt", "y", Older));
        using var archive = new ArchiveFileSystem(ZipPath, new ZipEngine());

        var vm = await CompareInWindowAsync(_fs, archive, "/");

        vm.Rows.Single(r => r.Name == "only-disk.txt").Direction.Should().Be(SyncDirection.ToRight);
        vm.StatusText.Should().NotContain("TakesNoCopies");
    }

    [Fact]
    public async Task Run_IntoAScratchCopy_AnyWay_IsRefusedAndTheFileStaysAsItWas()
    {
        Put("a.txt", "AAA", Older);
        Zip(("keep.txt", "KEEP", Older));
        var before = File.ReadAllBytes(ZipPath);
        using var scratch = new ArchiveFileSystem(ZipPath, new ZipEngine(), isScratchCopy: true);
        var rows = await CompareAsync(_fs, Disk, scratch, "/");
        var plan = Default(rows).Where(p => p.Direction == SyncDirection.ToRight).ToList();

        var (result, _) = await RunAsync(plan, new SyncEndpoint(_fs, Disk, false), new SyncEndpoint(scratch, "/", true), LogPath);

        result.Succeeded.Should().BeFalse();
        File.ReadAllBytes(ZipPath).Should().Equal(before);
    }

    // ── One pass over an archive read from (6.42) ────────────────────────────

    private string TarOf(int files)
    {
        var tar = Path.Combine(_root, "t.tar");
        var sources = new List<(string, string)>();
        for (var i = 0; i < files; i++)
        {
            var local = Path.Combine(_root, $"src{i}.txt");
            File.WriteAllText(local, "content " + i);
            File.SetLastWriteTime(local, Newer);
            sources.Add((local, $"dir{i % 3}/f{i}.txt"));
        }

        new TarEngine().Pack(tar, sources);
        return tar;
    }

    [Fact]
    public async Task Run_FromAnArchive_ReadsItInOnePass_ThroughManyFolders()
    {
        var engine = new CountingEngine(new TarEngine());
        using var archive = new ArchiveFileSystem(TarOf(30), engine);

        var rows = await CompareAsync(_fs, Disk, archive, "/");
        var (result, _) = await RunAsync(Default(rows), new SyncEndpoint(_fs, Disk, false), new SyncEndpoint(archive, "/", true), LogPath);

        result.Succeeded.Should().BeTrue(string.Join("; ", result.Failures));
        result.FilesCopied.Should().Be(30);
        File.ReadAllText(Path.Combine(Disk, "dir2", "f29.txt"), Encoding.UTF8).Should().Be("content 29");
        engine.Passes.Should().Be(1);
        engine.SingleReads.Should().Be(0, "going file by file decodes a solid archive from its start for each");
    }

    [Fact]
    public async Task Compare_ByContent_ReadsTheArchiveInOnePass_OnlyThePairsOfEqualSize()
    {
        var engine = new CountingEngine(new TarEngine());
        using var archive = new ArchiveFileSystem(TarOf(6), engine);
        for (var i = 0; i < 5; i++)
        {
            Put($"dir{i % 3}/f{i}.txt", "content " + i, Older);
        }

        Put("dir2/f5.txt", "of another length", Older);

        var rows = await CompareAsync(_fs, Disk, archive, "/", new ComparisonOptions { CompareContent = true, TimeTolerance = TimeSpan.FromSeconds(61) });

        rows.Should().NotContain(r => r.Name == "f0.txt", "the bytes are the same, whatever the times say");
        rows.Should().Contain(r => r.Name == "f5.txt");
        engine.Passes.Should().Be(1);
        engine.SingleReads.Should().Be(0);
        engine.Requested.Should().HaveCount(5).And.NotContain("dir2/f5.txt");
    }

    [Fact]
    public async Task Compare_ByContent_ListsEachFolderOnce()
    {
        var server = new InMemoryFileSystem("sftp");
        server.AddFile("/l/a.txt", "AAA"u8.ToArray(), Older);
        server.AddFile("/l/sub/b.txt", "AAA"u8.ToArray(), Older);
        var listed = new List<string>();
        var left = new Mock<IVirtualFileSystem>();
        left.SetupGet(f => f.Scheme).Returns("sftp");
        left.Setup(f => f.GetEntryAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns((VfsPath path, CancellationToken ct) => server.GetEntryAsync(path, ct));
        left.Setup(f => f.OpenReadAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns((VfsPath path, CancellationToken ct) => server.OpenReadAsync(path, ct));
        left.Setup(f => f.ListAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns((VfsPath path, CancellationToken ct) =>
            {
                listed.Add(path.Value);
                return server.ListAsync(path, ct);
            });
        var tar = Path.Combine(_root, "c.tar");
        var local = Path.Combine(_root, "x.txt");
        File.WriteAllText(local, "AAA");
        new TarEngine().Pack(tar, [(local, "a.txt"), (local, "sub/b.txt")]);
        using var archive = new ArchiveFileSystem(tar, new TarEngine());

        await CompareAsync(left.Object, "/l", archive, "/", new ComparisonOptions { CompareContent = true });

        listed.Should().BeEquivalentTo(["/l", "/l/sub"], "a server is not asked twice for the sake of announcing the reads");
    }

    [Fact]
    public async Task Compare_NotByContent_ReadsNothing()
    {
        var engine = new CountingEngine(new TarEngine());
        using var archive = new ArchiveFileSystem(TarOf(4), engine);

        await CompareAsync(_fs, Disk, archive, "/");

        engine.Passes.Should().Be(0);
        engine.SingleReads.Should().Be(0);
    }

    /// <summary>Tells a single read from a pass.</summary>
    private sealed class CountingEngine(CoreEngine inner) : CoreEngine
    {
        public int Passes { get; private set; }

        public int SingleReads { get; private set; }

        public IReadOnlyList<string> Requested { get; private set; } = [];

        public bool CanWrite => inner.CanWrite;

        public IReadOnlyList<EntryInfo> ListAll(string archivePath) => inner.ListAll(archivePath);

        public Stream ExtractEntry(string archivePath, string entryPath)
        {
            SingleReads++;
            return inner.ExtractEntry(archivePath, entryPath);
        }

        public void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume, CancellationToken ct)
        {
            Passes++;
            Requested = entryPaths;
            inner.ReadEntries(archivePath, entryPaths, consume, ct);
        }

        public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files) => throw new NotSupportedException();

        public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) => throw new NotSupportedException();

        public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files) => throw new NotSupportedException();

        public void DeleteEntry(string archivePath, string entryPath) => throw new NotSupportedException();
    }
}
