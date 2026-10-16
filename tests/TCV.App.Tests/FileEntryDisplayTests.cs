using System.IO;
using FluentAssertions;
using TCV.App.Converters;
using TCV.App.ViewModels;
using Xunit;

namespace TCV.App.Tests;

public class FileEntryDisplayTests
{
    [Fact]
    public void Format_ZeroBytes_IsARealSizeNotAFolderMarker()
    {
        FileSizeConverter.Format(0).Should().NotContain("DIR").And.EndWith(" B");
    }

    [Fact]
    public void Format_Negative_IsBlank()
    {
        FileSizeConverter.Format(-1).Should().BeEmpty();
    }

    [Fact]
    public void Format_Megabytes_UsesMbUnit()
    {
        FileSizeConverter.Format(5L * 1024 * 1024).Should().EndWith(" MB");
    }

    [Fact]
    public void FormatAttributes_None_IsAllDashes()
    {
        FileEntryViewModel.FormatAttributes(FileAttributes.Normal).Should().Be("----");
    }

    [Fact]
    public void FormatAttributes_DirectoryFlag_IsNotSpelledOut()
    {
        FileEntryViewModel.FormatAttributes(FileAttributes.Directory).Should().Be("----");
    }

    [Fact]
    public void FormatAttributes_AllFlags_ShowsRahs()
    {
        FileEntryViewModel.FormatAttributes(
            FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.Hidden | FileAttributes.System)
            .Should().Be("rahs");
    }

    [Fact]
    public void ParentEntry_HasBlankAttributesAndSize()
    {
        var parent = FileEntryViewModel.CreateParent();

        parent.AttributesDisplay.Should().BeEmpty();
        parent.SizeDisplay.Should().BeEmpty();
    }

    [Fact]
    public void Format_UnitNames_FollowTheLanguage()
    {
        try
        {
            FileSizeConverter.UseUnits("Б", "КБ", "МБ", "ГБ");
            FileSizeConverter.Format(248).Should().EndWith(" Б");
            FileSizeConverter.Format(5L * 1024 * 1024).Should().EndWith(" МБ");
        }
        finally
        {
            FileSizeConverter.UseUnits("B", "KB", "MB", "GB");
        }
    }

    [Fact]
    public void DateFormatConverter_UsesTheLanguagePattern()
    {
        try
        {
            DateFormatConverter.UsePattern("dd.MM.yyyy  HH:mm");
            DateFormatConverter.Instance.Convert(new DateTime(2026, 10, 12, 10, 41, 0), typeof(string), null!,
                    System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be("12.10.2026  10:41");
        }
        finally
        {
            DateFormatConverter.UsePattern(DateFormatConverter.DefaultPattern);
        }
    }

    [Fact]
    public void DateFormatConverter_DefaultDate_IsBlank()
    {
        DateFormatConverter.Instance.Convert(default(DateTime), typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(string.Empty);
    }
}
