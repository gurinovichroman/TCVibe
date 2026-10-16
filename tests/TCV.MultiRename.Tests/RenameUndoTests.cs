using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.MultiRename.Tests;

/// <summary>Covers <see cref="RenameEngine.ExecuteAsync"/> returning its actions and
/// <see cref="RenameEngine.UndoAsync"/> reverting them.</summary>
public class RenameUndoTests
{
    private static IFileEntry Entry(string fullPath)
    {
        var mock = new Mock<IFileEntry>();
        var path = new VfsPath(fullPath);
        mock.Setup(e => e.Name).Returns(path.FileName);
        mock.Setup(e => e.Path).Returns(path);
        return mock.Object;
    }

    [Fact]
    public async Task Execute_ReturnsPerformedActionsWithNewPathsAndOriginalNames()
    {
        var fs = new FakeRenameFs("/docs/a.txt", "/docs/b.txt");
        var files = new[] { Entry("/docs/a.txt"), Entry("/docs/b.txt") };
        var engine = new RenameEngine(fs);
        var previews = new[]
        {
            new RenamePreviewItem("a.txt", "1.txt", false),
            new RenamePreviewItem("b.txt", "2.txt", false),
        };

        var actions = await engine.ExecuteAsync(files, previews);

        actions.Should().HaveCount(2);
        actions[0].CurrentPath.Value.Should().Be("/docs/1.txt");
        actions[0].OriginalName.Should().Be("a.txt");
        fs.Names.Should().BeEquivalentTo("1.txt", "2.txt");
    }

    [Fact]
    public async Task Execute_SkipsConflictsAndNoOpRenames()
    {
        var fs = new FakeRenameFs("/docs/a.txt", "/docs/b.txt", "/docs/c.txt");
        var files = new[] { Entry("/docs/a.txt"), Entry("/docs/b.txt"), Entry("/docs/c.txt") };
        var engine = new RenameEngine(fs);
        var previews = new[]
        {
            new RenamePreviewItem("a.txt", "a.txt", false),   // unchanged → skipped
            new RenamePreviewItem("b.txt", "dup.txt", true),  // conflict → skipped
            new RenamePreviewItem("c.txt", "z.txt", false),   // renamed
        };

        var actions = await engine.ExecuteAsync(files, previews);

        actions.Should().ContainSingle();
        actions[0].CurrentPath.Value.Should().Be("/docs/z.txt");
        fs.Names.Should().BeEquivalentTo("a.txt", "b.txt", "z.txt");
    }

    [Fact]
    public async Task Undo_RestoresOriginalNames()
    {
        var fs = new FakeRenameFs("/docs/a.txt", "/docs/b.txt");
        var files = new[] { Entry("/docs/a.txt"), Entry("/docs/b.txt") };
        var engine = new RenameEngine(fs);
        var previews = new[]
        {
            new RenamePreviewItem("a.txt", "1.txt", false),
            new RenamePreviewItem("b.txt", "2.txt", false),
        };

        var actions = await engine.ExecuteAsync(files, previews);
        var failed = await engine.UndoAsync(actions);

        failed.Should().Be(0);
        fs.Names.Should().BeEquivalentTo("a.txt", "b.txt");
    }

    [Fact]
    public async Task Undo_WithNothingPerformed_DoesNothing()
    {
        var fs = new FakeRenameFs("/docs/a.txt");
        var engine = new RenameEngine(fs);

        var failed = await engine.UndoAsync(Array.Empty<RenameAction>());

        failed.Should().Be(0);
        fs.Names.Should().BeEquivalentTo("a.txt");
    }

    /// <summary>Minimal in-memory FS that only tracks file paths and honours RenameAsync.</summary>
    private sealed class FakeRenameFs : IVirtualFileSystem
    {
        private readonly List<string> _paths;

        public FakeRenameFs(params string[] paths) => _paths = paths.Select(p => new VfsPath(p).Value).ToList();

        public IEnumerable<string> Names => _paths.Select(p => new VfsPath(p).FileName);

        public string Scheme => "file";
        public string DisplayName => "fake";

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct = default)
        {
            int idx = _paths.FindIndex(p => string.Equals(p, path.Value, StringComparison.OrdinalIgnoreCase));
            if (idx < 0) throw new FileNotFoundException(path.Value);

            var parent = path.Parent;
            var target = (parent is not null ? parent.Combine(newName) : new VfsPath(newName)).Value;
            if (_paths.Any(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase)))
                throw new IOException($"Target exists: {target}");

            _paths[idx] = target;
            return Task.CompletedTask;
        }

        // Unused members
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
        public void Dispose() { }
    }
}
