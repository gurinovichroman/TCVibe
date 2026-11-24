using System.Formats.Tar;
using System.IO.Compression;
using FluentAssertions;
using TCV.Archives.Engines;
using TCV.Core.Domain;

namespace TCV.Archives.Tests;

/// <summary>
/// A TAR as other programs write it (TRAP-99): PAX headers are no files, the long name they carry is the
/// entry's name, and a compressed TAR holds all its entries.
/// </summary>
public sealed class TarPaxTests : IDisposable
{
    private static readonly string LongName = new string('n', 150) + ".txt";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-pax-" + Guid.NewGuid().ToString("N"));

    public TarPaxTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>What <see cref="TarFile"/> writes by default: PAX, a header in front of every entry.</summary>
    private string PackWithDotNet(string name = "t.tar")
    {
        var source = Path.Combine(_dir, "src");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        File.WriteAllText(Path.Combine(source, "a.txt"), "AAA");
        File.WriteAllText(Path.Combine(source, "sub", "b.txt"), "BBB");
        File.WriteAllText(Path.Combine(source, "sub", LongName), "LONG");

        var tar = Path.Combine(_dir, name);
        TarFile.CreateFromDirectory(source, tar, includeBaseDirectory: false);
        return tar;
    }

    private string Gzip(string path, string extension = ".tar.gz")
    {
        var gz = Path.ChangeExtension(path, null) + extension;
        using var source = File.OpenRead(path);
        using var target = File.Create(gz);
        using var zip = new GZipStream(target, CompressionLevel.Optimal);
        source.CopyTo(zip);
        return gz;
    }

    private static string[] Names(string archive) =>
        new TarEngine().ListAll(archive).Select(e => e.FullPath).ToArray();

    private static readonly string[] Expected = ["a.txt", "sub", "sub/b.txt", "sub/" + LongName];

    [Fact]
    public void ListAll_PaxTar_ShowsTheFilesAndNoPaxHeaders()
    {
        Names(PackWithDotNet()).Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public void ListAll_PaxTarGz_ShowsEveryEntry()
    {
        Names(Gzip(PackWithDotNet())).Should().BeEquivalentTo(Expected);
    }

    [Fact]
    public void ListAll_TarWrittenByTheEngineAndCompressed_ShowsEveryEntry()
    {
        var a = Path.Combine(_dir, "a.txt");
        var b = Path.Combine(_dir, "b.txt");
        File.WriteAllText(a, "AAA");
        File.WriteAllText(b, "BBB");

        foreach (var name in new[] { "t.tar.gz", "t.tgz", "t.tar.bz2" })
        {
            var archive = Path.Combine(_dir, name);
            new TarEngine().Pack(archive, [(a, "a.txt"), (b, "sub/b.txt")]);

            Names(archive).Should().BeEquivalentTo(["a.txt", "sub", "sub/b.txt"], name);
        }
    }

    [Theory]
    [InlineData(TarEntryFormat.V7)]
    [InlineData(TarEntryFormat.Ustar)]
    [InlineData(TarEntryFormat.Gnu)]
    [InlineData(TarEntryFormat.Pax)]
    public void ListAll_EveryTarFormat_ShowsItsFoldersAndFiles(TarEntryFormat format)
    {
        var tar = Path.Combine(_dir, format + ".tar");
        var local = Path.Combine(_dir, "local.txt");
        File.WriteAllText(local, "AAA");
        using (var stream = File.Create(tar))
        using (var writer = new TarWriter(stream, format))
        {
            writer.WriteEntry(local, "sub/a.txt");
            writer.WriteEntry(Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName, "empty/");
        }

        Names(tar).Should().BeEquivalentTo("sub", "sub/a.txt", "empty");
        new TarEngine().ListAll(tar).Single(e => e.FullPath == "empty").IsDirectory.Should().BeTrue();
    }

    [Fact]
    public void Names_LongNameWrittenByTheEngine_ComesBackWhole()
    {
        var local = Path.Combine(_dir, "local.txt");
        File.WriteAllText(local, "AAA");
        var tar = Path.Combine(_dir, "long.tar");
        var name = "sub/" + LongName;

        new TarEngine().Pack(tar, [(local, name)]);

        Names(tar).Should().BeEquivalentTo("sub", name);
    }

    [Fact]
    public void ListAll_GitArchiveGlobalHeader_IsNoEntry()
    {
        var tar = Path.Combine(_dir, "git.tar");
        using (var stream = File.Create(tar))
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxGlobalExtendedAttributesTarEntry(new Dictionary<string, string> { ["comment"] = "abc" }));
            var file = new PaxTarEntry(TarEntryType.RegularFile, "repo/a.txt") { DataStream = new MemoryStream("AAA"u8.ToArray()) };
            writer.WriteEntry(file);
        }

