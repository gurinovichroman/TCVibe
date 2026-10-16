using System.IO.Compression;
using FluentAssertions;
using TCV.Archives.Engines;

namespace TCV.Archives.Tests;

public sealed class TarZipSevenZipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-arc-" + Guid.NewGuid().ToString("N"));

    public TarZipSevenZipTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, "src", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ── TAR ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Pack_TarRoundTrip_ListsAndExtractsEveryFile()
    {
        var a = Write("a.txt", "AAA");
        var b = Write("sub/b.txt", "BBB");
        var tar = Path.Combine(_dir, "out.tar");
        var engine = new TarEngine();

        engine.Pack(tar, [(a, "a.txt"), (b, "sub/b.txt")]);
        var entries = engine.ListAll(tar);
        engine.ExtractAll(tar, Path.Combine(_dir, "x"));

        entries.Select(e => e.FullPath).Should().BeEquivalentTo("a.txt", "sub", "sub/b.txt");
        File.ReadAllText(Path.Combine(_dir, "x", "sub", "b.txt")).Should().Be("BBB");
    }

    [Fact]
    public void TarEngine_CanWrite_IsFalseBecauseItCannotBeModifiedInPlace()
    {
        new TarEngine().CanWrite.Should().BeFalse();
    }

    [Fact]
    public void ExtractAll_TarEntryEscapingDestination_ThrowsAndWritesNothingOutside()
    {
        var evil = Write("evil.txt", "boom");
        var tar = Path.Combine(_dir, "evil.tar");
        new TarEngine().Pack(tar, [(evil, "../escaped.txt")]);
        var dest = Path.Combine(_dir, "dest");

        var act = () => new TarEngine().ExtractAll(tar, dest);

        act.Should().Throw<InvalidDataException>();
        File.Exists(Path.Combine(_dir, "escaped.txt")).Should().BeFalse();
    }

    [Fact]
    public void ExtractEntries_TarFolder_ExtractsOnlyTheRequestedEntries()
    {
        var a = Write("a.txt", "AAA");
        var b = Write("keep/b.txt", "BBB");
        var c = Write("keep/c.txt", "CCC");
        var tar = Path.Combine(_dir, "many.tar");
        var engine = new TarEngine();
        engine.Pack(tar, [(a, "a.txt"), (b, "keep/b.txt"), (c, "keep/c.txt")]);
        var dest = Path.Combine(_dir, "picked");

        engine.ExtractEntries(tar,
            [("keep/b.txt", Path.Combine(dest, "b.txt")), ("keep/c.txt", Path.Combine(dest, "c.txt"))],
            CancellationToken.None);

        Directory.GetFiles(dest).Select(Path.GetFileName).Should().BeEquivalentTo("b.txt", "c.txt");
    }

    [Fact]
    public void Pack_TarCancelled_LeavesNoPartialArchive()
    {
        var a = Write("a.txt", "AAA");
        var tar = Path.Combine(_dir, "cancelled.tar");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new TarEngine().Pack(tar, [(a, "a.txt")], CompressionLevel.Optimal, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        File.Exists(tar).Should().BeFalse();
    }

    // ── ZIP ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Pack_ZipWithEmptyDirectoryEntry_RestoresTheDirectoryOnExtract()
    {
        var a = Write("a.txt", "AAA");
        var zip = Path.Combine(_dir, "dirs.zip");
        var engine = new ZipEngine();

        engine.Pack(zip, [(a, "a.txt"), (string.Empty, "empty/")]);
        engine.ExtractAll(zip, Path.Combine(_dir, "x"));

        Directory.Exists(Path.Combine(_dir, "x", "empty")).Should().BeTrue();
        engine.ListAll(zip).Should().Contain(e => e.FullPath == "empty" && e.IsDirectory);
    }

    [Fact]
    public void Pack_ZipWithStoreLevel_IsLargerThanTheDeflatedOne()
    {
        var big = Write("big.txt", new string('x', 200_000));
        var stored = Path.Combine(_dir, "stored.zip");
        var deflated = Path.Combine(_dir, "deflated.zip");
        var engine = new ZipEngine();

        engine.Pack(stored, [(big, "big.txt")], CompressionLevel.NoCompression, CancellationToken.None);
        engine.Pack(deflated, [(big, "big.txt")], CompressionLevel.Optimal, CancellationToken.None);

        new FileInfo(stored).Length.Should().BeGreaterThan(new FileInfo(deflated).Length);
    }

    [Fact]
    public void Pack_ZipCancelled_LeavesNoPartialArchive()
    {
        var a = Write("a.txt", "AAA");
        var zip = Path.Combine(_dir, "cancelled.zip");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new ZipEngine().Pack(zip, [(a, "a.txt")], CompressionLevel.Optimal, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        File.Exists(zip).Should().BeFalse();
    }

    [Fact]
    public void AddFiles_ThenDeleteEntry_ChangesTheArchiveInPlace()
    {
        var a = Write("a.txt", "AAA");
        var b = Write("b.txt", "BBB");
        var zip = Path.Combine(_dir, "edit.zip");
        var engine = new ZipEngine();
        engine.Pack(zip, [(a, "a.txt")]);

        engine.AddFiles(zip, [(b, "folder/b.txt")]);
        engine.DeleteEntry(zip, "a.txt");

        engine.ListAll(zip).Select(e => e.FullPath).Should().BeEquivalentTo("folder", "folder/b.txt");
    }

    [Fact]
    public void DeleteEntry_DirectoryPrefix_RemovesEverythingBelowIt()
    {
        var a = Write("a.txt", "AAA");
        var zip = Path.Combine(_dir, "dir.zip");
        var engine = new ZipEngine();
        engine.Pack(zip, [(a, "keep.txt"), (a, "gone/one.txt"), (a, "gone/deep/two.txt")]);

        engine.DeleteEntry(zip, "gone");

        engine.ListAll(zip).Select(e => e.FullPath).Should().BeEquivalentTo("keep.txt");
    }

    [Fact]
    public void DeleteEntry_NamesDifferingOnlyByCase_RemovesOnlyTheExactOne()
    {
        var zip = Path.Combine(_dir, "case.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Readme.txt");
            archive.CreateEntry("readme.txt");
        }

        new ZipEngine().DeleteEntry(zip, "readme.txt");

        new ZipEngine().ListAll(zip).Select(e => e.FullPath).Should().BeEquivalentTo("Readme.txt");
    }

    [Fact]
    public void ExtractEntry_ZipEntryStoredWithBackslashes_IsFoundByItsSlashPath()
    {
        var zip = Path.Combine(_dir, "backslash.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("dir\\file.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("payload");
        }

        using var stream = new ZipEngine().ExtractEntry(zip, "dir/file.txt");
        using var reader = new StreamReader(stream);

        reader.ReadToEnd().Should().Be("payload");
    }

    [Fact]
    public void ExtractAll_ZipCancelled_Throws()
    {
        var a = Write("a.txt", "AAA");
        var zip = Path.Combine(_dir, "c.zip");
        new ZipEngine().Pack(zip, [(a, "a.txt")]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => new ZipEngine().ExtractAll(zip, Path.Combine(_dir, "x"), null, cts.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    // ── 7z and encrypted archives (need 7-Zip; return early when it is not installed) ────────

    [Fact]
    public void SevenZip_ListExtractAndFolder_WorkThroughOnePass()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("a.txt", "AAA");
        Write("sub/b.txt", "BBB");
        var archive = Path.Combine(_dir, "t.7z");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", archive, "a.txt", "sub");
        var engine = new SevenZipEngine();

        engine.ListAll(archive).Select(e => e.FullPath).Should().BeEquivalentTo("a.txt", "sub", "sub/b.txt");
        engine.ExtractAll(archive, Path.Combine(_dir, "x"));
        engine.ExtractEntries(archive, [("sub/b.txt", Path.Combine(_dir, "one", "b.txt"))], CancellationToken.None);

        File.ReadAllText(Path.Combine(_dir, "x", "sub", "b.txt")).Should().Be("BBB");
        File.ReadAllText(Path.Combine(_dir, "one", "b.txt")).Should().Be("BBB");
    }

    [Fact]
    public void SevenZip_EncryptedArchive_AsksForThePasswordAndAcceptsTheRightOne()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("secret.txt", "classified");
        var archive = Path.Combine(_dir, "enc.7z");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", archive, "-psecret", "-mhe=on", "secret.txt");

        var noPassword = () => new SevenZipEngine().ListAll(archive);
        var wrongPassword = () => new SevenZipEngine("nope").ListAll(archive);
        var listed = new SevenZipEngine("secret").ListAll(archive);

        noPassword.Should().Throw<ArchivePasswordRequiredException>();
        wrongPassword.Should().Throw<ArchivePasswordRequiredException>();
        listed.Should().ContainSingle(e => e.FullPath == "secret.txt");
        using var stream = new SevenZipEngine("secret").ExtractEntry(archive, "secret.txt");
        new StreamReader(stream).ReadToEnd().Should().Be("classified");
    }

    [Theory]
    [InlineData("-mem=AES256")]
    [InlineData("-mem=ZipCrypto")]
    public void EncryptedZip_WithoutPassword_ThrowsAndWithItExtracts(string method)
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("secret.txt", "classified");
        var archive = Path.Combine(_dir, "enc.zip");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", "-tzip", method, "-ppw123", archive, "secret.txt");

        var noPassword = () => new ZipEngine().ListAll(archive);
        var wrongPassword = () => new ZipEngine("bad").ListAll(archive);
        new ZipEngine("pw123").ListAll(archive);
        new ZipEngine("pw123").ExtractAll(archive, Path.Combine(_dir, "x"));

        noPassword.Should().Throw<ArchivePasswordRequiredException>();
        wrongPassword.Should().Throw<ArchivePasswordRequiredException>();
        File.ReadAllText(Path.Combine(_dir, "x", "secret.txt")).Should().Be("classified");
    }

    [Fact]
    public void ArchivesPlugin_RemembersAPasswordForLaterEnginesOnTheSameArchive()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("secret.txt", "classified");
        var archive = Path.Combine(_dir, "remember.zip");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", "-tzip", "-mem=AES256", "-ppw123", archive, "secret.txt");
        var plugin = new ArchivesPlugin();

        plugin.Create(archive, new Dictionary<string, string> { ["password"] = "pw123" });
        var later = plugin.CreateEngine(archive);

        later.ExtractEntry(archive, "secret.txt").Dispose();
    }

    [Fact]
    public void Pack_TarCancelledMidwayThroughTheInterface_StopsAndLeavesNoArchive()
    {
        var a = Write("a.txt", "AAA");
        var b = Write("b.txt", "BBB");
        var tar = Path.Combine(_dir, "midway.tar");
        using var cts = new CancellationTokenSource();

        IEnumerable<(string, string)> Files()
        {
            yield return (a, "a.txt");
            cts.Cancel();
            yield return (b, "b.txt");
        }

        TCV.Core.PluginSdk.IArchiveEngine engine = new TarEngine();
        var act = () => engine.Pack(tar, Files(), CompressionLevel.Optimal, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        File.Exists(tar).Should().BeFalse();
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    public void ListAll_WrongPasswordAndOnlyALargeEncryptedFile_StillRejectsThePassword(string format)
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        // Bigger than the old 4 MB probe limit, and incompressible so the archive stays that large.
        var big = Path.Combine(_dir, "src", "big.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(big)!);
        var bytes = new byte[5 * 1024 * 1024];
        new Random(1).NextBytes(bytes);
        File.WriteAllBytes(big, bytes);
        var archive = Path.Combine(_dir, "big." + format);
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", format == "zip" ? "-tzip" : "-t7z",
            format == "zip" ? "-mem=AES256" : "-mhe=off", "-ppw123", archive, "big.bin");
        var engine = new ArchiveEngineFactory();

        var wrong = () => engine.Create(format, "nope").ListAll(archive);

        wrong.Should().Throw<ArchivePasswordRequiredException>();
        engine.Create(format, "pw123").ListAll(archive).Should().ContainSingle(e => e.FullPath == "big.bin");
    }

    [Fact]
    public void ArchivesPlugin_ForgetPassword_LaterEnginesNoLongerUseIt()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("secret.txt", "classified");
        var archive = Path.Combine(_dir, "forget.zip");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", "-tzip", "-mem=AES256", "-ppw123", archive, "secret.txt");
        var plugin = new ArchivesPlugin();
        plugin.RememberPassword(archive, "pw123");

        plugin.ForgetPassword(archive);
        var list = () => plugin.CreateEngine(archive).ListAll(archive);

        list.Should().Throw<ArchivePasswordRequiredException>();
    }
}
