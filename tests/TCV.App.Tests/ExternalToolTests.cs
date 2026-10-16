using FluentAssertions;
using TCV.App.Navigation;
using TCV.App.Settings;

namespace TCV.App.Tests;

public class ExternalToolCatalogTests
{
    private static readonly string[] NoSelection = [];

    [Fact]
    public void Catalog_ListsTheEditorsAndAgentsWeClaimToSupport()
    {
        ExternalToolCatalog.Editors.Select(e => e.Command).Should()
            .Contain(["code", "cursor", "rider"], "the roadmap names these three explicitly");
        ExternalToolCatalog.Agents.Select(a => a.Command).Should().Contain("claude");
    }

    [Fact]
    public void Catalog_EveryEntryIsLaunchable()
    {
        foreach (var tool in ExternalToolCatalog.Editors.Concat(ExternalToolCatalog.Agents))
        {
            tool.Name.Should().NotBeNullOrWhiteSpace();
            tool.Command.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void Catalog_AgentsAllRunInATerminal_TheyAreTuis()
    {
        ExternalToolCatalog.Agents.Should().OnlyContain(a => a.InTerminal);
    }

    [Fact]
    public void Catalog_NamesAreUnique_TheyKeyTheDefaultInSettings()
    {
        var names = ExternalToolCatalog.Editors.Concat(ExternalToolCatalog.Agents)
            .Select(t => t.Name).ToList();

        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Expand_FolderAndFile_PassesBoth()
    {
        var args = ExternalToolCatalog.ExpandArguments(
            "\"%P\" %F", @"D:\repo", [@"D:\repo\src\app.cs"]);

        args.Should().Be(@"""D:\repo"" D:\repo\src\app.cs");
    }

    [Fact]
    public void Expand_NothingSelected_DegradesToOpeningTheFolder()
    {
        var args = ExternalToolCatalog.ExpandArguments("\"%P\" %F", @"D:\repo", NoSelection);

        args.Should().Be(@"""D:\repo""", "a dangling %F must not leave stray whitespace");
    }

    [Fact]
    public void Expand_PathsWithSpaces_AreQuoted()
    {
        var args = ExternalToolCatalog.ExpandArguments(
            "%F", @"D:\my repo", [@"D:\my repo\a b.cs", @"D:\my repo\c.cs"]);

        args.Should().Be(@"""D:\my repo\a b.cs"" ""D:\my repo\c.cs""");
    }

    [Fact]
    public void Expand_KeepsDoubleSpacesInsideAPath()
    {
        // A directory really can contain two spaces; normalising whitespace would break its path
        var args = ExternalToolCatalog.ExpandArguments("\"%P\" %F", @"D:\odd  name", []);

        args.Should().Be(@"""D:\odd  name""");
    }

    [Fact]
    public void Expand_NameAndExtensionMacros_UseTheFirstSelection()
    {
        var args = ExternalToolCatalog.ExpandArguments(
            "%N.%E", @"D:\repo", [@"D:\repo\readme.md", @"D:\repo\other.txt"]);

        args.Should().Be("readme.md");
    }

    [Fact]
    public void Expand_EmptyTemplate_StaysEmpty()
    {
        ExternalToolCatalog.ExpandArguments(string.Empty, @"D:\repo", NoSelection).Should().BeEmpty();
    }

    [Fact]
    public void ToSettings_CarriesTheTemplateIntoTheStoredShape()
    {
        var stored = ExternalToolCatalog.Editors.First(e => e.Command == "code").ToSettings();

        stored.Name.Should().Be("VS Code");
        stored.Command.Should().Be("code");
        stored.Arguments.Should().Contain("%P");
        stored.InTerminal.Should().BeFalse();
    }
}

public class CommandResolverTests
{
    private static readonly string[] Path = [@"C:\bin", @"C:\tools\"];
    private static readonly string[] Exts = [".exe", ".cmd"];

    private static Func<string, bool> Existing(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    [Fact]
    public void Resolve_ExactFileOnPath_IsFound()
    {
        CommandResolver.Resolve("tool.exe", Path, Exts, Existing(@"C:\bin\tool.exe"))
            .Should().Be(@"C:\bin\tool.exe");
    }

    [Fact]
    public void Resolve_ShimWithoutExtension_TriesPathExt()
    {
        // This is what makes agent CLIs work: npm installs them as claude.cmd, never as "claude"
        CommandResolver.Resolve("claude", Path, Exts, Existing(@"C:\bin\claude.cmd"))
            .Should().Be(@"C:\bin\claude.cmd");
    }

    [Fact]
    public void Resolve_UppercasePathExt_StillResolvesALowercaseShim()
    {
        // Windows reports PATHEXT as ".COM;.EXE;.CMD…" — the probe must not care about case
        CommandResolver.Resolve("claude", Path, [".CMD"], Existing(@"C:\bin\claude.cmd"))
            .Should().BeEquivalentTo(@"C:\bin\claude.cmd");
    }

    [Fact]
    public void Resolve_WalksPathInOrder()
    {
        var resolved = CommandResolver.Resolve(
            "code", Path, Exts, Existing(@"C:\tools\code.cmd", @"C:\bin\code.cmd"));

        resolved.Should().Be(@"C:\bin\code.cmd", "the first PATH entry wins, as in a shell");
    }

    [Fact]
    public void Resolve_TrailingSeparatorInPathEntry_IsHandled()
    {
        CommandResolver.Resolve("t", Path, Exts, Existing(@"C:\tools\t.exe"))
            .Should().Be(@"C:\tools\t.exe");
    }

    [Fact]
    public void Resolve_AbsolutePath_IsNotSearchedOnPath()
    {
        CommandResolver.Resolve(@"D:\apps\my editor.exe", Path, Exts,
            Existing(@"D:\apps\my editor.exe")).Should().Be(@"D:\apps\my editor.exe");

        CommandResolver.Resolve(@"D:\apps\gone.exe", Path, Exts, Existing(@"C:\bin\gone.exe"))
            .Should().BeNull();
    }

    [Fact]
    public void Resolve_QuotedCommand_IsUnquoted()
    {
        CommandResolver.Resolve("\"tool.exe\"", Path, Exts, Existing(@"C:\bin\tool.exe"))
            .Should().Be(@"C:\bin\tool.exe");
    }

    [Fact]
    public void Resolve_MissingCommand_IsNull()
    {
        CommandResolver.Resolve("nosuch", Path, Exts, Existing(@"C:\bin\other.exe")).Should().BeNull();
        CommandResolver.Exists("nosuch", Path, Exts, Existing()).Should().BeFalse();
    }

    [Fact]
    public void Resolve_BlankCommand_IsNull()
    {
        CommandResolver.Resolve("  ", Path, Exts, Existing(@"C:\bin\tool.exe")).Should().BeNull();
        CommandResolver.Resolve(null, Path, Exts, _ => true).Should().BeNull();
    }

    [Fact]
    public void Resolve_NoPathExt_FallsBackToTheUsualWindowsSet()
    {
        CommandResolver.Resolve("tool", Path, [], Existing(@"C:\bin\tool.cmd"))
            .Should().Be(@"C:\bin\tool.cmd");
    }

    [Fact]
    public void Resolve_ExtensionsWithoutDot_AreAccepted()
    {
        CommandResolver.Resolve("tool", Path, ["exe"], Existing(@"C:\bin\tool.exe"))
            .Should().Be(@"C:\bin\tool.exe");
    }

    [Fact]
    public void Resolve_DoesNotDoubleUpAnExtensionItAlreadyHas()
    {
        // "tool.exe" must not be probed as "tool.exe.exe" and silently match a decoy
        CommandResolver.Resolve("tool.exe", Path, Exts, Existing(@"C:\bin\tool.exe.exe"))
            .Should().BeNull();
    }
}

public class ExternalToolSettingsTests
{
    [Fact]
    public void Defaults_AreEmpty_MeaningAutodetect()
    {
        var settings = new AppSettings();

        settings.ExternalTools.Editors.Should().BeEmpty();
        settings.ExternalTools.Agents.Should().BeEmpty();
        settings.ExternalTools.DefaultEditor.Should().BeEmpty();
        settings.ExternalTools.DefaultAgent.Should().BeEmpty();
    }

    [Fact]
    public void ExternalTools_SurviveASettingsRoundTrip()
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tcv-ext-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var file = System.IO.Path.Combine(dir, "settings.json");
            var serializer = new SettingsSerializer(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsSerializer>.Instance, file);
            var settings = new AppSettings();
            settings.ExternalTools.Editors.Add(new ExternalTool
            {
                Name = "Cursor", Command = "cursor", Arguments = "\"%P\" %F",
            });
            settings.ExternalTools.Agents.Add(new ExternalTool
            {
                Name = "Claude Code", Command = "claude", InTerminal = true,
            });
            settings.ExternalTools.DefaultEditor = "Cursor";

            serializer.Save(settings);
            var loaded = serializer.Load();

            loaded.ExternalTools.Editors.Should().ContainSingle()
                .Which.Arguments.Should().Be("\"%P\" %F");
            loaded.ExternalTools.Agents.Should().ContainSingle()
                .Which.InTerminal.Should().BeTrue();
            loaded.ExternalTools.DefaultEditor.Should().Be("Cursor");
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}

public class SettingsDialogExternalToolsTests
{
    /// <summary>Runs <paramref name="body"/> against a settings dialog backed by a throwaway file.</summary>
    private static void WithDialog(AppSettings settings, Action<ViewModels.Dialogs.SettingsDialogViewModel> body)
    {
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tcv-set-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var serializer = new SettingsSerializer(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SettingsSerializer>.Instance,
                System.IO.Path.Combine(dir, "settings.json"));

            body(new ViewModels.Dialogs.SettingsDialogViewModel(settings, serializer));
        }
        finally
        {
            try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Save_InTheAutodetectState_KeepsTheDefaultThePickerRemembered()
    {
        // The grids are empty (nothing customised), so there is no row to select — saving anything
        // else in the dialog must not reset which editor Ctrl+E starts.
        var settings = new AppSettings();
        settings.ExternalTools.DefaultEditor = "Cursor";
        settings.ExternalTools.DefaultAgent = "Claude Code";

        WithDialog(settings, vm =>
        {
            vm.ThemeName = "Light";
            vm.SaveCommand.Execute(null);
        });

        settings.Theme.ThemeName.Should().Be("Light");
        settings.ExternalTools.DefaultEditor.Should().Be("Cursor");
        settings.ExternalTools.DefaultAgent.Should().Be("Claude Code");
    }

    [Fact]
    public void Save_WithAConfiguredList_WritesTheSelectedDefault()
    {
        var settings = new AppSettings();
        settings.ExternalTools.Editors.Add(new ExternalTool { Name = "VS Code", Command = "code" });
        settings.ExternalTools.Editors.Add(new ExternalTool { Name = "Rider", Command = "rider" });
        settings.ExternalTools.DefaultEditor = "VS Code";

        WithDialog(settings, vm =>
        {
            vm.DefaultEditor!.Name.Should().Be("VS Code", "the stored default is preselected");

            vm.DefaultEditor = vm.Editors.Single(e => e.Name == "Rider");
            vm.SaveCommand.Execute(null);
        });

        settings.ExternalTools.DefaultEditor.Should().Be("Rider");
    }
}

public class TerminalArgumentQuotingTests
{
    [Theory]
    [InlineData(@"D:\", @"""D:\\""")]                    // drive root — the case that broke wt.exe
    [InlineData(@"D:\repo", @"""D:\repo""")]
    [InlineData(@"D:\my repo", @"""D:\my repo""")]
    [InlineData(@"\\server\share\", @"""\\server\share\\""")]
    public void QuoteArgument_DoublesATrailingBackslashSoItCannotEscapeTheQuote(string path, string expected)
    {
        Services.TerminalService.QuoteArgument(path).Should().Be(expected);
    }

    [Fact]
    public void QuoteArgument_LeavesInnerBackslashesAlone()
    {
        // Only the run before the closing quote is special to CommandLineToArgvW.
        Services.TerminalService.QuoteArgument(@"C:\a\b\c").Should().Be(@"""C:\a\b\c""");
    }
}

public class ExternalToolSafetyTests
{
    private const string Q = "\"";

    [Fact]
    public void Expand_FileNameWithAmpersand_IsQuoted()
    {
        var args = ExternalToolCatalog.ExpandArguments("%F", @"D:\repo", [@"D:\repo\a&calc.txt"]);

        args.Should().Be(Q + @"D:\repo\a&calc.txt" + Q);
    }

    [Fact]
    public void Expand_DriveRootInsideTemplateQuotes_DoesNotEscapeTheClosingQuote()
    {
        var args = ExternalToolCatalog.ExpandArguments("\"%P\" %F", @"C:\", []);

        args.Should().Be(Q + @"C:\" + @"\" + Q);
    }

    [Fact]
    public void Expand_ForCmd_RefusesPercentAndBang()
    {
        var percent = () => ExternalToolCatalog.ExpandArguments("%F", @"D:\r", [@"D:\r\100%.txt"], forCmd: true);
        var bang = () => ExternalToolCatalog.ExpandArguments("%F", @"D:\r", [@"D:\r\hi!.txt"], forCmd: true);

        percent.Should().Throw<TCV.Core.Domain.UnsafeMacroValueException>();
        bang.Should().Throw<TCV.Core.Domain.UnsafeMacroValueException>();
    }

    [Fact]
    public void Expand_NotForCmd_AllowsPercent()
    {
        ExternalToolCatalog.ExpandArguments("%F", @"D:\r", [@"D:\r\100%.txt"]).Should().Be(Q + @"D:\r\100%.txt" + Q);
    }

    [Fact]
    public void Expand_ValueHoldingAMacro_IsNotExpandedTwice()
    {
        var args = ExternalToolCatalog.ExpandArguments("%F %N", @"D:\r", [@"D:\r\a%Nb.txt"]);

        args.Should().Be(Q + @"D:\r\a%Nb.txt" + Q + " " + Q + "a%Nb" + Q);
    }

    [Fact]
    public void QuoteArgument_TerminalServiceDelegatesToTheSharedRoutine()
    {
        Services.TerminalService.QuoteArgument(@"C:\").Should().Be(TCV.Core.Domain.ShellQuoting.QuoteArgument(@"C:\"));
    }
}

public class CommandResolverShimTests
{
    private static readonly string[] Dirs = [@"C:\vscode\bin"];
    private static readonly string[] Exts = [".com", ".exe", ".cmd"];

    [Fact]
    public void Resolve_ExtensionlessShellScriptBesideTheCmdShim_PicksTheCmd()
    {
        // VS Code and npm ship a POSIX "code"/"claude" script next to code.cmd; ShellExecute on it
        // opens the "how do you want to open this file" dialog
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\vscode\bin\code", @"C:\vscode\bin\code.cmd",
        };

        CommandResolver.Resolve("code", Dirs, Exts, existing.Contains).Should().Be(@"C:\vscode\bin\code.cmd");
    }

    [Fact]
    public void Resolve_OnlyAnExtensionlessFile_IsNotRunnable()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\vscode\bin\code" };

        CommandResolver.Resolve("code", Dirs, Exts, existing.Contains).Should().BeNull();
    }
}
