using System.IO.Compression;
using FluentAssertions;
using TCV.Archives.Engines;
using TCV.Core.Domain;

namespace TCV.Archives.Tests;

/// <summary>
/// Renaming entries and creating folders inside an archive (6.71): on a real ZIP through the engine and
/// through the archive's file system, and refused by every format the engine cannot write.
/// </summary>
public sealed class ArchiveEditTests : IDisposable
{
    private static readonly DateTime Stamp = new(2020, 1, 2, 3, 4, 6);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-edit-" + Guid.NewGuid().ToString("N"));

    public ArchiveEditTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>A ZIP of the given entries; a name ending in "/" is a directory entry.</summary>
    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_dir, "test.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = zip.CreateEntry(name, name.EndsWith("stored.txt") ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
            entry.LastWriteTime = Stamp;
            if (!name.EndsWith('/'))
            {
                using var writer = new StreamWriter(entry.Open());
                writer.Write(content);
            }
        }

        return path;
    }

    private static Dictionary<string, string> Contents(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return zip.Entries.ToDictionary(
            e => e.FullName,
            e => e.FullName.EndsWith('/') ? string.Empty : new StreamReader(e.Open()).ReadToEnd());
    }

    // ── The engine ───────────────────────────────────────────────────────────

    [Fact]
    public void RenameEntries_File_KeepsItsContentAndTimeAndTheOtherEntries()
    {
        var zip = Zip(("a.txt", "AAA"), ("dir/b.txt", "BBB"));

        new ZipEngine().RenameEntries(zip, [("a.txt", "c.txt")], CancellationToken.None);

        Contents(zip).Should().Equal(new Dictionary<string, string> { ["c.txt"] = "AAA", ["dir/b.txt"] = "BBB" });
        using var read = ZipFile.OpenRead(zip);
        read.GetEntry("c.txt")!.LastWriteTime.DateTime.Should().Be(Stamp);
    }

    [Fact]
    public void RenameEntries_StoredEntry_StaysStored()
    {
        var zip = Zip(("stored.txt", new string('x', 4000)));

        new ZipEngine().RenameEntries(zip, [("stored.txt", "still-stored.txt")], CancellationToken.None);

        using var read = ZipFile.OpenRead(zip);
        var entry = read.GetEntry("still-stored.txt")!;
        entry.CompressedLength.Should().Be(entry.Length);
    }

    [Fact]
    public void RenameEntries_Folder_TakesEverythingInsideAlong()
    {
        var zip = Zip(("dir/", ""), ("dir/a.txt", "A"), ("dir/sub/b.txt", "B"), ("other.txt", "O"));

        new ZipEngine().RenameEntries(zip, [("dir", "docs")], CancellationToken.None);

        Contents(zip).Should().Equal(new Dictionary<string, string>
        {
            ["docs/"] = string.Empty,
            ["docs/a.txt"] = "A",
            ["docs/sub/b.txt"] = "B",
            ["other.txt"] = "O",
        });
    }

    [Fact]
    public void RenameEntries_Swap_ExchangesTheNamesInOneEdit()
    {
        var zip = Zip(("a.txt", "was a"), ("b.txt", "was b"));

        new ZipEngine().RenameEntries(zip, [("a.txt", "b.txt"), ("b.txt", "a.txt")], CancellationToken.None);

        Contents(zip).Should().Equal(new Dictionary<string, string> { ["a.txt"] = "was b", ["b.txt"] = "was a" });
    }

    [Fact]
    public void RenameEntries_NameTaken_LeavesTheArchiveAsItWas()
    {
        var zip = Zip(("a.txt", "A"), ("b.txt", "B"));
        var before = File.ReadAllBytes(zip);

        var act = () => new ZipEngine().RenameEntries(zip, [("a.txt", "B.TXT")], CancellationToken.None);

        act.Should().Throw<IOException>();
        File.ReadAllBytes(zip).Should().Equal(before);
        Directory.GetFiles(_dir).Should().ContainSingle("the scratch copy is removed");
    }

    [Fact]
    public void RenameEntries_EncryptedEntry_IsRefusedAndTheArchiveLeftAsItWas()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        var src = Path.Combine(_dir, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "secret.txt"), "classified");
        var zip = Path.Combine(_dir, "enc.zip");
        Fixtures.RunSevenZip(src, "a", "-tzip", "-mem=AES256", "-ppw123", zip, "secret.txt");
        var before = File.ReadAllBytes(zip);

        var act = () => new ZipEngine("pw123").RenameEntries(zip, [("secret.txt", "open.txt")], CancellationToken.None);

        act.Should().Throw<ArchiveEntryEncryptedException>().Which.EntryName.Should().Be("secret.txt");
        File.ReadAllBytes(zip).Should().Equal(before);
    }

    [Fact]
    public void AddFiles_DirectoryName_AddsAnEmptyFolderThatExtracts()
    {
        var zip = Zip(("a.txt", "A"));

        new ZipEngine().AddFiles(zip, [(string.Empty, "docs/new/")], CancellationToken.None);
        new ZipEngine().ExtractAll(zip, Path.Combine(_dir, "x"));

        Directory.Exists(Path.Combine(_dir, "x", "docs", "new")).Should().BeTrue();
    }

    // ── Which formats ────────────────────────────────────────────────────────

    public static TheoryData<string, bool> Engines => new()
    {
        { "zip", true },
        { "7z", false },
        { "rar", false },
        { "tar", false },
        { "iso", false },
        { "cab", false },
    };

    [Theory]
    [MemberData(nameof(Engines))]
    public void SupportsOperation_RenameAndNewFolder_OnlyWhereTheEngineCanWrite(string format, bool writable)
    {
        using var fs = new ArchiveFileSystem(Path.Combine(_dir, "x." + format), new ArchiveEngineFactory().Create(format));

        fs.SupportsOperation(VfsOperation.Rename).Should().Be(writable);
        fs.SupportsOperation(VfsOperation.CreateDirectory).Should().Be(writable);
        fs.CanRenameAsBatch.Should().Be(writable);
    }

    [Fact]
    public async Task RenameAndCreateDirectory_ReadOnlyFormat_AreRefusedWithoutTouchingTheArchive()
    {
        var src = Path.Combine(_dir, "a.txt");
        File.WriteAllText(src, "A");
        var tar = Path.Combine(_dir, "t.tar");
        new TarEngine().Pack(tar, [(src, "a.txt")]);
        var before = File.ReadAllBytes(tar);
        using var fs = new ArchiveFileSystem(tar, new TarEngine());

        var rename = () => fs.RenameAsync(new VfsPath("/a.txt"), "b.txt", CancellationToken.None);
        var mkdir = () => fs.CreateDirectoryAsync(new VfsPath("/new"), CancellationToken.None);

        await rename.Should().ThrowAsync<NotSupportedException>();
        await mkdir.Should().ThrowAsync<NotSupportedException>();
        File.ReadAllBytes(tar).Should().Equal(before);
    }

    // ── Through the archive's file system ────────────────────────────────────

    [Fact]
    public async Task RenameAsync_ListingAfterwards_ShowsTheNewName()
    {
        var zip = Zip(("dir/a.txt", "A"), ("b.txt", "B"));
        using var fs = new ArchiveFileSystem(zip, new ZipEngine());
        (await fs.ListAsync(new VfsPath("/dir"))).Should().ContainSingle(e => e.Name == "a.txt");   // cached now

        await fs.RenameAsync(new VfsPath("/dir/a.txt"), "c.txt", CancellationToken.None);

        (await fs.ListAsync(new VfsPath("/dir"))).Select(e => e.Name).Should().Equal("c.txt");
        (await fs.GetEntryAsync(new VfsPath("/dir/a.txt"))).Should().BeNull();
    }

    [Fact]
    public async Task RenameBatchAsync_SeveralEntries_AreRenamedTogether()
    {
        var zip = Zip(("1.txt", "one"), ("2.txt", "two"), ("dir/3.txt", "three"));
        using var fs = new ArchiveFileSystem(zip, new ZipEngine());

        await fs.RenameBatchAsync(
            [(new VfsPath("/1.txt"), "2.txt"), (new VfsPath("/2.txt"), "3.txt"), (new VfsPath("/dir"), "d")],
            CancellationToken.None);

        Contents(zip).Should().Equal(new Dictionary<string, string>
        {
            ["2.txt"] = "one",
            ["3.txt"] = "two",
            ["d/3.txt"] = "three",
        });
    }

    [Fact]
    public async Task CreateDirectoryAsync_EmptyFolder_IsListed()
    {
        var zip = Zip(("docs/readme.txt", "R"));
        using var fs = new ArchiveFileSystem(zip, new ZipEngine());
        await fs.ListAsync(new VfsPath("/docs"));

        await fs.CreateDirectoryAsync(new VfsPath("/docs/new"), CancellationToken.None);

        (await fs.ListAsync(new VfsPath("/docs"))).Should().Contain(e => e.Name == "new" && e.IsDirectory);
        Contents(zip).Keys.Should().Contain("docs/new/");
    }
}
