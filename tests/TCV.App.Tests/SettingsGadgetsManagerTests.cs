using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The Gadgets tab as the gadget manager (6.50, «Manage gadgets…»): more than one gadget can be
/// added, names stay unique, and a quoted program path is stored bare.
/// </summary>
public class SettingsGadgetsManagerTests
{
    private static void WithDialog(AppSettings settings, Action<SettingsDialogViewModel> body, string? initialTab = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcv-gadgets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var serializer = new SettingsSerializer(
                NullLogger<SettingsSerializer>.Instance, Path.Combine(dir, "settings.json"));
            body(new SettingsDialogViewModel(settings, serializer) { InitialTab = initialTab });
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AddGadget_Twice_GivesDistinctNames()
    {
        WithDialog(new AppSettings(), vm =>
        {
            vm.AddGadgetCommand.Execute(null);
            vm.AddGadgetCommand.Execute(null);
            vm.AddGadgetCommand.Execute(null);

            vm.Gadgets.Should().HaveCount(3);
            vm.Gadgets.Select(g => g.Name.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
            vm.Gadgets[0].Command.Should().Be("dotnet", "the first row is a working example");
        });
    }

    [Fact]
    public void Save_TwoGadgetsWithTheSameName_IsBlocked()
    {
        var settings = new AppSettings();
        settings.Gadgets.Add(new Gadget { Name = "build", Command = "dotnet", Arguments = "build" });

        WithDialog(settings, vm =>
        {
            vm.AddGadgetCommand.Execute(null);
            var second = vm.Gadgets[1];
            second.Name = " Build ";
            second.Command = "msbuild";

            vm.SaveCommand.Execute(null);

            vm.SavedSuccessfully.Should().BeFalse();
            vm.SaveError.Should().NotBeEmpty();
            vm.DuplicateGadgetName().Should().Be("Build");
        });

        settings.Gadgets.Should().ContainSingle().Which.Command.Should().Be("dotnet");
    }

    [Fact]
    public void Save_ADuplicateRowWithoutACommand_IsDroppedRatherThanBlocking()
    {
        var settings = new AppSettings();
        settings.Gadgets.Add(new Gadget { Name = "build", Command = "dotnet" });

        WithDialog(settings, vm =>
        {
            vm.AddGadgetCommand.Execute(null);
            vm.Gadgets[1].Name = "build";   // no command: it is not saved, so it collides with nothing

            vm.SaveCommand.Execute(null);

            vm.SavedSuccessfully.Should().BeTrue();
        });

        settings.Gadgets.Should().ContainSingle();
    }

    [Fact]
    public void Save_QuotedCommandAndFolder_AreStoredBare()
    {
        var settings = new AppSettings();

        WithDialog(settings, vm =>
        {
            vm.AddGadgetCommand.Execute(null);
            var row = vm.Gadgets.Single();
            row.Command = @"  ""C:\Program Files\Tool\tool.exe""  ";
            row.WorkingDirectory = @"""D:\My Repo""";

            vm.SaveCommand.Execute(null);
        });

        var saved = settings.Gadgets.Single();
        saved.Command.Should().Be(@"C:\Program Files\Tool\tool.exe");
        saved.WorkingDirectory.Should().Be(@"D:\My Repo");
    }

    [Theory]
    [InlineData(@"""C:\a b\x.exe""", @"C:\a b\x.exe")]
    [InlineData(@"  C:\a b\x.exe  ", @"C:\a b\x.exe")]
    [InlineData(@"""half", @"""half")]
    [InlineData(@"""", @"""")]
    [InlineData("", "")]
    public void Unquote_StripsOnlyAPairOfSurroundingQuotes(string typed, string expected)
    {
        GadgetViewModel.Unquote(typed).Should().Be(expected);
    }

    [Fact]
    public void InitialTab_IsWhatTheManagerCommandAsksFor()
    {
        WithDialog(new AppSettings(), vm => vm.InitialTab.Should().Be(SettingsDialogViewModel.GadgetsTab),
            SettingsDialogViewModel.GadgetsTab);
    }
}
