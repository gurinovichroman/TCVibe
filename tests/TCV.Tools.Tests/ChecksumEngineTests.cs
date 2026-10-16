using System.Text;
using FluentAssertions;
using TCV.Tools;

namespace TCV.Tools.Tests;

public class ChecksumEngineTests : IDisposable
{
    private readonly ChecksumEngine _engine = new();
    private readonly string _dir;

    public ChecksumEngineTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tcv-checksum-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private string WriteData(string name, string content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    // ── Known test vectors ────────────────────────────────────────────────────

    [Fact]
    public async Task Compute_Sha512_MatchesKnownVector()
    {
        var path = WriteData("abc.txt", "abc");
        var hash = await _engine.ComputeAsync(path, ChecksumAlgorithm.SHA512);
        hash.Should().Be(
            "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a"
          + "2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f");
    }

    [Fact]
    public async Task Compute_Crc32_MatchesCanonicalCheckValue()
    {
        // The canonical CRC-32 check value for "123456789" is 0xCBF43926.
        var path = WriteData("check.txt", "123456789");
        var hash = await _engine.ComputeAsync(path, ChecksumAlgorithm.CRC32);
        hash.Should().Be("cbf43926");
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.MD5, 32)]
    [InlineData(ChecksumAlgorithm.SHA1, 40)]
    [InlineData(ChecksumAlgorithm.SHA256, 64)]
    [InlineData(ChecksumAlgorithm.SHA512, 128)]
    [InlineData(ChecksumAlgorithm.CRC32, 8)]
    public async Task Compute_ProducesHexDigestOfExpectedLength(ChecksumAlgorithm algo, int hexLen)
    {
        var path = WriteData("data.bin", "some payload");
        var hash = await _engine.ComputeAsync(path, algo);
        hash.Should().HaveLength(hexLen);
        hash.Should().MatchRegex("^[0-9a-f]+$");
    }

    // ── Write + verify round-trips ────────────────────────────────────────────

    [Fact]
    public async Task WriteChecksumFile_ThenVerify_AllOk_ForSha1()
    {
        var path = WriteData("payload.dat", "the quick brown fox");
        var results = await CollectAsync(new[] { path }, ChecksumAlgorithm.SHA1);
        var checksumFile = Path.Combine(_dir, "sums.sha1");

        await _engine.WriteChecksumFileAsync(checksumFile, results);
        var verify = await _engine.VerifyChecksumFileAsync(checksumFile);

        verify.Should().HaveCount(1);
        verify[0].Status.Should().Be("OK");
        verify[0].IsOk.Should().BeTrue();
    }

    [Fact]
    public async Task WriteSfv_WithCrc32_ThenVerify_AllOk()
    {
        var path = WriteData("movie.bin", "some binary-ish content 12345");
        var results = await CollectAsync(new[] { path }, ChecksumAlgorithm.CRC32);
        var sfv = Path.Combine(_dir, "sums.sfv");

        await _engine.WriteSfvFileAsync(sfv, results);
        var verify = await _engine.VerifyChecksumFileAsync(sfv);

        verify.Should().ContainSingle().Which.Status.Should().Be("OK");
    }

    [Fact]
    public async Task Verify_DetectsFailed_WhenFileChangedAfterHashing()
    {
        var path = WriteData("mutable.txt", "original");
        var results = await CollectAsync(new[] { path }, ChecksumAlgorithm.SHA256);
        var checksumFile = Path.Combine(_dir, "sums.sha256");
        await _engine.WriteChecksumFileAsync(checksumFile, results);

        File.WriteAllText(path, "tampered");   // change the bytes the digest was taken over

        var verify = await _engine.VerifyChecksumFileAsync(checksumFile);
        verify.Should().ContainSingle().Which.Status.Should().Be("FAILED");
    }

    [Fact]
    public async Task Verify_ReportsMissing_WhenReferencedFileAbsent()
    {
        var checksumFile = Path.Combine(_dir, "orphan.md5");
        await File.WriteAllTextAsync(checksumFile, "MD5 (ghost.txt) = 0123456789abcdef0123456789abcdef\n");

        var verify = await _engine.VerifyChecksumFileAsync(checksumFile);
        verify.Should().ContainSingle().Which.Status.Should().Be("Missing");
    }

    // ── Format detection (black-box, through verification) ─────────────────────

    [Fact]
    public async Task Verify_GnuCoreutilsLayout_HashFirst_IsUnderstood()
    {
        var path = WriteData("gnu.txt", "gnu style line");
        var sha1 = await _engine.ComputeAsync(path, ChecksumAlgorithm.SHA1);
        // GNU coreutils writes "<hash>  <name>" (two spaces, optional '*' binary marker).
        var checksumFile = Path.Combine(_dir, "gnu.sha1");
        await File.WriteAllTextAsync(checksumFile, $"{sha1} *gnu.txt\n");

        var verify = await _engine.VerifyChecksumFileAsync(checksumFile);
        verify.Should().ContainSingle().Which.Status.Should().Be("OK");
    }

    [Fact]
    public async Task Verify_GnuLayout_WhenFilenameIsBareHex_IsNotMisreadAsSfv()
    {
        // Filename "cafebab1" is itself a valid 8-char (CRC32-length) hex string. A GNU line
        // "<sha256>  cafebab1" must be read hash-first (SHA256 of the file), not as an SFV line
        // whose last token "cafebab1" looks like a CRC32 digest.
        var path = WriteData("cafebab1", "ambiguous filename content");
        var sha256 = await _engine.ComputeAsync(path, ChecksumAlgorithm.SHA256);
        var checksumFile = Path.Combine(_dir, "ambiguous.sha256");
        await File.WriteAllTextAsync(checksumFile, $"{sha256}  cafebab1\n");

        var verify = await _engine.VerifyChecksumFileAsync(checksumFile);
        verify.Should().ContainSingle();
        verify[0].FileName.Should().Be("cafebab1");
        verify[0].Status.Should().Be("OK");
    }

    [Fact]
    public async Task Verify_FilenameWithSpaces_InSfvLayout_IsUnderstood()
    {
        var path = WriteData("my movie.bin", "content with spaced name");
        var crc = await _engine.ComputeAsync(path, ChecksumAlgorithm.CRC32);
        var sfv = Path.Combine(_dir, "spaced.sfv");
        await File.WriteAllTextAsync(sfv, $"my movie.bin {crc}\n");

        var verify = await _engine.VerifyChecksumFileAsync(sfv);
        verify.Should().ContainSingle().Which.Status.Should().Be("OK");
    }

    [Fact]
    public async Task Verify_SkipsCommentAndBlankLines()
    {
        var path = WriteData("data.txt", "payload");
        var md5 = await _engine.ComputeAsync(path, ChecksumAlgorithm.MD5);
        var checksumFile = Path.Combine(_dir, "commented.md5");
        await File.WriteAllTextAsync(checksumFile,
            $"; a comment\n# another comment\n\nMD5 (data.txt) = {md5}\n");

        var verify = await _engine.VerifyChecksumFileAsync(checksumFile);
        verify.Should().ContainSingle().Which.Status.Should().Be("OK");
    }

    private async Task<IReadOnlyList<ChecksumResult>> CollectAsync(
        IReadOnlyList<string> paths, ChecksumAlgorithm algo)
    {
        var list = new List<ChecksumResult>();
        await foreach (var r in _engine.ComputeManyAsync(paths, algo))
            list.Add(r);
        return list;
    }
}
