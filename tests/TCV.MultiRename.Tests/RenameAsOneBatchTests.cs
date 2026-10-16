using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.MultiRename.Tests;

/// <summary>
/// A file system that renames a batch in one step (an archive, 6.71) gets the whole plan in one call:
/// no ordering, no temporary names, and all or nothing.
/// </summary>
public class RenameAsOneBatchTests
{
    private static IFileEntry Entry(string fullPath)
    {
        var mock = new Mock<IFileEntry>();
        var path = new VfsPath(fullPath);
        mock.Setup(e => e.Name).Returns(path.FileName);
        mock.Setup(e => e.Path).Returns(path);
        return mock.Object;
    }

    private static RenamePreviewItem Plan(string oldName, string newName) => new(oldName, newName, false);

    [Fact]
    public async Task ExecuteBatch_BatchFileSystem_GetsEveryRenameInOneCall()
    {
        var fs = new BatchFs("/d/a.txt", "/d/b.txt", "/d/c.txt");

        var result = await new RenameEngine(fs).ExecuteBatchAsync(
            [Entry("/d/a.txt"), Entry("/d/b.txt"), Entry("/d/c.txt")],
            [Plan("a.txt", "b.txt"), Plan("b.txt", "a.txt"), Plan("c.txt", "c.txt")]);

        result.Succeeded.Should().BeTrue();
        fs.Batches.Should().ContainSingle()
            .Which.Should().Equal(("/d/a.txt", "b.txt"), ("/d/b.txt", "a.txt"));
        fs.SingleRenames.Should().BeEmpty("a swap needs no temporary name when the renames apply together");
        fs.Contents["/d/a.txt"].Should().Be("was b.txt");
        result.Performed.Select(a => (a.CurrentPath.Value, a.OriginalName))
            .Should().Equal(("/d/b.txt", "a.txt"), ("/d/a.txt", "b.txt"));
    }

    [Fact]
    public async Task ExecuteBatch_BatchRefused_NothingIsPerformedAndEveryFileSaysWhy()
    {
        var fs = new BatchFs("/d/a.txt", "/d/b.txt") { Refusal = "name taken" };

        var result = await new RenameEngine(fs).ExecuteBatchAsync(
            [Entry("/d/a.txt"), Entry("/d/b.txt")], [Plan("a.txt", "x.txt"), Plan("b.txt", "y.txt")]);

        result.Performed.Should().BeEmpty();
        result.Failures.Select(f => (f.OldName, f.Error)).Should().Equal(("a.txt", "name taken"), ("b.txt", "name taken"));
        fs.Contents.Keys.Should().BeEquivalentTo("/d/a.txt", "/d/b.txt");
    }

    [Fact]
    public async Task ExecuteBatch_Cancelled_ReportsCancelledWithNothingDone()
    {
        var fs = new BatchFs("/d/a.txt");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await new RenameEngine(fs).ExecuteBatchAsync([Entry("/d/a.txt")], [Plan("a.txt", "b.txt")], cts.Token);

        result.Cancelled.Should().BeTrue();
        result.Performed.Should().BeEmpty();
        fs.Batches.Should().BeEmpty();
    }

    [Fact]
    public async Task UndoBatch_BatchFileSystem_TakesTheBatchBackInOneCall()
    {
        var fs = new BatchFs("/d/1.txt", "/d/2.txt");
        var engine = new RenameEngine(fs);
        var done = await engine.ExecuteBatchAsync(
            [Entry("/d/1.txt"), Entry("/d/2.txt")], [Plan("1.txt", "2.txt"), Plan("2.txt", "3.txt")]);

        var undone = await engine.UndoBatchAsync(done.Performed);

        undone.Succeeded.Should().BeTrue();
        fs.Batches.Should().HaveCount(2);
        fs.Contents["/d/1.txt"].Should().Be("was 1.txt");
        fs.Contents["/d/2.txt"].Should().Be("was 2.txt");
    }

    [Fact]
    public async Task ExecuteBatch_FileInsideARenamedFolder_IsRecordedUnderTheFoldersNewName()
    {
        var fs = new BatchFs("/d", "/d/a.txt");

        var result = await new RenameEngine(fs).ExecuteBatchAsync(
            [Entry("/d"), Entry("/d/a.txt")], [Plan("d", "e"), Plan("a.txt", "b.txt")]);

        // Where the undo has to find them: the file went along with its folder.
        result.Performed.Select(a => (a.CurrentPath.Value, a.OriginalName))
            .Should().Equal(("/e", "d"), ("/e/b.txt", "a.txt"));
    }

    [Fact]
    public async Task ExecuteBatch_ViewThatCannotBatch_RenamesOneByOne()
    {
        var fs = new BatchFs("/d/a.txt", "/d/b.txt") { CanRenameAsBatch = false };

        var result = await new RenameEngine(fs).ExecuteBatchAsync(
            [Entry("/d/a.txt"), Entry("/d/b.txt")], [Plan("a.txt", "x.txt"), Plan("b.txt", "y.txt")]);

        result.Succeeded.Should().BeTrue();
        fs.Batches.Should().BeEmpty();
        fs.SingleRenames.Should().HaveCount(2);
    }

    /// <summary>Files by path; a batch applies every rename at once, as an archive rewrite does.</summary>
    private sealed class BatchFs : IVirtualFileSystem, IBatchRenameFileSystem
    {
        private readonly Dictionary<string, string> _files;

        public BatchFs(params string[] paths) =>
            _files = paths.ToDictionary(p => p, p => "was " + new VfsPath(p).FileName, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyDictionary<string, string> Contents => _files;

        public List<List<(string Path, string NewName)>> Batches { get; } = [];

        public List<(string Path, string NewName)> SingleRenames { get; } = [];

        public string? Refusal { get; init; }

        public bool CanRenameAsBatch { get; init; } = true;

        public Task RenameBatchAsync(IReadOnlyList<(VfsPath Path, string NewName)> renames, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Refusal is not null)
            {
                throw new IOException(Refusal);
            }

            Batches.Add(renames.Select(r => (r.Path.Value, r.NewName)).ToList());
            var moved = renames.Select(r => (Target: r.Path.Parent!.Combine(r.NewName).Value, Content: _files[r.Path.Value])).ToList();
            foreach (var (path, _) in renames)
            {
                _files.Remove(path.Value);
            }

            foreach (var (target, content) in moved)
            {
                _files[target] = content;
            }

            return Task.CompletedTask;
        }

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct = default)
        {
            SingleRenames.Add((path.Value, newName));
            var content = _files[path.Value];
            _files.Remove(path.Value);
            _files[path.Parent!.Combine(newName).Value] = content;
            return Task.CompletedTask;
        }

        public string Scheme => "zip";
        public string DisplayName => "batch";
        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }
        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();
        public bool SupportsOperation(VfsOperation operation) => true;

        public void Dispose()
        {
        }
    }
}
