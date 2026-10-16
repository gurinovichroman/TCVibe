using System.IO;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Services;
using TCV.App.Settings;
using TCV.App.Shells;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>The shells the command line offers: the catalog, the choice, and the Shells tab.</summary>
public class ShellSettingsTests
{
    // ── Catalog ───────────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_StartsWithCmd_WhatTheCommandLineRanBeforeItHadAChoice()
    {
        ShellCatalog.All[0].Name.Should().Be("cmd");
        ShellCatalog.Fallback.Command.Should().Be("cmd.exe");
        ShellCatalog.All.Select(s => s.Name).Should().Contain(["PowerShell 7", "Windows PowerShell", "Git Bash"]);
    }

    [Fact]
    public void Catalog_NamesAreUnique_TheyKeyTheChoiceInSettings()
    {
        ShellCatalog.All.Select(s => s.Name.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Catalog_EveryShellStaysOpenAfterTheCommand_AndSaysWhereTheCommandGoes()
    {
        foreach (var shell in ShellCatalog.All)
        {
            shell.Arguments.Should().Contain("%C", shell.Name);
            shell.Arguments.Should().MatchRegex(@"/k|-NoExit|exec bash", $"{shell.Name} would close before its output is read");
        }
    }

    [Fact]
    public void Catalog_GitBash_IsFoundOnlyWhereGitInstallsIt()
    {
        // A bare "bash" on PATH is WSL's System32\bash.exe.
        var shell = ShellCatalog.All.Single(s => s.Name == "Git Bash");

        shell.Command.Should().BeEmpty();
        shell.ProbePaths.Should().NotBeEmpty().And.OnlyContain(p => p.EndsWith(@"\bin\bash.exe"));
    }

    [Fact]
    public void Wsl_IsAskedOnlyOfTheRealInstall_NotTheSystem32Stub()
    {
        ShellCatalog.WslProgram.Should().Be(@"%ProgramFiles%\WSL\wsl.exe");
    }

    [Fact]
    public void WslDistributions_ReadTheListAsWslWritesIt_WithoutDockerDesktop()
    {
        // UTF-16 read as bytes leaves NULs; a BOM and blank lines happen too.
        const string output = "\uFEFFUbuntu\r\n\r\ndocker-desktop\r\nD\0e\0b\0i\0a\0n\0\r\nDocker-Desktop-Data\r\nubuntu\r\n  Arch  \n";

        ShellCatalog.WslDistributions(output).Should().Equal("Ubuntu", "Debian", "Arch");
    }

    [Fact]
    public void WslDistributions_OnlyDockerDesktop_IsNoShellAtAll()
    {
        ShellCatalog.WslDistributions("docker-desktop\r\n").Should().BeEmpty();
        ShellCatalog.WslDistributions(string.Empty).Should().BeEmpty();
    }

    [Fact]
    public void ForWslDistribution_RunsTheCommandInBashThere_AndStaysInBash()
    {
        var shell = ShellCatalog.ForWslDistribution(ShellCatalog.WslProgram, "Ubuntu-24.04");

        shell.Name.Should().Be("Ubuntu-24.04 (WSL)");
        shell.Command.Should().Be(ShellCatalog.WslProgram);
        ShellCommandLine.Build(@"C:\Program Files\WSL\wsl.exe", shell.Arguments, "ls -la | head", @"C:\x")
            .Arguments.Should().Equal("-d", "Ubuntu-24.04", "-e", "bash", "-lic", "ls -la | head; exec bash -li");
    }

    [Fact]
    public void ForWslDistribution_ANameWithABlank_StaysOneArgument()
    {
        var shell = ShellCatalog.ForWslDistribution(ShellCatalog.WslProgram, "My Distro");

        ShellCommandLine.Build("wsl.exe", shell.Arguments, "ls", @"C:\x").Arguments.Take(2).Should().Equal("-d", "My Distro");
    }

    [Fact]
    public void Catalog_GitBash_GetsTheCommandInsideTheArgumentThatKeepsItOpen()
    {
        var bash = ShellCatalog.All.Single(s => s.Name == "Git Bash");

        var launch = ShellCommandLine.Build(@"C:\Program Files\Git\bin\bash.exe", bash.Arguments, "echo \"a b\"", @"C:\x");

        launch.Arguments[^1].Should().Be("echo \"a b\"; exec bash --login -i");
    }

    [Fact]
    public void Pick_FindsTheNamedShell_IgnoringCaseAndBlanks()
    {
        var shells = new List<ShellProfile> { Profile("cmd"), Profile("Git Bash") };

        ShellCatalog.Pick(shells, " git bash ").Name.Should().Be("Git Bash");
    }

    [Fact]
    public void Pick_AShellThatIsGone_FallsBackToTheFirst()
    {
        var shells = new List<ShellProfile> { Profile("PowerShell 7"), Profile("WSL") };

        ShellCatalog.Pick(shells, "Nushell").Name.Should().Be("PowerShell 7");
        ShellCatalog.Pick(shells, null).Name.Should().Be("PowerShell 7");
    }

    [Fact]
    public void Pick_NoShellsAtAll_StillRunsInCmd()
    {
        ShellCatalog.Pick([], "anything").Name.Should().Be("cmd");
    }

    // ── Settings file ─────────────────────────────────────────────────────────

    [Fact]
    public void OldSettingsFile_WithoutShells_ReadsAsAutodetectAndCmd()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        settings.Normalize();

        settings.Shells.Profiles.Should().BeEmpty();
        settings.Shells.Selected.Should().BeEmpty();
    }

    [Fact]
    public void HandEditedNulls_AreRepaired()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{ "Shells": { "Profiles": null, "Selected": null } }""")!;
        settings.Normalize();

        settings.Shells.Profiles.Should().NotBeNull();
        settings.Shells.Selected.Should().BeEmpty();

        var none = JsonSerializer.Deserialize<AppSettings>("""{ "Shells": null }""")!;
        none.Normalize();
        none.Shells.Should().NotBeNull();
    }

    // ── Service ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Service_ConfiguredShells_AreOfferedAsTheyAre_AndTheChoiceIsKept()
    {
        await WithService(async (settings, service, file) =>
        {
            settings.Shells.Profiles.Add(Profile("PowerShell 7", "pwsh.exe"));
            settings.Shells.Profiles.Add(Profile("Nushell", "nu.exe"));

            (await service.AvailableAsync()).Select(s => s.Name).Should().Equal("PowerShell 7", "Nushell");
            (await service.CurrentAsync()).Name.Should().Be("PowerShell 7");

            service.Select("Nushell");

            (await service.CurrentAsync()).Name.Should().Be("Nushell");
            service.SelectedName.Should().Be("Nushell");
            File.ReadAllText(file).Should().Contain("Nushell");
        });
    }

    [Fact]
    public async Task Service_NothingPickedYet_NamesCmd()
    {
        await WithService((_, service, _) =>
        {
            service.SelectedName.Should().Be("cmd");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task Service_DetectedShells_AlwaysIncludeCmdFirst()
    {
        await WithService(async (_, service, _) =>
        {
            var shells = await service.AvailableAsync();

            shells[0].Name.Should().Be("cmd");
            shells.Select(s => s.Name).Should().OnlyHaveUniqueItems();
        });
    }

    [Fact]
    public async Task Service_AShellWithoutAProgram_IsNotStarted()
    {
        await WithService((_, service, _) =>
        {
            service.Run(Profile("broken", "  "), Path.GetTempPath(), "dir").Should().BeFalse();
            return Task.CompletedTask;
        });
    }

    // ── The Shells tab ────────────────────────────────────────────────────────

    [Fact]
    public void Tab_EmptyListStaysEmpty_ItMeansTheShellsFoundHere()
    {
        WithDialog(new AppSettings(), vm => vm.Shells.Should().BeEmpty());
    }

    [Fact]
    public void Tab_ShowsTheConfiguredShells()
    {
        var settings = new AppSettings();
        settings.Shells.Profiles.Add(Profile("MSYS2", @"C:\msys64\usr\bin\bash.exe", "-lic %C"));

        WithDialog(settings, vm =>
        {
            vm.Shells.Should().ContainSingle();
            vm.Shells[0].Arguments.Should().Be("-lic %C");
        });
    }

    [Fact]
    public void Tab_AddTwice_GivesDistinctNames()
    {
        WithDialog(new AppSettings(), vm =>
        {
            vm.AddShellCommand.Execute(null);
            vm.AddShellCommand.Execute(null);

            vm.Shells.Should().HaveCount(2);
            vm.Shells.Select(s => s.Name.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
        });
    }

    [Fact]
    public void Tab_Save_StoresTheProgramBare_AndDropsRowsWithoutOne()
    {
        var settings = new AppSettings();

        WithDialog(settings, vm =>
        {
            vm.Shells.Add(new ShellProfileViewModel { Name = " Cygwin ", Command = "\"C:\\cygwin64\\bin\\bash.exe\"", Arguments = " --login -i -c %C " });
            vm.Shells.Add(new ShellProfileViewModel { Name = "empty" });

            vm.SaveCommand.Execute(null);

            vm.SavedSuccessfully.Should().BeTrue();
        });

        var saved = settings.Shells.Profiles.Should().ContainSingle().Subject;
        saved.Name.Should().Be("Cygwin");
        saved.Command.Should().Be(@"C:\cygwin64\bin\bash.exe");
        saved.Arguments.Should().Be("--login -i -c %C");
    }

    [Fact]
    public void Tab_Save_TwoShellsWithTheSameName_IsBlocked()
    {
        var settings = new AppSettings();
        settings.Shells.Profiles.Add(Profile("bash", "bash.exe"));

        WithDialog(settings, vm =>
        {
            vm.Shells.Add(new ShellProfileViewModel { Name = "BASH", Command = "wsl.exe" });

            vm.SaveCommand.Execute(null);

            vm.SavedSuccessfully.Should().BeFalse();
            vm.DuplicateShellName().Should().Be("BASH");
        });

        settings.Shells.Profiles.Should().ContainSingle();
    }

    [Fact]
    public void Tab_InitialTab_IsWhatTheManagerCommandAsksFor()
    {
        WithDialog(new AppSettings(), vm => vm.InitialTab.Should().Be(SettingsDialogViewModel.ShellsTab),
            SettingsDialogViewModel.ShellsTab);
    }

    [Fact]
    public void ManagerCommand_IsInThePalette_WithoutADefaultGesture()
    {
        TCV.App.Commands.CommandCatalog.All.Select(c => c.Id).Should().Contain("ShellsManager");
        new HotkeyManager(new AppSettings()).GetBinding("ShellsManager").Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ShellProfile Profile(string name, string command = "cmd.exe", string arguments = "%C") =>
        new() { Name = name, Command = command, Arguments = arguments };

    private static async Task WithService(Func<AppSettings, ShellService, string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcv-shells-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "settings.json");
            var settings = new AppSettings();
            var service = new ShellService(
                settings,
                new SettingsSerializer(NullLogger<SettingsSerializer>.Instance, file),
                new TerminalService(NullLogger<TerminalService>.Instance),
                NullLogger<ShellService>.Instance);
            await body(settings, service, file);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void WithDialog(AppSettings settings, Action<SettingsDialogViewModel> body, string? initialTab = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcv-shells-" + Guid.NewGuid().ToString("N"));
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
}