        Names(tar).Should().BeEquivalentTo("repo", "repo/a.txt");
    }

    [Fact]
    public void ExtractEntry_LongNameFromThePaxHeader_ReadsTheFile()
    {
        foreach (var archive in new[] { PackWithDotNet(), Gzip(PackWithDotNet("again.tar")) })
        {
            using var content = new TarEngine().ExtractEntry(archive, "sub/" + LongName);

            new StreamReader(content).ReadToEnd().Should().Be("LONG");
        }
    }

    [Fact]
    public void ExtractEntry_NoSuchEntry_ThrowsFileNotFound()
    {
        var tar = PackWithDotNet();

        var act = () => new TarEngine().ExtractEntry(tar, "gone.txt");

        act.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ReadEntries_TarGz_HandsOverEachFileOnce()
    {
        var archive = Gzip(PackWithDotNet());
        var handed = new Dictionary<string, string>();

        new TarEngine().ReadEntries(archive, ["sub/b.txt", "a.txt", "gone.txt"],
            (path, content) => handed.Add(path, new StreamReader(content).ReadToEnd()), CancellationToken.None);

        handed.Should().BeEquivalentTo(new Dictionary<string, string> { ["a.txt"] = "AAA", ["sub/b.txt"] = "BBB" });
    }

    [Fact]
    public void ExtractAll_PaxTarGz_RecreatesTheTreeWithItsLongName()
    {
        var archive = Gzip(PackWithDotNet());
        var dest = Path.Combine(_dir, "out");

        new TarEngine().ExtractAll(archive, dest);

        File.ReadAllText(Path.Combine(dest, "sub", "b.txt")).Should().Be("BBB");
        File.ReadAllText(Path.Combine(dest, "sub", LongName)).Should().Be("LONG");
        Directory.GetDirectories(dest).Select(Path.GetFileName).Should().Equal("sub");
    }

    [Fact]
    public void ExtractAll_EntryEscapingTheDestinationInATarGz_ThrowsAndWritesNothingOutside()
    {
        var tar = Path.Combine(_dir, "evil.tar");
        using (var stream = File.Create(tar))
        using (var writer = new TarWriter(stream, TarEntryFormat.Pax))
        {
            writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "../evil.txt") { DataStream = new MemoryStream("x"u8.ToArray()) });
        }

        var act = () => new TarEngine().ExtractAll(Gzip(tar), Path.Combine(_dir, "safe"));

        act.Should().Throw<Exception>();
        File.Exists(Path.Combine(_dir, "evil.txt")).Should().BeFalse();
    }

    [Fact]
    public void ListAll_LoneGzipFile_IsStillOneEntry()
    {
        var gz = Path.Combine(_dir, "notes.txt.gz");
        using (var target = File.Create(gz))
        using (var zip = new SharpCompress.Compressors.Deflate.GZipStream(target, SharpCompress.Compressors.CompressionMode.Compress) { FileName = "notes.txt" })
        using (var writer = new StreamWriter(zip))
        {
            writer.Write(new string('x', 2000));
        }

        var entries = new TarEngine().ListAll(gz);
        using var content = new TarEngine().ExtractEntry(gz, "notes.txt");

        entries.Where(e => !e.IsDirectory).Select(e => e.FullPath).Should().Equal("notes.txt");
        new StreamReader(content).ReadToEnd().Should().HaveLength(2000);
    }

    private string NamelessGzip(string fileName, byte[] content)
    {
        var gz = Path.Combine(_dir, fileName);
        using var target = File.Create(gz);
        using var zip = new System.IO.Compression.GZipStream(target, System.IO.Compression.CompressionMode.Compress);
        zip.Write(content);
        return gz;
    }

    [Fact]
    public void ListAll_GzipWithNoNameInItsHeader_IsOneEntryNamedAfterTheFile()
    {
        var gz = NamelessGzip("report.csv.gz", new byte[2000]);

        var file = new TarEngine().ListAll(gz).Single(e => !e.IsDirectory);

        file.FullPath.Should().Be("report.csv");
        file.Size.Should().Be(2000);
    }

    [Fact]
    public void Read_GzipWithNoNameInItsHeader_GivesItsContent()
    {
        var bytes = "name is not in the header"u8.ToArray();
        var gz = NamelessGzip("note.txt.gz", bytes);
        var engine = new TarEngine();

        using (var entry = engine.ExtractEntry(gz, "note.txt"))
        using (var copy = new MemoryStream())
        {
            entry.CopyTo(copy);
            copy.ToArray().Should().Equal(bytes);
        }

        byte[]? read = null;
        engine.ReadEntries(gz, ["NOTE.txt"], (_, content) =>
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            read = copy.ToArray();
        }, CancellationToken.None);
        read.Should().Equal(bytes);

        var dest = Path.Combine(_dir, "out");
        engine.ExtractAll(gz, dest, null, CancellationToken.None);
        File.ReadAllBytes(Path.Combine(dest, "note.txt")).Should().Equal(bytes);

        var act = () => engine.ExtractEntry(gz, "other.txt");
        act.Should().Throw<FileNotFoundException>();
    }

    private string Compressible()
    {
        var local = Path.Combine(_dir, "compressible.txt");
        File.WriteAllText(local, string.Concat(Enumerable.Repeat("the same line again and again\n", 4000)));
        return local;
    }

    [Theory]
    [InlineData("t.tar.gz")]
    [InlineData("t.tgz")]
    [InlineData("t.tar.bz2")]
    public void Pack_CompressedTar_IsSmallerThanWhatItHolds(string name)
    {
        var local = Compressible();
        var archive = Path.Combine(_dir, name);

        new TarEngine().Pack(archive, [(local, "a.txt")]);

        new FileInfo(archive).Length.Should().BeLessThan(new FileInfo(local).Length / 10);
        Names(archive).Should().Equal("a.txt");
        using var content = new TarEngine().ExtractEntry(archive, "a.txt");
        content.Length.Should().Be(new FileInfo(local).Length);
    }

    [Fact]
    public void Pack_TarGz_FollowsTheLevelAskedFor()
    {
        var local = Compressible();
        var stored = Path.Combine(_dir, "stored.tar.gz");
        var deflated = Path.Combine(_dir, "deflated.tar.gz");

        new TarEngine().Pack(stored, [(local, "a.txt")], System.IO.Compression.CompressionLevel.NoCompression, CancellationToken.None);
        new TarEngine().Pack(deflated, [(local, "a.txt")], System.IO.Compression.CompressionLevel.Optimal, CancellationToken.None);

        new FileInfo(stored).Length.Should().BeGreaterThan(new FileInfo(local).Length);
        new FileInfo(deflated).Length.Should().BeLessThan(new FileInfo(local).Length / 10);
        Names(stored).Should().Equal("a.txt");
    }

    /// <summary>What <c>tar -C dir -cf x.tar .</c> writes: the folder itself comes first, as <c>./</c>.</summary>
    private string TarOfADot()
    {
        var tar = Path.Combine(_dir, "dot.tar");
        using var stream = File.Create(tar);
        using var writer = new TarWriter(stream, TarEntryFormat.Gnu);
        writer.WriteEntry(new GnuTarEntry(TarEntryType.Directory, "./"));
        writer.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, "./a.txt") { DataStream = new MemoryStream("AAA"u8.ToArray()) });
        return tar;
    }

    private sealed class LastValue : IProgress<double>
    {
        public double Value { get; private set; }

        public void Report(double value) => Value = value;
    }

    [Fact]
    public void ExtractAll_TarOfADot_TakesTheRootEntryForTheDestination()
    {
        var dest = Path.Combine(_dir, "out");
        var progress = new LastValue();

        new TarEngine().ExtractAll(Gzip(TarOfADot()), dest, progress, CancellationToken.None);

        File.ReadAllText(Path.Combine(dest, "a.txt")).Should().Be("AAA");
        progress.Value.Should().Be(1d);
    }

    [Fact]
    public void ListAll_GzipOfAFileThatStartsWithZeros_IsStillOneEntry()
    {
        var gz = Path.Combine(_dir, "disk.img.gz");
        using (var target = File.Create(gz))
        using (var zip = new SharpCompress.Compressors.Deflate.GZipStream(target, SharpCompress.Compressors.CompressionMode.Compress) { FileName = "disk.img" })
        {
            zip.Write(new byte[4096]);
            zip.Write("data"u8);
        }

        new TarEngine().ListAll(gz).Where(e => !e.IsDirectory).Select(e => e.FullPath).Should().Equal("disk.img");
        using var content = new TarEngine().ExtractEntry(gz, "disk.img");
        using var copy = new MemoryStream();
        content.CopyTo(copy);
        copy.Length.Should().Be(4100);
    }

    [Fact]
    public void ListAll_EmptyTarGz_HasNoEntries()
    {
        // As GNU tar writes it: the end marker, padded to its blocking factor.
        var tar = Path.Combine(_dir, "empty.tar");
        File.WriteAllBytes(tar, new byte[10240]);

        Names(Gzip(tar)).Should().BeEmpty();
    }

    [Fact]
    public void ListAll_XzFile_LeavesTheFileClosed()
    {
        var xz = Path.Combine(_dir, "broken.tar.xz");
        File.WriteAllBytes(xz, [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0, 0, 0, 0]);

        try
        {
            new TarEngine().ListAll(xz);
        }
        catch (Exception)
        {
            // What it says of a damaged file is not the point here.
        }

        var open = () => new FileStream(xz, FileMode.Open, FileAccess.Read, FileShare.None).Dispose();
        open.Should().NotThrow();
    }

    [Fact]
    public void ExtractEntry_TarGz_ACaseThatDiffers_IsStillFound()
    {
        using var content = new TarEngine().ExtractEntry(Gzip(PackWithDotNet()), "SUB/B.TXT");

        new StreamReader(content).ReadToEnd().Should().Be("BBB");
    }

    [Fact]
    public async Task Panel_PaxTarGz_ShowsItsFoldersAndNoPaxFolder()
    {
        using var fs = new ArchiveFileSystem(Gzip(PackWithDotNet()), new TarEngine());

        var root = await fs.ListAsync(new VfsPath("/"));

        root.Select(e => e.Name).Should().BeEquivalentTo("a.txt", "sub");
    }
}
