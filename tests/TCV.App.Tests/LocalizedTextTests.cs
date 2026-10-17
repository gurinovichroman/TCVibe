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
        PluralFormat.Format("xx", "{0:plural:Datei|Dateien}", 1).Should().Be("Datei");
        PluralFormat.Format("xx", "{0:plural:Datei|Dateien}", 3).Should().Be("Dateien");
    }

    [Theory]
    [InlineData("de", 1, 0)]
    [InlineData("de", 0, 1)]
    [InlineData("nl", 2, 1)]
    [InlineData("es", 1, 0)]
    [InlineData("it", 5, 1)]
    [InlineData("pt", 0, 1)]
    public void FormIndex_GermanicAndRomance_SingularOnlyForOne(string language, int count, int form)
    {
        PluralFormat.FormIndex(language, count).Should().Be(form);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(100, 1)]
    public void FormIndex_French_CountsZeroAsSingular(int count, int form)
    {
        PluralFormat.FormIndex("fr", count).Should().Be(form);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 2)]
    [InlineData(12, 2)]
    [InlineData(14, 2)]
    [InlineData(21, 2)]   // unlike Russian, 21 is "many" in Polish
    [InlineData(22, 1)]
    [InlineData(112, 2)]
    [InlineData(0, 2)]
    public void FormIndex_Polish_OneIsExactlyOne(int count, int form)
    {
        PluralFormat.FormIndex("pl", count).Should().Be(form);
    }

    [Theory]
    [InlineData(1, "1 файл")]
    [InlineData(2, "2 файли")]
    [InlineData(5, "5 файлів")]
    [InlineData(11, "11 файлів")]
    [InlineData(21, "21 файл")]
    [InlineData(0, "0 файлів")]
    public void Format_Ukrainian_PicksOneFewOrMany(int count, string expected)
    {
        PluralFormat.Format("uk", "{0} {0:plural:файл|файли|файлів}", count).Should().Be(expected);
    }

    [Fact]
    public void FormCount_MatchesTheFormsEachRuleCanReturn()
    {
        foreach (var language in LocalizationManager.SupportedLanguages)
        {
            var highest = Enumerable.Range(0, 200).Max(n => PluralFormat.FormIndex(language, n));
            (highest + 1).Should().Be(PluralFormat.FormCount(language), language);
        }
    }

    [Fact]
    public void EverySupportedLanguage_HasACulture()
    {
        foreach (var language in LocalizationManager.SupportedLanguages)
        {
            LocalizationManager.CultureOf(language).TwoLetterISOLanguageName.Should().Be(language);
        }
    }
}

/// <summary>
/// English and Russian titles of commands and columns live in code, every other language in its
/// dictionary. Without a loaded dictionary (a headless test, a first-run race) the others read as
/// English — never as a blank or a «[key]» placeholder.
/// </summary>
public class CatalogTitleTests
{
    [Fact]
    public void CommandTitles_InAnyOtherLanguage_FallBackToEnglishWithoutADictionary()
    {
        foreach (var command in TCV.App.Commands.CommandCatalog.All)
        {
            command.Title("de").Should().Be(command.TitleEn, command.Id);
            command.Category("pl").Should().Be(command.CategoryEn, command.Id);
        }
    }

    [Fact]
    public void ColumnTitles_InAnyOtherLanguage_FallBackToEnglishWithoutADictionary()
    {
        foreach (var column in TCV.App.Columns.ColumnCatalog.All)
        {
            column.Title("fr").Should().Be(column.TitleEn, column.Id);
        }
    }

    [Fact]
    public void CategoryKey_DropsTheSpaces()
    {
        TCV.App.Commands.CommandDescriptor.CategoryKey("Agent layer").Should().Be("CmdCat_Agentlayer");
        TCV.App.Commands.CommandDescriptor.CategoryKey("Files").Should().Be("CmdCat_Files");
    }
}
