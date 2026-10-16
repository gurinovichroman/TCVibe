using System.IO;
using System.Text;
using FluentAssertions;
using TCV.App.Columns;

namespace TCV.App.Tests;

public class FileMetricsServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-metrics-" + Guid.NewGuid().ToString("N"));

    public FileMetricsServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string WriteBytes(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    // ── Line counting ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("a\nb\nc\n", 3)]
    [InlineData("a\nb\nc", 3)]      // no trailing newline — the last line still counts
    [InlineData("a\r\nb\r\n", 2)]
    [InlineData("single line", 1)]
    [InlineData("\n", 1)]           // one empty line
    public void CountLines_MatchesWhatAnEditorShows(string content, int expected)
    {
        var path = Write("f.txt", content);
        FileMetricsService.CountLines(path, new FileInfo(path).Length).Should().Be(expected);
    }

    [Fact]
    public void CountLines_EmptyFile_IsZero()
    {
        var path = Write("empty.txt", string.Empty);
        FileMetricsService.CountLines(path, 0).Should().Be(0);
    }

    [Fact]
    public void CountLines_BinaryFile_ReturnsNull()
    {
        var path = WriteBytes("blob.bin", [0x00, 0x01, 0x02, 0x00, 0xFF]);
        FileMetricsService.CountLines(path, 5).Should().BeNull();
    }

    [Fact]
    public void CountLines_OversizedFile_IsNotOpened()
    {
        var path = Write("small.txt", "a\nb\n");
        // The size argument is the gate, so a bogus one proves the file was never read.
        FileMetricsService.CountLines(path, FileMetricsService.MaxLineCountBytes + 1).Should().BeNull();
    }

    [Fact]
    public void CountLines_MissingFile_ReturnsNullWithoutThrowing()
    {
        var act = () => FileMetricsService.CountLines(Path.Combine(_dir, "nope.txt"), 10);
        act.Should().NotThrow();
        FileMetricsService.CountLines(Path.Combine(_dir, "nope.txt"), 10).Should().BeNull();
    }

    [Fact]
    public void CountLines_LargeFile_CountsAcrossBufferBoundaries()
    {
        // Bigger than the 64 KB read buffer, so a per-buffer bug would show up here.
        var content = string.Join("\n", Enumerable.Range(0, 5000).Select(i => $"line {i} padding padding padding"));
        var path = Write("big.txt", content);

        FileMetricsService.CountLines(path, new FileInfo(path).Length).Should().Be(5000);
    }

    // ── Size on disk ──────────────────────────────────────────────────────────

    [Fact]
    public void GetSizeOnDisk_OrdinaryFile_IsRoundedUpToWholeClusters()
    {
        // 5000 bytes is not a multiple of any cluster size (512, 4096, ...): the allocation must be
        // larger than the logical size and a whole number of sectors. Reporting the logical size — what
        // the compressed-size API alone returns for a plain file — would make this column a copy of Size.
        var path = Write("f.bin", new string('x', 5000));
        var logical = new FileInfo(path).Length;

        var onDisk = FileMetricsService.GetSizeOnDisk(path, logical);

        onDisk.Should().BeGreaterThan(logical);
        (onDisk % 512).Should().Be(0);
    }

    [Fact]
    public void GetSizeOnDisk_ExactClusterMultiple_IsNotRoundedFurther()
    {
        var path = WriteBytes("exact.bin", new byte[65536]);

        FileMetricsService.GetSizeOnDisk(path, 65536).Should().Be(65536);
    }

    [Fact]
    public void GetSizeOnDisk_DeepPathBeyond260Characters_StillMeasuresTheAllocation()
    {
        var deep = _dir;
        while (deep.Length < 300)
        {
            deep = Path.Combine(deep, new string('d', 40));
        }

        Directory.CreateDirectory(deep);
        var path = Path.Combine(deep, "f.bin");
        File.WriteAllBytes(path, new byte[5000]);

        var onDisk = FileMetricsService.GetSizeOnDisk(path, 5000);

        path.Length.Should().BeGreaterThan(260);
        onDisk.Should().BeGreaterThan(5000, "the extended-path prefix lets the query work; a fallback would return 5000");
    }

    [Theory]
    [InlineData(@"C:\dir\file.txt", @"\\?\C:\dir\file.txt")]
    [InlineData(@"\\server\share\file.txt", @"\\?\UNC\server\share\file.txt")]
    [InlineData(@"\\?\C:\already", @"\\?\C:\already")]
    [InlineData("relative.txt", "relative.txt")]
    public void ToExtendedPath_PrefixesAbsolutePathsOnly(string path, string expected)
    {
        FileMetricsService.ToExtendedPath(path).Should().Be(expected);
    }

    [Fact]
    public void CountLines_Utf16FileEndingInANewline_HasNoPhantomLine()
    {
        var path = WriteBytes("wide.txt", new UnicodeEncoding(false, true).GetPreamble()
            .Concat(new UnicodeEncoding(false, false).GetBytes("one\r\ntwo\r\n")).ToArray());

        FileMetricsService.CountLines(path, new FileInfo(path).Length).Should().Be(2);
    }

    [Fact]
    public void CountLines_Utf16FileWithoutTrailingNewline_CountsTheLastLine()
    {
        var path = WriteBytes("wide2.txt", new UnicodeEncoding(false, true).GetPreamble()
            .Concat(new UnicodeEncoding(false, false).GetBytes("one\r\ntwo")).ToArray());

        FileMetricsService.CountLines(path, new FileInfo(path).Length).Should().Be(2);
    }

    // ── Budget ────────────────────────────────────────────────────────────────

    [Fact]
    public void Measure_OverThePassBudget_SkipsTheLineCount_ButStillReportsTheSizeAndIsNotCached()
    {
        var path = Write("f.txt", new string('x', 5000) + "\nsecond\n");
        var info = new FileInfo(path);
        var service = new FileMetricsService();
        var empty = new FileMetricsService.ReadBudget(10);

        var starved = service.Measure(path, info.Length, info.LastWriteTime, false, empty);
        var later = service.Measure(path, info.Length, info.LastWriteTime, false, new FileMetricsService.ReadBudget(1_000_000));

        starved.Lines.Should().BeNull();
        starved.Deferred.Should().BeTrue();
        starved.SizeOnDisk.Should().NotBeNull();
        later.Lines.Should().Be(2, "a pass with a fresh budget can still count it: nothing was cached from the starved one");
        later.Deferred.Should().BeFalse();
    }

    [Fact]
    public void ReadBudget_RefusesACharge_ThatDoesNotFit_AndKeepsTheRest()
    {
        var budget = new FileMetricsService.ReadBudget(100);

        budget.TryCharge(60).Should().BeTrue();
        budget.TryCharge(60).Should().BeFalse();
        budget.Remaining.Should().Be(40);
        budget.TryCharge(40).Should().BeTrue();
    }

    [Fact]
    public void IsCached_TrueOnlyAfterAMeasurement()
    {
        var path = Write("f.txt", "a\n");
        var info = new FileInfo(path);
        var service = new FileMetricsService();

        service.IsCached(path, info.Length, info.LastWriteTime).Should().BeFalse();
        service.Measure(path, info.Length, info.LastWriteTime, false);

        service.IsCached(path, info.Length, info.LastWriteTime).Should().BeTrue();
    }

    [Fact]
    public void GetSizeOnDisk_MissingFile_FallsBackToTheLogicalSize()
    {
        FileMetricsService.GetSizeOnDisk(Path.Combine(_dir, "nope.txt"), 1234).Should().Be(1234);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public void Measure_Directory_ReturnsNothing()
    {
        var metrics = new FileMetricsService().Measure(_dir, 0, DateTime.Now, isDirectory: true);

        metrics.Lines.Should().BeNull();
        metrics.SizeOnDisk.Should().BeNull();
    }

    [Fact]
    public void Measure_SecondCall_ServesTheCachedValueEvenAfterTheFileChanged()
    {
        var path = Write("f.txt", "a\nb\n");
        var info = new FileInfo(path);
        var service = new FileMetricsService();

        var first = service.Measure(path, info.Length, info.LastWriteTime, false);
        File.WriteAllText(path, "a\nb\nc\nd\n");
        var second = service.Measure(path, info.Length, info.LastWriteTime, false);

        // Same key ⇒ same answer: the cache is keyed on size and timestamp, and the caller passes
        // the ones from the listing it is rendering.
        second.Lines.Should().Be(first.Lines);
    }

    [Fact]
    public void Measure_ChangedTimestamp_IsANewMeasurement()
    {
        var path = Write("f.txt", "a\nb\n");
        var service = new FileMetricsService();
        var stamp = new DateTime(2026, 1, 1);

        service.Measure(path, 4, stamp, false).Lines.Should().Be(2);
        File.WriteAllText(path, "a\nb\nc\n");
        service.Measure(path, 6, stamp.AddSeconds(1), false).Lines.Should().Be(3);
    }
}
