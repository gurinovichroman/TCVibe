using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.MultiRename.Rules;

namespace TCV.MultiRename.Tests;

/// <summary>Ordering, partial failure, cycles and preview conflicts of <see cref="RenameEngine"/>.</summary>
public class RenameBatchTests
{
    private static IFileEntry Entry(string fullPath, bool isDirectory = false)
    {
        var mock = new Mock<IFileEntry>();
        var path = new VfsPath(fullPath);
        mock.Setup(e => e.Name).Returns(path.FileName);
        mock.Setup(e => e.Path).Returns(path);
        mock.Setup(e => e.IsDirectory).Returns(isDirectory);
        return mock.Object;
    }

    private static RenamePreviewItem Plan(string oldName, string newName) => new(oldName, newName, false);

    // ── Ordering and cycles ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteBatch_SwapOfTwoNames_Works()
    {
        var fs = new FakeFs("/d/a.txt", "/d/b.txt");
        var engine = new RenameEngine(fs);

        var result = await engine.ExecuteBatchAsync(
            [Entry("/d/a.txt"), Entry("/d/b.txt")], [Plan("a.txt", "b.txt"), Plan("b.txt", "a.txt")]);

        result.Succeeded.Should().BeTrue();
        result.Performed.Should().HaveCount(2);
        fs.Names.Should().BeEquivalentTo("a.txt", "b.txt");
        fs.Contents["/d/b.txt"].Should().Be("was a.txt");
        fs.Contents["/d/a.txt"].Should().Be("was b.txt");
        fs.Names.Should().NotContain(n => n.StartsWith(".tcv-rename-"));
    }

    [Fact]
    public async Task ExecuteBatch_Chain_RenamesTheTailFirst()
    {
        var fs = new FakeFs("/d/1.txt", "/d/2.txt", "/d/3.txt");
        var engine = new RenameEngine(fs);

        var result = await engine.ExecuteBatchAsync(
            [Entry("/d/1.txt"), Entry("/d/2.txt"), Entry("/d/3.txt")],
            [Plan("1.txt", "2.txt"), Plan("2.txt", "3.txt"), Plan("3.txt", "4.txt")]);

        result.Succeeded.Should().BeTrue();
        fs.Names.Should().BeEquivalentTo("2.txt", "3.txt", "4.txt");
        fs.Contents["/d/4.txt"].Should().Be("was 3.txt");
        fs.Contents["/d/2.txt"].Should().Be("was 1.txt");
    }

