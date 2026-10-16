using System.Text;
using FluentAssertions;
using TCV.App.Viewer;

namespace TCV.App.Tests;

/// <summary>Covers the pure logic behind the internal file viewer (F3 lister): BOM/encoding
/// detection, binary/image classification and hex-dump formatting.</summary>
public class ViewerContentTests
{
    [Fact]
    public void DetectEncoding_Utf8Bom()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' };
        var (enc, bom) = ViewerContent.DetectEncoding(data);
        enc.Should().BeOfType<UTF8Encoding>();
        bom.Should().Be(3);
    }

    [Fact]
    public void DetectEncoding_Utf16LeBom()
    {
        var data = new byte[] { 0xFF, 0xFE, (byte)'h', 0x00 };
        var (enc, bom) = ViewerContent.DetectEncoding(data);
        enc.Should().BeSameAs(Encoding.Unicode);
        bom.Should().Be(2);
    }

    [Fact]
    public void DetectEncoding_Utf16BeBom()
    {
        var data = new byte[] { 0xFE, 0xFF, 0x00, (byte)'h' };
        var (enc, bom) = ViewerContent.DetectEncoding(data);
        enc.Should().BeSameAs(Encoding.BigEndianUnicode);
        bom.Should().Be(2);
    }

    [Fact]
    public void DetectEncoding_Utf32LeBom_TakesPrecedenceOverUtf16()
    {
        var data = new byte[] { 0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00 };
        var (enc, bom) = ViewerContent.DetectEncoding(data);
        enc.Should().BeOfType<UTF32Encoding>();
        bom.Should().Be(4);
    }

    [Fact]
    public void DetectEncoding_PlainAscii_IsUtf8()
    {
        var data = Encoding.ASCII.GetBytes("hello world\r\n");
        var (enc, bom) = ViewerContent.DetectEncoding(data);
        enc.Should().BeOfType<UTF8Encoding>();
        bom.Should().Be(0);
    }

    [Fact]
    public void DetectEncoding_ValidMultibyteUtf8_NoBom()
    {
        var data = Encoding.UTF8.GetBytes("Привет, мир"); // Encoding.UTF8 here has no BOM in GetBytes
        var (enc, _) = ViewerContent.DetectEncoding(data);
        enc.Should().BeOfType<UTF8Encoding>();
        ViewerContent.DecodeText(data, enc).Should().Be("Привет, мир");
    }

    [Fact]
    public void DetectEncoding_InvalidUtf8_FallsBackToLatin1()
    {
        // 0xFF is never valid on its own in UTF-8 and there is no NUL, so it is 8-bit text.
        var data = new byte[] { (byte)'a', 0xFF, (byte)'b' };
        var (enc, _) = ViewerContent.DetectEncoding(data);
        enc.Should().BeSameAs(Encoding.Latin1);
    }

    [Fact]
    public void DetectEncoding_BomlessUtf16Le_ByNulPattern()
    {
        var data = Encoding.Unicode.GetBytes("abcdefgh"); // no BOM, zero high bytes at odd indexes
        var (enc, bom) = ViewerContent.DetectEncoding(data);
        enc.Should().BeSameAs(Encoding.Unicode);
        bom.Should().Be(0);
    }

    [Fact]
    public void Utf8SafeLength_TrimsSplitMultibyteSequence()
    {
        // "ы" is 0xD0 0xAB in UTF-8; cutting after the lead byte leaves an incomplete sequence.
        var full = Encoding.UTF8.GetBytes("abcы");
        var cut = full[..(full.Length - 1)]; // ...ab c + lone 0xD0

        ViewerContent.Utf8SafeLength(cut).Should().Be(cut.Length - 1); // the stray 0xD0 is dropped

        var trimmed = cut[..ViewerContent.Utf8SafeLength(cut)];
        ViewerContent.DecodeText(trimmed, ViewerContent.Utf8).Should().Be("abc"); // no U+FFFD tail
    }

    [Fact]
    public void Utf8SafeLength_KeepsCompleteSequence()
    {
        var full = Encoding.UTF8.GetBytes("abcы"); // ends on a complete 2-byte code point
        ViewerContent.Utf8SafeLength(full).Should().Be(full.Length);
    }

    [Fact]
    public void DecodeText_StripsUtf8Bom()
    {
        var data = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("text")).ToArray();
        ViewerContent.DecodeText(data, ViewerContent.Utf8).Should().Be("text");
    }

    [Fact]
    public void LooksBinary_TextIsNotBinary()
    {
        var data = Encoding.UTF8.GetBytes("just some readable text\r\nwith lines\ttabs");
        ViewerContent.LooksBinary(data).Should().BeFalse();
    }

    [Fact]
    public void LooksBinary_NulByteIsBinary()
    {
        var data = new byte[] { (byte)'a', 0x00, (byte)'b', (byte)'c' };
        ViewerContent.LooksBinary(data).Should().BeTrue();
    }

    [Fact]
    public void LooksBinary_Utf16TextIsNotBinary()
    {
        var data = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hello")).ToArray();
        ViewerContent.LooksBinary(data).Should().BeFalse();
    }

    [Fact]
    public void LooksBinary_EmptyIsNotBinary()
    {
        ViewerContent.LooksBinary(Array.Empty<byte>()).Should().BeFalse();
    }

    [Fact]
    public void ClassifyDefault_PngIsImage()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3 };
        ViewerContent.ClassifyDefault("logo.png", png).Should().Be(ViewerMode.Image);
    }

    [Fact]
    public void ClassifyDefault_ImageByExtensionOnly()
    {
        ViewerContent.ClassifyDefault("photo.jpg", new byte[] { 1, 2, 3, 4 })
            .Should().Be(ViewerMode.Image);
    }

    [Fact]
    public void ClassifyDefault_TextFile()
    {
        var data = Encoding.UTF8.GetBytes("# a markdown file\n\nsome content");
        ViewerContent.ClassifyDefault("readme.md", data).Should().Be(ViewerMode.Text);
    }

    [Fact]
    public void ClassifyDefault_BinaryFile()
    {
        var data = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 }; // MZ exe header
        ViewerContent.ClassifyDefault("app.dll", data).Should().Be(ViewerMode.Hex);
    }

    [Fact]
    public void ToHexDump_FormatsOffsetHexAndAscii()
    {
        var data = Encoding.ASCII.GetBytes("Hi!");
        var dump = ViewerContent.ToHexDump(data);
        // offset(8) + 2 spaces + 49-char hex field ("48 69 21 " + 40 pad) + 1 gutter space + ascii + \n
        var expected = "00000000  48 69 21 " + new string(' ', 40) + " Hi!\n";
        dump.Should().Be(expected);
    }

    [Fact]
    public void ToHexDump_HonoursBaseOffset()
    {
        var dump = ViewerContent.ToHexDump(new byte[] { 0xAB }, baseOffset: 0x100);
        dump.Should().StartWith("00000100  AB ");
    }

    [Fact]
    public void ToHexDump_NonPrintableBytesBecomeDots()
    {
        var data = new byte[] { 0x00, 0x1F, 0x41, 0x7F };
        var dump = ViewerContent.ToHexDump(data);
        dump.TrimEnd('\n').Should().EndWith("..A.");
    }

    [Fact]
    public void GetCodePage_Windows1251_DecodesCyrillic()
    {
        var enc = ViewerContent.GetCodePage(1251);
        var bytes = enc.GetBytes("Тест");
        ViewerContent.DecodeText(bytes, enc).Should().Be("Тест");
    }
}
