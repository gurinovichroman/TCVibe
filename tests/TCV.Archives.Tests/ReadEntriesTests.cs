using System.IO.Compression;
using FluentAssertions;
using TCV.Archives.Engines;

namespace TCV.Archives.Tests;

/// <summary>
/// The one-pass read behind extracting a folder (6.37): every requested file handed over once, with
/// the path it was asked for, matched the way ExtractEntry matches a single one.
/// </summary>
public sealed class ReadEntriesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-read-" + Guid.NewGuid().ToString("N"));

    public ReadEntriesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Write(string relative, string content)
    {
        var path = Path.Combine(_dir, "src", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private string PackTar()
    {
        var tar = Path.Combine(_dir, "t.tar");
        new TarEngine().Pack(tar,
        [
            (Write("a.txt", "AAA"), "a.txt"),
            (Write("dir/b.txt", "BBB"), "dir/b.txt"),
            (Write("dir/c.txt", "CCC"), "dir/c.txt"),
        ]);
        return tar;
    }

    private static Dictionary<string, string> ReadAll(
        TCV.Core.PluginSdk.IArchiveEngine engine, string archive, params string[] paths)
    {
        var handed = new Dictionary<string, string>();
        engine.ReadEntries(archive, paths, (path, content) =>
        {
            handed.Add(path, new StreamReader(content).ReadToEnd());
        }, CancellationToken.None);
        return handed;
    }

    [Fact]
    public void ReadEntries_Tar_HandsOverEachRequestedFileOnceAndNothingElse()
    {
        var tar = PackTar();

        var handed = ReadAll(new TarEngine(), tar, "dir/c.txt", "a.txt");

        handed.Should().BeEquivalentTo(new Dictionary<string, string> { ["dir/c.txt"] = "CCC", ["a.txt"] = "AAA" });
    }

    [Fact]
    public void ReadEntries_PathNotInTheArchive_IsLeftOut()
    {
        var tar = PackTar();
        var zip = Path.Combine(_dir, "t.zip");
        new ZipEngine().Pack(zip, [(Write("z.txt", "ZZZ"), "z.txt")]);

        var fromTar = ReadAll(new TarEngine(), tar, "a.txt", "gone.txt");
        var fromZip = ReadAll(new ZipEngine(), zip, "gone.txt", "z.txt");

        fromTar.Keys.Should().Equal("a.txt");
        fromZip.Keys.Should().Equal("z.txt");
    }

    [Fact]
    public void ReadEntries_ZipNamesDifferingOnlyByCase_EachRequestGetsItsOwnEntry()
    {
        var zip = Path.Combine(_dir, "case.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("Readme.txt", "upper"), ("readme.txt", "lower") })
            {
                using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                writer.Write(text);
            }
        }

        var handed = ReadAll(new ZipEngine(), zip, "readme.txt", "Readme.txt", "README.TXT");

        // The third spelling matches nothing exactly, and both entries already answered their own name.
        handed.Should().BeEquivalentTo(new Dictionary<string, string> { ["Readme.txt"] = "upper", ["readme.txt"] = "lower" });
    }

    [Fact]
    public void ReadEntries_OtherSpellingOfAName_IsAnsweredLikeExtractEntry()
    {
        var tar = PackTar();

        var handed = ReadAll(new TarEngine(), tar, "/DIR/B.TXT");

        handed.Should().BeEquivalentTo(new Dictionary<string, string> { ["/DIR/B.TXT"] = "BBB" });
    }

    [Fact]
    public void ReadEntries_ConsumerThrows_TheSameExceptionComesBack()
    {
        var tar = PackTar();

        // The message would read as a password failure if the engine classified it as its own.
        var act = () => new TarEngine().ReadEntries(tar, ["a.txt"],
            (_, _) => throw new InvalidOperationException("password of the disk"), CancellationToken.None);

        act.Should().Throw<InvalidOperationException>().WithMessage("password of the disk");
    }

    [Fact]
    public void ReadEntries_Iso_DefaultReadsEveryRequestedEntry()
    {
        var iso = Fixtures.CreateIso(_dir, "readme", "guide");

        var handed = ReadAll(new IsoEngine(), iso, "README.TXT", "DOCS/GUIDE.TXT");

        handed.Should().BeEquivalentTo(new Dictionary<string, string> { ["README.TXT"] = "readme", ["DOCS/GUIDE.TXT"] = "guide" });
    }

    [Fact]
    public void ExtractEntries_TarEntryAskedForTwice_WritesBothDestinations()
    {
        var tar = PackTar();
        var one = Path.Combine(_dir, "one", "b.txt");
        var two = Path.Combine(_dir, "two", "b.txt");

        new TarEngine().ExtractEntries(tar, [("dir/b.txt", one), ("dir/b.txt", two)], CancellationToken.None);

        File.ReadAllText(one).Should().Be("BBB");
        File.ReadAllText(two).Should().Be("BBB");
    }

    [Fact]
    public void ExtractEntries_TarMissingEntry_WritesTheRestAndThrows()
    {
        var tar = PackTar();
        var dest = Path.Combine(_dir, "out");

        var act = () => new TarEngine().ExtractEntries(tar,
            [("a.txt", Path.Combine(dest, "a.txt")), ("nope.txt", Path.Combine(dest, "nope.txt"))],
            CancellationToken.None);

        act.Should().Throw<FileNotFoundException>();
        File.ReadAllText(Path.Combine(dest, "a.txt")).Should().Be("AAA");
    }

    [Fact]
    public void ReadEntries_SolidSevenZip_HandsOverTheRequestedFilesInOnePass()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        for (var i = 0; i < 6; i++)
        {
            Write($"dir/f{i}.txt", new string((char)('a' + i), 5000));
        }

        var archive = Path.Combine(_dir, "solid.7z");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", "-ms=on", archive, "dir");

        var handed = ReadAll(new SevenZipEngine(), archive, "dir/f4.txt", "dir/f1.txt");

        handed.Keys.Should().BeEquivalentTo("dir/f4.txt", "dir/f1.txt");
        handed["dir/f1.txt"].Should().Be(new string('b', 5000));
        handed["dir/f4.txt"].Should().Be(new string('e', 5000));
    }

    [Fact]
    public void ReadEntries_WrongPasswordWhileReading_IsReportedAsAPasswordProblem()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("secret.txt", string.Concat(Enumerable.Repeat("classified ", 20_000)));
        var archive = Path.Combine(_dir, "data-only.7z");
        // Names readable, data encrypted: nothing fails before the first byte is decoded.
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", "-pright", "-mhe=off", archive, "secret.txt");

        var act = () => new SevenZipEngine("wrong").ReadEntries(archive, ["secret.txt"],
            (_, content) => content.CopyTo(Stream.Null), CancellationToken.None);

        act.Should().Throw<ArchivePasswordRequiredException>();
    }

    // Two archives the test above made, password "right", that the key derived from "wrong" decrypts into
    // bytes breaking the LZMA2 decoder in an unusual way. A fresh archive gets a fresh salt and IV, so the
    // test above met them only now and then (about one run in seventy, 6.78); kept here, they fail every time.
    private const string WrongKeyGivesBadLzmaProperties =
        "N3q8ryccAAQQvoSJgAAAAAAAAAByAAAAAAAAAKCKpXQOBSfaZtrKWKUX39QNFe8/UwbdTD9PpJ64EJtc34YsGwRj6Tx1shfOZ2fimIw94BWfVDp4" +
        "NhX8DbAy+C7SKr5z2C8BGf877y2iCeGrJpxk/+DVcpQN7l8WdT2ut8GL0cjs8gIYbHJM/fuutNve+KLMGWj6osFy2DEXBDssHvE+UQEEBgABCYCA" +
        "AAcLAQACJAbxBwESUw+1D2pi0gw6Slx1LqHbL6c+ISEBDAEADHjDYFsACAoBspWQLgAABQERFwBzAGUAYwByAGUAdAAuAHQAeAB0AAAAGQQAAAAA" +
        "FAoBAEl7zMxsXN0BFQYBACAAAAAAAA==";

    private const string WrongKeyRunsPastTheEnd =
        "N3q8ryccAARiZQIUgAAAAAAAAAByAAAAAAAAAL+527dYCuI4B+mMg+baCPaE/yialaQq+80luc0GAEhOTCG40RJhWKyq3MPRcX7F+GzWTy14CFjY" +
        "ACqZpakgbw4ZLabN8k/C2CLG3KcvAAx5ORG+GCER1CaMKh2xTERN8G5VTGi1DX4AJhaC/VK1TaAQnXQMvqtYxPHUlo9xfHNiiY0DeQEEBgABCYCA" +
        "AAcLAQACJAbxBwESUw/lTcosQwA6as4JD9VJMun8ISEBDAEADHjDYFsACAoBspWQLgAABQERFwBzAGUAYwByAGUAdAAuAHQAeAB0AAAAGQQAAAAA" +
        "FAoBAEl7zMxsXN0BFQYBACAAAAAAAA==";

    [Theory]
    [InlineData(WrongKeyGivesBadLzmaProperties)]
    [InlineData(WrongKeyRunsPastTheEnd)]
    public void WrongPassword_DecoderFailsUnusually_IsStillReportedAsAPasswordProblem(string base64)
    {
        var archive = Path.Combine(_dir, "odd.7z");
        File.WriteAllBytes(archive, Convert.FromBase64String(base64));

        // The archive itself is sound: the right password reads it.
        ReadAll(new SevenZipEngine("right"), archive, "secret.txt")["secret.txt"]
            .Should().Be(string.Concat(Enumerable.Repeat("classified ", 20_000)));

        var read = () => new SevenZipEngine("wrong").ReadEntries(archive, ["secret.txt"],
            (_, content) => content.CopyTo(Stream.Null), CancellationToken.None);
        var extractOne = () => new SevenZipEngine("wrong").ExtractEntry(archive, "secret.txt").Dispose();
        var extractAll = () => new SevenZipEngine("wrong").ExtractAll(archive, Path.Combine(_dir, "out"));
        var list = () => new SevenZipEngine("wrong").ListAll(archive);

        read.Should().Throw<ArchivePasswordRequiredException>();
        extractOne.Should().Throw<ArchivePasswordRequiredException>();
        extractAll.Should().Throw<ArchivePasswordRequiredException>();
        list.Should().Throw<ArchivePasswordRequiredException>();
    }

    [Fact]
    public void ReadEntries_ConsumerCancels_StopsAtThatEntry()
    {
        if (Fixtures.SevenZip is null)
        {
            return;
        }

        Write("dir/big.txt", new string('x', 4_000_000));
        Write("dir/next.txt", "next");
        var archive = Path.Combine(_dir, "cancel.7z");
        Fixtures.RunSevenZip(Path.Combine(_dir, "src"), "a", "-ms=on", archive, "dir");
        using var cts = new CancellationTokenSource();
        var handed = new List<string>();

        var act = () => new SevenZipEngine().ReadEntries(archive, ["dir/big.txt", "dir/next.txt"], (path, content) =>
        {
            handed.Add(path);
            content.ReadByte();
            cts.Cancel();
            cts.Token.ThrowIfCancellationRequested();
        }, cts.Token);

        act.Should().Throw<OperationCanceledException>();
        handed.Should().ContainSingle();
    }
}
