using System.IO.Compression;
using FluentAssertions;
using TCV.Archives.Engines;

namespace TCV.Archives.Tests;

public class ZipEngineTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly ZipEngine _engine = new();

    public ZipEngineTests() => Directory.CreateDirectory(_tempDir);
    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private string CreateZip(params (string name, string content)[] entries)
    {
        var path = Path.Combine(_tempDir, "test.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name);
            using var stream = entry.Open();
            using var writer = new StreamWriter(stream);
            writer.Write(content);
        }
        return path;
    }

    [Fact]
    public void ListAll_FlatZip_ReturnsAllFiles()
    {
        var path = CreateZip(("a.txt", "aaa"), ("b.txt", "bbb"));
        var entries = _engine.ListAll(path);

        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Name == "a.txt" && !e.IsDirectory);
        entries.Should().Contain(e => e.Name == "b.txt" && !e.IsDirectory);
    }

    [Fact]
    public void ListAll_NestedZip_CreatesImplicitDirectories()
    {
        var path = CreateZip(("folder/sub/file.txt", "content"));
        var entries = _engine.ListAll(path);

        entries.Should().Contain(e => e.FullPath == "folder" && e.IsDirectory);
        entries.Should().Contain(e => e.FullPath == "folder/sub" && e.IsDirectory);
        entries.Should().Contain(e => e.FullPath == "folder/sub/file.txt" && !e.IsDirectory);
    }

    [Fact]
    public void ExtractEntry_ExistingEntry_ReturnsContent()
    {
        var path = CreateZip(("hello.txt", "Hello, World!"));
        using var stream = _engine.ExtractEntry(path, "hello.txt");
        using var reader = new StreamReader(stream);
        reader.ReadToEnd().Should().Be("Hello, World!");
    }

    [Fact]
    public void ExtractEntry_MissingEntry_ThrowsFileNotFoundException()
    {
        var path = CreateZip(("a.txt", "a"));
        var act = () => _engine.ExtractEntry(path, "missing.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void Pack_CreatesValidZip()
    {
        var src = Path.Combine(_tempDir, "source.txt");
        File.WriteAllText(src, "packed content");
        var dest = Path.Combine(_tempDir, "packed.zip");

        _engine.Pack(dest, [(src, "source.txt")]);

        File.Exists(dest).Should().BeTrue();
        using var zip = ZipFile.OpenRead(dest);
        zip.Entries.Should().ContainSingle(e => e.Name == "source.txt");
    }

    [Fact]
    public void ExtractAll_ExtractsAllFiles()
    {
        var path = CreateZip(("a.txt", "aaa"), ("b/c.txt", "ccc"));
        var dest = Path.Combine(_tempDir, "extracted");
        Directory.CreateDirectory(dest);

        _engine.ExtractAll(path, dest);

        File.ReadAllText(Path.Combine(dest, "a.txt")).Should().Be("aaa");
        File.ReadAllText(Path.Combine(dest, "b", "c.txt")).Should().Be("ccc");
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("b/../../evil.txt")]
    [InlineData(@"..\evil.txt")]
    public void ExtractAll_EntryEscapingDestination_ThrowsAndWritesNothing(string entryName)
    {
        var path = CreateZip((entryName, "pwned"));
        var dest = Path.Combine(_tempDir, "extracted");
        Directory.CreateDirectory(dest);

        var act = () => _engine.ExtractAll(path, dest);

        act.Should().Throw<InvalidDataException>();
        File.Exists(Path.Combine(_tempDir, "evil.txt")).Should().BeFalse();
    }

    [Fact]
    public void AddFiles_SourceLockedByAnotherProgram_KeepsTheExistingEntry()
    {
        var path = CreateZip(("app.log", "OLD"), ("other.txt", "keep"));
        var local = Path.Combine(_tempDir, "app.log");
        File.WriteAllText(local, "NEW");

        using (new FileStream(local, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            var act = () => _engine.AddFiles(path, [(local, "app.log")]);
            act.Should().Throw<IOException>();
        }

        using var stream = _engine.ExtractEntry(path, "app.log");
        new StreamReader(stream).ReadToEnd().Should().Be("OLD");
        _engine.ListAll(path).Should().Contain(e => e.FullPath == "other.txt");
    }

    [Fact]
    public void AddFiles_ExistingEntry_ReplacesItsContent()
    {
        var path = CreateZip(("a.txt", "OLD"));
        var local = Path.Combine(_tempDir, "a.txt");
        File.WriteAllText(local, "NEW");

        _engine.AddFiles(path, [(local, "a.txt")]);

        using var stream = _engine.ExtractEntry(path, "a.txt");
        new StreamReader(stream).ReadToEnd().Should().Be("NEW");
        _engine.ListAll(path).Should().ContainSingle(e => e.FullPath == "a.txt");
    }

    // ── Edits go through a scratch copy (6.35) ───────────────────────────────

    private string[] ScratchFiles() => Directory.GetFiles(_tempDir, ".tcv-*");

    [Fact]
    public void AddFiles_SourceFailsAfterOthersWereAdded_LeavesTheArchiveByteForByte()
    {
        var path = CreateZip(("keep.txt", "KEEP"));
        var before = File.ReadAllBytes(path);
        var first = Path.Combine(_tempDir, "first.txt");
        var locked = Path.Combine(_tempDir, "locked.txt");
        File.WriteAllText(first, "1");
        File.WriteAllText(locked, "2");

        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var act = () => _engine.AddFiles(path, [(first, "first.txt"), (locked, "locked.txt")]);
            act.Should().Throw<IOException>();
        }

        File.ReadAllBytes(path).Should().Equal(before);
        ScratchFiles().Should().BeEmpty();
    }

    [Fact]
    public void AddFiles_CancelledBetweenEntries_LeavesTheArchiveUntouched()
    {
        var path = CreateZip(("keep.txt", "KEEP"));
        var before = File.ReadAllBytes(path);
        var a = Path.Combine(_tempDir, "a.txt");
        var b = Path.Combine(_tempDir, "b.txt");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");
        using var cts = new CancellationTokenSource();

        IEnumerable<(string, string)> CancelAfterFirst()
        {
            yield return (a, "a.txt");
            cts.Cancel();
            yield return (b, "b.txt");
        }

        var act = () => _engine.AddFiles(path, CancelAfterFirst(), cts.Token);

        act.Should().Throw<OperationCanceledException>();
        File.ReadAllBytes(path).Should().Equal(before);
        ScratchFiles().Should().BeEmpty();
    }

    [Fact]
    public void AddFiles_ReadOnlyArchive_IsRefusedAndLeftAlone()
    {
        var path = CreateZip(("keep.txt", "KEEP"));
        var local = Path.Combine(_tempDir, "new.txt");
        File.WriteAllText(local, "NEW");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        try
        {
            var act = () => _engine.AddFiles(path, [(local, "new.txt")]);

            act.Should().Throw<UnauthorizedAccessException>();
            _engine.ListAll(path).Select(e => e.FullPath).Should().BeEquivalentTo("keep.txt");
            ScratchFiles().Should().BeEmpty();
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void AddFiles_MissingArchive_CreatesIt()
    {
        var path = Path.Combine(_tempDir, "fresh.zip");
        var local = Path.Combine(_tempDir, "a.txt");
        File.WriteAllText(local, "A");

        _engine.AddFiles(path, [(local, "a.txt")]);

        _engine.ListAll(path).Select(e => e.FullPath).Should().BeEquivalentTo("a.txt");
        ScratchFiles().Should().BeEmpty();
    }

    [Fact]
    public void DeleteEntries_FilesAndAFolder_RemovesThemAllInOneEdit()
    {
        var path = CreateZip(
            ("a.txt", "a"), ("b.txt", "b"), ("keep.txt", "k"), ("dir/one.txt", "1"), ("dir/sub/two.txt", "2"));

        _engine.DeleteEntries(path, ["a.txt", "b.txt", "dir"], CancellationToken.None);

        _engine.ListAll(path).Select(e => e.FullPath).Should().BeEquivalentTo("keep.txt");
        ScratchFiles().Should().BeEmpty();
    }

    [Fact]
    public void DeleteEntries_Cancelled_LeavesTheArchiveUntouched()
    {
        var path = CreateZip(("a.txt", "a"), ("b.txt", "b"));
        var before = File.ReadAllBytes(path);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => _engine.DeleteEntries(path, ["a.txt", "b.txt"], cts.Token);

        act.Should().Throw<OperationCanceledException>();
        File.ReadAllBytes(path).Should().Equal(before);
        ScratchFiles().Should().BeEmpty();
    }

    [Fact]
    public void AddFiles_ArchiveReachedThroughASymlink_EditsTheRealArchiveAndKeepsTheLink()
    {
        var real = CreateZip(("keep.txt", "KEEP"));
        var link = Path.Combine(_tempDir, "link.zip");
        try
        {
            File.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;   // creating a symlink needs Developer Mode or elevation; nothing to check without one
        }

        var local = Path.Combine(_tempDir, "new.txt");
        File.WriteAllText(local, "NEW");

        _engine.AddFiles(link, [(local, "new.txt")]);

        new FileInfo(link).LinkTarget.Should().NotBeNull();
        _engine.ListAll(real).Select(e => e.FullPath).Should().BeEquivalentTo("keep.txt", "new.txt");
        ScratchFiles().Should().BeEmpty();
    }

    [Fact]
    public void DeleteEntries_NothingToDelete_LeavesTheArchiveUntouched()
    {
        var path = CreateZip(("a.txt", "a"));
        var stamp = File.GetLastWriteTimeUtc(path).AddMinutes(-5);
        File.SetLastWriteTimeUtc(path, stamp);

        _engine.DeleteEntries(path, [], CancellationToken.None);

        File.GetLastWriteTimeUtc(path).Should().Be(stamp);
    }

    [Fact]
    public void DeleteEntries_MissingArchive_ThrowsInsteadOfCreatingOne()
    {
        var path = Path.Combine(_tempDir, "missing.zip");

        var act = () => _engine.DeleteEntries(path, ["a.txt"], CancellationToken.None);

        act.Should().Throw<FileNotFoundException>();
        File.Exists(path).Should().BeFalse();
    }
}
