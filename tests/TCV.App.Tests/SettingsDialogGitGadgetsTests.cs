using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The git and gadget tabs added by 6.30 — until then both sections could only be edited by hand
/// in <c>settings.json</c>. What matters is the round trip: what the tab shows is what was stored,
/// and what it saves is loadable again.
/// </summary>
public class SettingsDialogGitGadgetsTests
{
    private static void WithDialog(AppSettings settings, Action<SettingsDialogViewModel> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcv-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var serializer = new SettingsSerializer(
                NullLogger<SettingsSerializer>.Instance, Path.Combine(dir, "settings.json"));
            body(new SettingsDialogViewModel(settings, serializer));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    // ── Git tab ───────────────────────────────────────────────────────────────

    [Fact]
    public void Git_settings_survive_a_round_trip_through_the_tab()
    {
        var settings = new AppSettings();
        settings.Git.WorktreeRoot = @"D:\wt";
        settings.Git.WorktreeSetupCommand = "dotnet restore";
        settings.Git.WorktreeCarryPatterns = [".env", "*.local.json"];
        settings.Git.WorktreeCreatesWorkspace = false;

        WithDialog(settings, vm =>
        {
            vm.WorktreeRoot.Should().Be(@"D:\wt");
            vm.WorktreeCarryPatterns.Should().Contain(".env").And.Contain("*.local.json");
            vm.WorktreeCreatesWorkspace.Should().BeFalse();

            vm.WorktreeSetupCommand = "  npm install  ";
            vm.WorktreeCreatesWorkspace = true;
            vm.SaveCommand.Execute(null);
        });

        settings.Git.WorktreeSetupCommand.Should().Be("npm install");
        settings.Git.WorktreeCreatesWorkspace.Should().BeTrue();
        settings.Git.WorktreeCarryPatterns.Should().Equal(".env", "*.local.json");
    }

    [Theory]
    [InlineData(".env\n*.local.json")]
    [InlineData(".env, *.local.json")]
    [InlineData(".env;*.local.json;")]
    public void Carry_patterns_accept_commas_semicolons_and_new_lines(string typed)
    {
        SettingsDialogViewModel.ParsePatterns(typed).Should().Equal(".env", "*.local.json");
    }

    [Fact]
    public void An_empty_pattern_box_means_use_the_built_in_defaults()
    {
        // Empty is a meaningful value here (WorktreeSetupPlan.DefaultCarryPatterns), not a blank
        // row — so it must not turn into a list holding one empty string.
        SettingsDialogViewModel.ParsePatterns("  \n ").Should().BeEmpty();
    }

    // ── Gadgets tab ───────────────────────────────────────────────────────────

    [Fact]
    public void Gadgets_survive_a_round_trip_through_the_tab()
    {
        var settings = new AppSettings();
        settings.Gadgets.Add(new Gadget
        {
            Name = "build",
            Command = "dotnet",
            Arguments = "build",
            WorkingDirectory = @"D:\repo",
            InTerminal = true,
        });

        WithDialog(settings, vm =>
        {
            var row = vm.Gadgets.Should().ContainSingle().Subject;
            row.Name.Should().Be("build");
            row.WorkingDirectory.Should().Be(@"D:\repo");

            row.Arguments = "build -c Release";
            vm.SaveCommand.Execute(null);
        });

        settings.Gadgets.Should().ContainSingle()
            .Which.Arguments.Should().Be("build -c Release");
    }

    [Fact]
    public void A_gadget_without_a_command_is_dropped_on_save()
    {
        // Same rule as the editors grid: a nameless or commandless row would appear in the Ctrl+F2
        // picker and then fail to start.
        var settings = new AppSettings();

        WithDialog(settings, vm =>
        {
            vm.AddGadgetCommand.Execute(null);
            vm.Gadgets.Single().Command = "   ";
            vm.SaveCommand.Execute(null);
        });

        settings.Gadgets.Should().BeEmpty();
    }

    [Fact]
    public void Removing_a_gadget_takes_effect_on_save()
    {
        var settings = new AppSettings();
        settings.Gadgets.Add(new Gadget { Name = "build", Command = "dotnet" });
        settings.Gadgets.Add(new Gadget { Name = "test", Command = "dotnet" });

        WithDialog(settings, vm =>
        {
            vm.RemoveGadgetCommand.Execute(vm.Gadgets.First(g => g.Name == "build"));
            vm.SaveCommand.Execute(null);
        });

        settings.Gadgets.Should().ContainSingle().Which.Name.Should().Be("test");
    }
}
