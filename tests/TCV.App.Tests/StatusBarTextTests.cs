using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using TCV.App.Localization;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The panel status line reads as a sentence in both languages (6.55): «1 folder, 1 file», not
/// «1 folders, 1 files». Checked against the shipped dictionaries through the same formatter the
/// status bar uses (<see cref="LocalizationManager.Format"/> → <see cref="PluralFormat"/>).
/// </summary>
public class StatusBarTextTests
{
    private static readonly string AppDir = SourceTree.AppDirectory;

    private static string Text(string language, string key)
    {
        var xaml = File.ReadAllText(Path.Combine(AppDir, "Localization", $"Strings.{language}.xaml"));
        var match = Regex.Match(xaml, $@"<sys:String x:Key=""{key}""[^>]*>(.*?)</sys:String>", RegexOptions.Singleline);
        match.Success.Should().BeTrue($"{key} must be in Strings.{language}.xaml");
        return match.Groups[1].Value;
    }

    [Theory]
    [InlineData(1, 1, "1 folder, 1 file")]
    [InlineData(0, 2, "0 folders, 2 files")]
    [InlineData(3, 21, "3 folders, 21 files")]
    public void Summary_English_UsesSingularAndPlural(int folders, int files, string expected)
    {
        PluralFormat.Format("en", Text("en", "Status_Summary"), folders, files, "10 B").Should().Be(expected);
    }

    [Theory]
    [InlineData(1, 1, "1 папка, 1 файл")]
    [InlineData(2, 3, "2 папки, 3 файла")]
    [InlineData(5, 11, "5 папок, 11 файлов")]
    [InlineData(21, 22, "21 папка, 22 файла")]
    public void Summary_Russian_UsesAllThreeForms(int folders, int files, string expected)
    {
        PluralFormat.Format("ru", Text("ru", "Status_Summary"), folders, files, "10 Б").Should().Be(expected);
    }

    [Theory]
    [InlineData("en", "Selected 2 of 40 (1 KB)")]
    [InlineData("ru", "Выбрано 2 из 40 (1 KB)")]
    public void Selected_ReadsTheSameThroughThePluralFormatter(string language, string expected)
    {
        PluralFormat.Format(language, Text(language, "Status_Selected"), 2, 40, "1 KB").Should().Be(expected);
    }
}

/// <summary>The second way ahead of the confirmation window («Merge» / «Replace» / «Cancel»).</summary>
public class ConfirmChoiceTests
{
    [Fact]
    public void Alternate_ClosesWithTheAlternateChoice_AsAnAnswerThatGoesAhead()
    {
        var vm = new ConfirmDialogViewModel("T", "M", "Merge", "Cancel", isWarning: true, alternateText: "Replace");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.AlternateCommand.Execute(null);

        closed.Should().BeTrue();
        vm.Choice.Should().Be(ConfirmChoice.Alternate);
        vm.DialogResult.Should().BeTrue();
        vm.HasAlternate.Should().BeTrue();
    }

    [Fact]
    public void Cancel_IsTheChoiceThatDoesNotGoAhead()
    {
        var vm = new ConfirmDialogViewModel("T", "M", "Merge", "Cancel", isWarning: false, alternateText: "Replace");

        vm.CancelCommand.Execute(null);

        vm.Choice.Should().Be(ConfirmChoice.Cancel);
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void PlainQuestion_HasNoAlternate()
    {
        var vm = new ConfirmDialogViewModel("T", "M", "Yes", "No", isWarning: false);

        vm.HasAlternate.Should().BeFalse();
        vm.ConfirmCommand.Execute(null);
        vm.Choice.Should().Be(ConfirmChoice.Confirm);
    }
}
