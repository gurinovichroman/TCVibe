using System.IO;
using System.IO.Compression;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels;
using TCV.Archives;
using TCV.Archives.Engines;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// An archive opened from inside another archive or from a server is a temporary copy, and takes no change
/// (TRAP-98); one that is the user's own says in its log lines which file an edit inside it rewrote (TRAP-62).
/// </summary>
[Collection("TemporaryFiles")]
public sealed class ArchiveEditingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-arc-edit-" + Guid.NewGuid().ToString("N"));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-arc-edit-temp-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousRoot = TemporaryFiles.Root;

    public ArchiveEditingTests()
    {
        Directory.CreateDirectory(_dir);
        TemporaryFiles.Root = _root;
    }

    public void Dispose()
    {
        TemporaryFiles.Root = _previousRoot;
        Directory.Delete(_dir, recursive: true);
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string Zip(string path, params string[] entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write("content of " + name);
        }

        return path;
    }

    private string ScratchZip() => Zip(TemporaryFiles.PathFor("inner.zip"), "a.txt");

    private string OwnZip() => Zip(Path.Combine(_dir, "own.zip"), "a.txt", "dir/b.txt");

    // ── Read-only copies ─────────────────────────────────────────────────────

    [Fact]
    public void OpenOptions_TheUsersOwnArchive_AreNone()
    {
        ArchiveEditing.OpenOptions(Path.Combine(_dir, "own.zip"), password: null).Should().BeNull();
    }

    [Fact]
    public void OpenOptions_APasswordAlone_IsPassedOn()
    {
        ArchiveEditing.OpenOptions(Path.Combine(_dir, "own.zip"), "secret").Should()
            .BeEquivalentTo(new Dictionary<string, string> { ["password"] = "secret" });
    }

    [Fact]
    public void OpenThroughThePlugin_ScratchCopy_IsReadOnly_TheUsersOwnIsNot()
    {
        var plugin = new ArchivesPlugin();
        var scratch = ScratchZip();
        var own = OwnZip();

        using var fromScratch = (ArchiveFileSystem)plugin.Create(scratch, ArchiveEditing.OpenOptions(scratch, null));
        using var fromDisk = (ArchiveFileSystem)plugin.Create(own, ArchiveEditing.OpenOptions(own, null));

        fromScratch.CanEdit.Should().BeFalse();
        fromScratch.SupportsOperation(VfsOperation.Rename).Should().BeFalse("the menu leaves out what SupportsOperation refuses");
        fromDisk.CanEdit.Should().BeTrue();
    }

    [Fact]
    public void CanEditFile_ByPathAlone_KnowsAScratchCopyFromTheUsersOwn()
    {
        var own = OwnZip();

        ArchiveEditing.CanEditFile(new ZipEngine(), ScratchZip()).Should().BeFalse();
        ArchiveEditing.CanEditFile(new ZipEngine(), own).Should().BeTrue();
        ArchiveEditing.CanEditFile(new TarEngine(), own).Should().BeFalse();
    }

    [Fact]
    public async Task CreateDirectoryOperation_InAScratchCopy_FailsAndLeavesTheFile()
    {
        var scratch = ScratchZip();
        var before = File.ReadAllBytes(scratch);
        using var fs = new ArchiveFileSystem(scratch, new ZipEngine(), isScratchCopy: true);
        var op = new CreateDirectoryOperation(fs, new VfsPath("/new"), NullLogger<CreateDirectoryOperation>.Instance);

        var act = () => op.ExecuteAsync(CancellationToken.None);

        await act.Should().ThrowAsync<NotSupportedException>();
        op.State.Should().Be(OperationState.Failed);
        File.ReadAllBytes(scratch).Should().Equal(before);
    }

    // ── Log lines ────────────────────────────────────────────────────────────

    [Fact]
    public async Task LogDescriptor_EditThroughAnArchive_NamesItsFile()
    {
        var own = OwnZip();
        using var fs = new ArchiveFileSystem(own, new ZipEngine());
        var entry = (await fs.GetEntryAsync(new VfsPath("/a.txt")))!;

        new RenameEntryOperation(fs, entry, "b.txt", NullLogger.Instance).LogDescriptor.ChangedArchive.Should().Be(own);
        new CreateDirectoryOperation(fs, new VfsPath("/new"), NullLogger.Instance).LogDescriptor.ChangedArchive.Should().Be(own);
    }

    [Fact]
    public async Task RenameOperation_InsideAnArchive_TheLogLineKnowsWhereToTakeItBack()
    {
        var own = OwnZip();
        using var fs = new ArchiveFileSystem(own, new ZipEngine());
        var op = new RenameOperation(fs, new VfsPath("/"), [("a.txt", "b.txt")], NullLogger.Instance);

        await op.ExecuteAsync(CancellationToken.None);

        var line = op.LogDescriptor;
        line.ChangedArchive.Should().Be(own);
        line.Renames!.Archive.Should().Be(own);
        (await fs.ListAsync(new VfsPath("/"))).Select(e => e.Name).Should().Contain("b.txt").And.NotContain("a.txt");

        var back = new RenameOperation(fs, new VfsPath("/"), [("b.txt", "a.txt")], NullLogger.Instance);
        await back.ExecuteAsync(CancellationToken.None);
        (await fs.ListAsync(new VfsPath("/"))).Select(e => e.Name).Should().Contain("a.txt").And.NotContain("b.txt");
    }

    [Fact]
    public async Task LogDescriptor_EditOfAHitOfAnArchiveSearch_NamesTheArchiveToo()
    {
        var own = OwnZip();
        using var fs = new ArchiveFileSystem(own, new ZipEngine());
        var entry = (await fs.GetEntryAsync(new VfsPath("/a.txt")))!;
        using var hits = new SearchResultsFileSystem(fs, [entry]);

        new RenameEntryOperation(hits, entry, "b.txt", NullLogger.Instance).LogDescriptor.ChangedArchive.Should().Be(own);
    }

    [Fact]
    public async Task LogDescriptor_EditOnAnythingElse_NamesNoArchive()
    {
        var disk = new InMemoryFileSystem("file");
        disk.AddFile("/a.txt", [1]);
        var entry = (await disk.GetEntryAsync(new VfsPath("/a.txt")))!;

        new RenameEntryOperation(disk, entry, "b.txt", NullLogger.Instance).LogDescriptor.ChangedArchive.Should().BeNull();
        new CreateDirectoryOperation(disk, new VfsPath("/new"), NullLogger.Instance).LogDescriptor.ChangedArchive.Should().BeNull();
    }

    // ── What Multi-Rename says ───────────────────────────────────────────────

    [Fact]
    public async Task ErrorText_NameTakenInAnArchive_IsTheDictionarys_NotTheEngines()
    {
        var own = OwnZip();
        using var fs = new ArchiveFileSystem(own, new ZipEngine());

        var act = () => fs.RenameBatchAsync([(new VfsPath("/a.txt"), "dir")], CancellationToken.None);
        var thrown = await act.Should().ThrowAsync<ArchiveNameTakenException>();

        MainViewModel.GetFileErrorMessage(thrown.Which).Should().Be("[Op_NameTaken]");
    }
}
