using System.Globalization;
using FluentAssertions;
using TCV.App.Localization;

namespace TCV.App.Tests;

public class GestureTokensTests
{
    private static string? Gestures(string id) => id switch
    {
        "CloseTab" => "Ctrl+W",
        "Delete" => "F8",
        _ => null,
    };

    private static string? Titles(string id) => id == "Unbound" ? "Unbound command" : null;

    private static string Expand(string text) => GestureTokens.Expand(text, Gestures, Titles);

    [Fact]
    public void Expand_BareToken_IsTheLiveGesture()
    {
        Expand("Delete them with {gesture:Delete}.").Should().Be("Delete them with F8.");
    }

    [Fact]
    public void Expand_HintToken_KeepsItsParentheses()
    {
        Expand("Close tab ({gesture:CloseTab})").Should().Be("Close tab (Ctrl+W)");
    }

    [Fact]
    public void Expand_HintWithoutGesture_DisappearsWithItsParentheses()
    {
        Expand("Pin it (palette ({gesture:Unbound})) now").Should().Be("Pin it (palette) now");
    }

    [Fact]
    public void Expand_BareTokenWithoutGesture_FallsBackToTheTitle()
    {
        Expand("Use {gesture:Unbound} here").Should().Be("Use «Unbound command» here");
    }

    [Fact]
    public void Expand_UnknownCommand_FallsBackToItsId()
    {
        Expand("Use {gesture:Nowhere}").Should().Be("Use «Nowhere»");
    }

    [Fact]
    public void Expand_TextWithoutTokens_IsReturnedAsIs()
    {
        const string text = "{0} of {1}";
        Expand(text).Should().BeSameAs(text);
    }

    [Fact]
    public void Expand_LeavesFormatItemsForStringFormat()
    {
        var expanded = Expand("Folder {0} not found ({gesture:CloseTab})");

        string.Format(CultureInfo.InvariantCulture, expanded, "X").Should().Be("Folder X not found (Ctrl+W)");
    }

    [Fact]
    public void CommandIds_ListsEveryToken()
    {
        GestureTokens.CommandIds("{gesture:Copy} / {gesture:Move} ({gesture:Delete})")
            .Should().Equal("Copy", "Move", "Delete");
    }
}

public class PluralFormatTests
{
    [Theory]
    [InlineData(0, "0 files")]
    [InlineData(1, "1 file")]
    [InlineData(2, "2 files")]
    [InlineData(21, "21 files")]
    public void Format_English_PicksOneOrOther(int count, string expected)
    {
        PluralFormat.Format("en", "{0} {0:plural:file|files}", count).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файла")]
    [InlineData(4, "4 файла")]
    [InlineData(5, "5 файлов")]
    [InlineData(11, "11 файлов")]
    [InlineData(12, "12 файлов")]
    [InlineData(14, "14 файлов")]
    [InlineData(21, "21 файл")]
    [InlineData(22, "22 файла")]
    [InlineData(25, "25 файлов")]
    [InlineData(111, "111 файлов")]
    [InlineData(101, "101 файл")]
    [InlineData(0, "0 файлов")]
    public void Format_Russian_PicksOneFewOrMany(int count, string expected)
    {
        PluralFormat.Format("ru", "{0} {0:plural:файл|файла|файлов}", count).Should().Be(expected);
    }

    [Fact]
    public void Format_LongAndOtherArguments_AreFormattedAsUsual()
    {
        PluralFormat.Format("en", "{0:plural:item|items} in {1}: {2}", 3L, "dir", 5).Should().Be("items in dir: 5");
    }

    [Fact]
    public void Format_FewerFormsThanTheRuleWants_UsesTheLastOne()
    {
        PluralFormat.Format("ru", "{0:plural:файл|файлы}", 5).Should().Be("файлы");
    }

    [Fact]
    public void Format_UnknownLanguage_UsesTheEnglishRule()
    {
        PluralFormat.Format("de", "{0:plural:Datei|Dateien}", 1).Should().Be("Datei");
        PluralFormat.Format("de", "{0:plural:Datei|Dateien}", 3).Should().Be("Dateien");
    }
}