    [Fact]
    public async Task ExecuteBatch_ThreeCycle_UsesOneTemporaryNameAndLeavesNone()
    {
        var fs = new FakeFs("/d/a", "/d/b", "/d/c");
        var engine = new RenameEngine(fs);

        var result = await engine.ExecuteBatchAsync(
            [Entry("/d/a"), Entry("/d/b"), Entry("/d/c")],
            [Plan("a", "b"), Plan("b", "c"), Plan("c", "a")]);

        result.Succeeded.Should().BeTrue();
        fs.Contents["/d/b"].Should().Be("was a");
        fs.Contents["/d/c"].Should().Be("was b");
        fs.Contents["/d/a"].Should().Be("was c");
        fs.RenameCalls.Count(c => c.NewName.StartsWith(".tcv-rename-")).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteBatch_TailFeedingIntoACycle_DoesNotLoopForever()
    {
        // x waits on a; a and b swap. Parking x would change nothing — a cycle member has to move.
        var fs = new FakeFs("/d/x", "/d/a", "/d/b");
        var engine = new RenameEngine(fs);

        var result = await engine.ExecuteBatchAsync(
            [Entry("/d/x"), Entry("/d/a"), Entry("/d/b")],
            [Plan("x", "a"), Plan("a", "b"), Plan("b", "a")]);

        // x→a and b→a collide by design (a duplicate target the preview would have flagged). The
        // engine must terminate, and anything it could not put back must be reported, not hidden.
        (result.Performed.Count + result.Failures.Count).Should().BeGreaterThan(0);
        var stranded = fs.Names.Count(n => n.StartsWith(".tcv-rename-"));
        result.Failures.Count(f => f.Error.Contains("temporary name")).Should().Be(stranded);
    }

    [Fact]
    public async Task ExecuteBatch_CaseOnlyChange_IsRenamedDirectly()
    {
        var fs = new FakeFs("/d/readme.txt");
        var engine = new RenameEngine(fs);

        var result = await engine.ExecuteBatchAsync([Entry("/d/readme.txt")], [Plan("readme.txt", "README.txt")]);

        result.Succeeded.Should().BeTrue();
        fs.Names.Should().Equal("README.txt");
    }

    // ── Partial failure ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteBatch_OneRenameFails_TheOthersStayRenamedAndAreUndoable()
    {
        var fs = new FakeFs("/d/a.txt", "/d/b.txt", "/d/c.txt") { FailWhenNewNameIs = "2.txt" };
        var engine = new RenameEngine(fs);
        var files = new[] { Entry("/d/a.txt"), Entry("/d/b.txt"), Entry("/d/c.txt") };

        var result = await engine.ExecuteBatchAsync(files, [Plan("a.txt", "1.txt"), Plan("b.txt", "2.txt"), Plan("c.txt", "3.txt")]);

        result.Succeeded.Should().BeFalse();
        result.Performed.Select(p => p.CurrentPath.FileName).Should().BeEquivalentTo("1.txt", "3.txt");
        result.Failures.Should().ContainSingle().Which.OldName.Should().Be("b.txt");
        fs.Names.Should().BeEquivalentTo("1.txt", "b.txt", "3.txt");

        var failed = await engine.UndoAsync(result.Performed);
        failed.Should().Be(0);
        fs.Names.Should().BeEquivalentTo("a.txt", "b.txt", "c.txt");
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_ThrowsWithTheSuccessesAttached()
    {
        var fs = new FakeFs("/d/a.txt", "/d/b.txt") { FailWhenNewNameIs = "2.txt" };
        var engine = new RenameEngine(fs);

        var act = () => engine.ExecuteAsync(
            [Entry("/d/a.txt"), Entry("/d/b.txt")], [Plan("a.txt", "1.txt"), Plan("b.txt", "2.txt")]);

        var thrown = await act.Should().ThrowAsync<RenameBatchException>();
        thrown.Which.Result.Performed.Should().ContainSingle();
    }

    [Fact]
    public async Task ExecuteBatch_CancelledInTheMiddleOfACycle_PutsTheParkedFileBack()
    {
        using var cts = new CancellationTokenSource();
        var fs = new FakeFs("/d/a", "/d/b") { OnRename = (calls) => { if (calls == 1) cts.Cancel(); } };
        var engine = new RenameEngine(fs);

        var result = await engine.ExecuteBatchAsync(
            [Entry("/d/a"), Entry("/d/b")], [Plan("a", "b"), Plan("b", "a")], cts.Token);

        result.Cancelled.Should().BeTrue();
        fs.Names.Should().BeEquivalentTo("a", "b");
        fs.Names.Should().NotContain(n => n.StartsWith(".tcv-rename-"));
        fs.Contents["/d/a"].Should().Be("was a");
    }

    [Fact]
    public async Task Undo_OfASwap_RestoresBothNames()
    {
        var fs = new FakeFs("/d/a.txt", "/d/b.txt");
        var engine = new RenameEngine(fs);
        var result = await engine.ExecuteBatchAsync(
            [Entry("/d/a.txt"), Entry("/d/b.txt")], [Plan("a.txt", "b.txt"), Plan("b.txt", "a.txt")]);

        var failed = await engine.UndoAsync(result.Performed);

        failed.Should().Be(0);
        fs.Contents["/d/a.txt"].Should().Be("was a.txt");
        fs.Contents["/d/b.txt"].Should().Be("was b.txt");
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    [Fact]
    public void Preview_NameAlreadyInTheFolder_IsAConflict()
    {
        var engine = new RenameEngine(Mock.Of<IVirtualFileSystem>());

        var previews = engine.Preview([Entry("/d/a.txt")], new RenameTemplate("taken.txt"),
            otherNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Taken.TXT" });

        previews[0].Kind.Should().Be(ConflictKind.ExistsInFolder);
        previews[0].HasConflict.Should().BeTrue();
    }

    [Fact]
    public void Preview_RenamingOntoAFileThatKeepsItsName_IsADuplicate()
    {
        var engine = new RenameEngine(Mock.Of<IVirtualFileSystem>());

        // Y→"x.txt" while X keeps "x.txt": order in the list must not hide the collision.
        var previews = engine.Preview([Entry("/d/y.txt"), Entry("/d/x.txt")], new RenameTemplate("x.txt"));

        previews[0].Kind.Should().Be(ConflictKind.Duplicate);
        previews[1].HasConflict.Should().BeFalse();
    }

    [Theory]
    [InlineData("", ConflictKind.Empty)]
    [InlineData("a:b.txt", ConflictKind.InvalidName)]
    [InlineData("name.", ConflictKind.InvalidName)]
    [InlineData("name ", ConflictKind.InvalidName)]
    [InlineData("CON", ConflictKind.InvalidName)]
    [InlineData("nul.txt", ConflictKind.InvalidName)]
    [InlineData("fine.txt", ConflictKind.None)]
    public void Validate_ClassifiesNames(string name, ConflictKind expected)
    {
        RenameEngine.Validate(name).Should().Be(expected);
    }

    [Fact]
    public void Validate_NameLongerThan255_IsTooLong()
    {
        RenameEngine.Validate(new string('a', 256)).Should().Be(ConflictKind.TooLong);
    }

    [Fact]
    public void Preview_ExtensionlessFileWithDefaultTemplate_KeepsItsNameWithoutATrailingDot()
    {
        var engine = new RenameEngine(Mock.Of<IVirtualFileSystem>());

        var previews = engine.Preview([Entry("/d/Makefile")], new RenameTemplate("[N].[E]"));

        previews[0].NewName.Should().Be("Makefile");
        previews[0].HasConflict.Should().BeFalse();
    }

    [Fact]
    public void Preview_Folder_IsNotSplitIntoStemAndExtension()
    {
        var engine = new RenameEngine(Mock.Of<IVirtualFileSystem>());
        var rules = new IRenameRule[] { new NameOnlyRule(new InsertRule("_old", 100)) };

        var previews = engine.Preview([Entry("/d/release.v2", isDirectory: true)], new RenameTemplate("[N].[E]"), rules);

        previews[0].NewName.Should().Be("release.v2_old", "a folder has no extension to protect");
    }

    [Fact]
    public void Preview_CounterWidth_PadsWithZeros()
    {
        var engine = new RenameEngine(Mock.Of<IVirtualFileSystem>());

        var previews = engine.Preview([Entry("/d/a.jpg"), Entry("/d/b.jpg")], new RenameTemplate("img_[C].[E]"), counterWidth: 3);

        previews.Select(p => p.NewName).Should().Equal("img_001.jpg", "img_002.jpg");
    }

    [Fact]
    public void Preview_ExplicitWidthAndOwnStart_OverrideTheDialogCounter()
    {
        var engine = new RenameEngine(Mock.Of<IVirtualFileSystem>());

        var previews = engine.Preview([Entry("/d/a.jpg"), Entry("/d/b.jpg")], new RenameTemplate("[C10+5:4]"), counterStart: 99);

        previews.Select(p => p.NewName).Should().Equal("0010", "0015");
    }

    [Fact]
    public void RegexReplaceRule_CatastrophicPattern_FailsFastInsteadOfHanging()
    {
        var rule = new RegexReplaceRule("(a+)+$", "x");
        var hostile = new string('a', 60) + "!";

        var act = () => rule.Apply(hostile);

        act.Should().Throw<System.Text.RegularExpressions.RegexMatchTimeoutException>();
    }

    // ── Fake file system ─────────────────────────────────────────────────────

    /// <summary>Tracks paths and contents; content follows the file through renames.</summary>
    private sealed class FakeFs : IVirtualFileSystem
    {
        private readonly Dictionary<string, string> _files;

        public FakeFs(params string[] paths) =>
            _files = paths.ToDictionary(p => new VfsPath(p).Value, p => "was " + new VfsPath(p).FileName, StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Names => _files.Keys.Select(p => new VfsPath(p).FileName);

        public IReadOnlyDictionary<string, string> Contents => _files;

        public List<(string From, string NewName)> RenameCalls { get; } = [];

        public string? FailWhenNewNameIs { get; init; }

        public Action<int>? OnRename { get; init; }

        public string Scheme => "file";
        public string DisplayName => "fake";

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RenameCalls.Add((path.Value, newName));
            OnRename?.Invoke(RenameCalls.Count);

            if (newName == FailWhenNewNameIs)
            {
                throw new IOException("disk says no");
            }

            if (!_files.TryGetValue(path.Value, out var content))
            {
                throw new FileNotFoundException(path.Value);
            }

            var target = (path.Parent is { } parent ? parent.Combine(newName) : new VfsPath(newName)).Value;
            if (_files.ContainsKey(target) && !string.Equals(target, path.Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Target exists: " + target);
            }

            _files.Remove(path.Value);
            _files[target] = content;
            return Task.CompletedTask;
        }

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
