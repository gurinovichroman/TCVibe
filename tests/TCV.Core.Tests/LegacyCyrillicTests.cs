using System.Text;
using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

/// <summary>Old Russian text files are recognised instead of being shown as Latin-1 mojibake.</summary>
public class LegacyCyrillicTests
{
    private const string Sample =
        "Просмотр файла в кодировке по умолчанию должен показывать русский текст без ручного выбора кодировки. " +
        "Это обычное предложение из нескольких слов, как в документации или в комментарии к коду.";

    static LegacyCyrillicTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [Theory]
    [InlineData(1251)]
    [InlineData(866)]
    [InlineData(20866)]
    public void DetectEncoding_LegacyRussianText_IsRecognisedAndDecodesBack(int codePage)
    {
        var encoding = Encoding.GetEncoding(codePage);
        var bytes = encoding.GetBytes(Sample);

        var (detected, bom) = TextProbe.DetectEncoding(bytes);

        detected.CodePage.Should().Be(codePage);
        bom.Should().Be(0);
        TextProbe.DecodeText(bytes, detected).Should().Be(Sample);
    }

    [Fact]
    public void DetectEncoding_ShortRussianWord_IsStillRecognised()
    {
        var bytes = Encoding.GetEncoding(1251).GetBytes("привет, мир\r\n");

        TextProbe.DetectEncoding(bytes).Encoding.CodePage.Should().Be(1251);
    }

    [Fact]
    public void DetectEncoding_Utf8Russian_StaysUtf8()
    {
        var bytes = new UTF8Encoding(false).GetBytes(Sample);

        TextProbe.DetectEncoding(bytes).Encoding.Should().BeOfType<UTF8Encoding>();
    }

    [Fact]
    public void DetectEncoding_FewStrayHighBytes_StaysLatin1()
    {
        var bytes = new byte[] { (byte)'a', 0xFF, (byte)'b' };

        TextProbe.DetectEncoding(bytes).Encoding.Should().BeSameAs(Encoding.Latin1);
    }

    [Fact]
    public void DetectEncoding_WesternLatin1Text_IsNotTakenForRussian()
    {
        // "Café Zürich, Ærø, naïve façade, résumé": accents, not Cyrillic
        var bytes = Encoding.Latin1.GetBytes("Café Zürich, Ærø, naïve façade, résumé, jalapeño, über, señor, crème brûlée");

        TextProbe.DetectEncoding(bytes).Encoding.Should().BeSameAs(Encoding.Latin1);
    }

    [Fact]
    public void DetectEncoding_RandomHighBytes_StaysLatin1()
    {
        var random = new Random(7);
        var bytes = Enumerable.Range(0, 400).Select(_ => (byte)random.Next(0x80, 0x100)).ToArray();

        TextProbe.DetectEncoding(bytes).Encoding.Should().BeSameAs(Encoding.Latin1);
    }

    [Fact]
    public void LooksBinary_LegacyRussianText_IsText()
    {
        TextProbe.LooksBinary(Encoding.GetEncoding(1251).GetBytes(Sample)).Should().BeFalse();
    }

    [Fact]
    public void LooksBinary_Utf16WithoutBom_IsStillText()
    {
        TextProbe.LooksBinary(Encoding.Unicode.GetBytes("plain ascii as utf-16 text")).Should().BeFalse();
    }
}
