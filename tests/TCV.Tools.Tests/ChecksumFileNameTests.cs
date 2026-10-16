using FluentAssertions;
using TCV.Tools;

namespace TCV.Tools.Tests;

/// <summary>Which files the Checksums window treats as a list to verify rather than a file to hash (6.55).</summary>
public sealed class ChecksumFileNameTests
{
    [Theory]
    [InlineData("release.sha256")]
    [InlineData("RELEASE.SHA256")]
    [InlineData("files.md5")]
    [InlineData("files.sha1")]
    [InlineData("files.sha512")]
    [InlineData("disc.sfv")]
    [InlineData("SHA256SUMS")]
    [InlineData("sha256sums")]
    [InlineData("MD5SUMS")]
    [InlineData("SHA1SUMS")]
    [InlineData("SHA512SUMS")]
    public void IsChecksumFileName_FormatTheEngineVerifies_IsTrue(string name)
    {
        ChecksumEngine.IsChecksumFileName(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("readme.txt")]
    [InlineData("sha256")]
    [InlineData("notes.sha256.txt")]
    [InlineData("SHA256SUMS.asc")]
    [InlineData("checksums")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void IsChecksumFileName_AnythingElse_IsFalse(string? name)
    {
        ChecksumEngine.IsChecksumFileName(name).Should().BeFalse();
    }

    [Fact]
    public void IsChecksumFileName_FullPath_LooksAtTheFileNameOnly()
    {
        ChecksumEngine.IsChecksumFileName(@"D:\iso\ubuntu.sha256").Should().BeTrue();
        ChecksumEngine.IsChecksumFileName(@"D:\sums.sha256\readme.txt").Should().BeFalse();
    }

    [Fact]
    public void IsChecksumFileName_EveryExtensionTheDialogWrites_IsRecognised()
    {
        foreach (var algorithm in Enum.GetValues<ChecksumAlgorithm>())
        {
            ChecksumEngine.IsChecksumFileName("checksums." + ChecksumEngine.Extension(algorithm))
                .Should().BeTrue("a file the window saves must open back in the window as something to verify ({0})", algorithm);
        }
    }
}
