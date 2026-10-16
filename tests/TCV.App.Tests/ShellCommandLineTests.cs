using System.Text.RegularExpressions;
using FluentAssertions;
using TCV.App.Shells;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>
/// The command line's shells: a template is split as argv before the command goes in, each shell gets
/// the command the way it reads its line, and wt.exe hands every shell exactly that line.
/// </summary>
public class ShellCommandLineTests
{
    private const string Folder = @"D:\Projects\app";

    // ── Splitting a template (CommandLineToArgvW rules) ──────────────────────

    [Theory]
    [InlineData("/s /k %C", new[] { "/s", "/k", "%C" })]
    [InlineData("--login -i -c \"%C; exec bash --login -i\"", new[] { "--login", "-i", "-c", "%C; exec bash --login -i" })]
    [InlineData("  a \t b  ", new[] { "a", "b" })]
    [InlineData("a\\\"b", new[] { "a\"b" })]
    [InlineData("\"a\\\\\" b", new[] { "a\\", "b" })]
    [InlineData(@"C:\dir\ x", new[] { @"C:\dir\", "x" })]
    [InlineData("\"\" x", new[] { "", "x" })]
    [InlineData("\"a\"\"b\"", new[] { "a\"b" })]
    [InlineData("pre\"quoted part\"post", new[] { "prequoted partpost" })]
    public void SplitArguments_ReadsTheLineAsArgvProgramsDo(string text, string[] expected)
    {
        ShellCommandLine.SplitArguments(text).Should().Equal(expected);
    }

    [Fact]
    public void SplitArguments_EmptyText_HasNoArguments()
    {
        ShellCommandLine.SplitArguments(string.Empty).Should().BeEmpty();
        ShellCommandLine.SplitArguments("   ").Should().BeEmpty();
    }

    // ── Putting the command in ────────────────────────────────────────────────

    [Fact]
    public void Build_Cmd_GetsTheCommandAsTyped_InOnePairOfQuotesThatSlashSStrips()
    {
        var launch = ShellCommandLine.Build("cmd.exe", "/s /k %C", "echo \"a b\" & dir", Folder);

        launch.ReadsRawLine.Should().BeTrue();
        launch.Arguments.Should().Equal("/s", "/k", "echo \"a b\" & dir");
        ShellCommandLine.Arguments(launch).Should().Be("/s /k \"echo \"a b\" & dir\"");
    }

    [Fact]
    public void Build_Cmd_ACommandWithoutBlanks_IsNotQuoted()
    {
        var launch = ShellCommandLine.Build(@"C:\Windows\System32\cmd.exe", "/s /k %C", "dir", Folder);

        ShellCommandLine.Arguments(launch).Should().Be("/s /k dir");
    }

    [Fact]
    public void Build_PowerShell_EscapesQuotesSoTheShellReadsTheCommandAsTyped()
    {
        const string command = "Get-ChildItem | ? { $_.Name -like \"*.cs\" }";
        var launch = ShellCommandLine.Build("pwsh.exe", "-NoLogo -NoExit -Command %C", command, Folder);

        launch.ReadsRawLine.Should().BeFalse();
        ShellCommandLine.Arguments(launch).Should()
            .Be("-NoLogo -NoExit -Command \"Get-ChildItem | ? { $_.Name -like \\\"*.cs\\\" }\"");
        ShellCommandLine.SplitArguments(ShellCommandLine.Arguments(launch)).Should().Equal(launch.Arguments);
        launch.Arguments[^1].Should().Be(command);
    }

    [Fact]
    public void Build_Bash_TheCommandIsPartOfTheArgumentThatKeepsTheShellOpen()
    {
        var launch = ShellCommandLine.Build(
            @"C:\Program Files\Git\bin\bash.exe", "--login -i -c \"%C; exec bash --login -i\"", "npm test", Folder);

        launch.Arguments.Should().Equal("--login", "-i", "-c", "npm test; exec bash --login -i");
    }

    [Fact]
    public void Build_TemplateWithoutTheCommandMacro_AppendsTheCommand()
    {
        var launch = ShellCommandLine.Build("nu.exe", "-e", "ls | first 3", Folder);

        launch.Arguments.Should().Equal("-e", "ls | first 3");
    }

    [Fact]
    public void Build_EmptyTemplate_RunsTheCommandAsTheOnlyArgument()
    {
        ShellCommandLine.Build("python.exe", string.Empty, "-V", Folder).Arguments.Should().Equal("-V");
    }

    [Fact]
    public void Build_FolderMacro_IsThePanelFolder()
    {
        var launch = ShellCommandLine.Build("wsl.exe", "--cd %P -e bash -lic %C", "ls", Folder);

        launch.Arguments.Should().Equal("--cd", Folder, "-e", "bash", "-lic", "ls");
    }

    [Fact]
    public void Build_TheCommandIsNotExpandedAgain_AndEnvironmentVariablesStayForTheShell()
    {
        var launch = ShellCommandLine.Build("cmd.exe", "/s /k %C", "echo %P %PATH% %C", Folder);

        launch.Arguments[^1].Should().Be("echo %P %PATH% %C", "a macro in the typed text is the shell's business");
        ShellCommandLine.Build("bash.exe", "-c \"%C; echo $HOME %USERPROFILE%\"", "ls", Folder)
            .Arguments[^1].Should().Be("ls; echo $HOME %USERPROFILE%");
    }

    [Theory]
    [InlineData("cmd", true)]
    [InlineData("cmd.exe", true)]
    [InlineData("CMD.EXE", true)]
    [InlineData(@"C:\Windows\System32\cmd.exe", true)]
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\"", true)]
    [InlineData("pwsh.exe", false)]
    [InlineData("cmdx.exe", false)]
    [InlineData(@"C:\tools\cmd\bash.exe", false)]
    [InlineData("", false)]
    public void IsCmd_KnowsCmdWhereverItLives(string program, bool expected)
    {
        ShellCommandLine.IsCmd(program).Should().Be(expected);
    }

    public static TheoryData<string> TrickyCommands() => new()
    {
        "ls -la",
        "echo \"a b\"",
        "dir C:\\",
        "dir \"C:\\Program Files\\\"",
        @"echo C:\a\\b",
        "find . -name '*.tmp' -exec rm {} \\;",
        "npm ci; npm run build",
        "echo a\tb",
        "echo \"\"",
        "git log --format=\"%h %s\" -n 3",
        "echo привет; exit",
    };

    [Theory]
    [MemberData(nameof(TrickyCommands))]
    public void Arguments_ArgvShell_ReadsBackExactlyTheValues(string command)
    {
        var launch = ShellCommandLine.Build("pwsh.exe", "-NoExit -Command %C", command, Folder);

        ShellCommandLine.SplitArguments(ShellCommandLine.Arguments(launch)).Should().Equal(launch.Arguments);
    }

    // ── Through Windows Terminal ──────────────────────────────────────────────

    public static TheoryData<string, string, string> ShellsAndCommands()
    {
        var data = new TheoryData<string, string, string>();
        foreach (var command in TrickyCommands())
        {
            data.Add("cmd.exe", "/s /k %C", command);
            data.Add("pwsh.exe", "-NoLogo -NoExit -Command %C", command);
            data.Add(@"C:\Program Files\Git\bin\bash.exe", "--login -i -c \"%C; exec bash --login -i\"", command);
        }

        data.Add("cmd.exe", "/s /k %C", "dir");
        data.Add("cmd.exe", "/s /k %C", "echo a\\;b");
        return data;
    }

    [Theory]
    [MemberData(nameof(ShellsAndCommands))]
    public void ForWindowsTerminal_TheShellGetsTheLineItWouldGetDirectly(string program, string template, string command)
    {
        var launch = ShellCommandLine.Build(program, template, command, Folder);

        var line = WindowsTerminal.CommandlineOfNewTab(ShellCommandLine.ForWindowsTerminal(launch, Folder), out var folder);

        folder.Should().Be(Folder);
        var expectedProgram = launch.ReadsRawLine
            ? (program.Contains(' ') ? $"\"{program}\"" : program)
            : ShellQuoting.QuoteIfNeeded(program);
        line.Should().Be(expectedProgram + " " + ShellCommandLine.Arguments(launch));
    }

    [Fact]
    public void ForWindowsTerminal_ADriveRoot_DoesNotEscapeItsClosingQuote()
    {
        var launch = ShellCommandLine.Build("cmd.exe", "/s /k %C", "dir", @"D:\");

        ShellCommandLine.ForWindowsTerminal(launch, @"D:\").Should().StartWith("-d \"D:\\\\\" ");
        WindowsTerminal.CommandlineOfNewTab(ShellCommandLine.ForWindowsTerminal(launch, @"D:\"), out var folder);
        folder.Should().Be(@"D:\");
    }

    [Fact]
    public void ForWindowsTerminal_BashsEscapedSemicolon_ReachesBash()
    {
        var launch = ShellCommandLine.Build(
            "bash.exe", "-c \"%C; exec bash\"", "find . -exec rm {} \\;", Folder);

        var line = WindowsTerminal.CommandlineOfNewTab(ShellCommandLine.ForWindowsTerminal(launch, Folder), out _);

        ShellCommandLine.SplitArguments(line).Should().Equal("bash.exe", "-c", "find . -exec rm {} \\;; exec bash");
    }

    /// <summary>
    /// What Windows Terminal does with its command line (AppCommandlineArgs, Commandline::AddArg): argv,
    /// a new command at every <c>;</c> without a <c>\</c> in front, <c>\;</c> back to <c>;</c>, and the
    /// arguments joined again — quoted when they hold a space, inner quotes untouched.
    /// </summary>
    private static class WindowsTerminal
    {
        private static readonly Regex Delimiter = new(@"^;|[^\\];");

        public static string CommandlineOfNewTab(string arguments, out string folder)
        {
            var argv = ShellCommandLine.SplitArguments(arguments);
            argv[0].Should().Be("-d");
            folder = argv[1];

            var parts = new List<string>();
            foreach (var arg in argv.Skip(2))
            {
                Delimiter.IsMatch(arg).Should().BeFalse($"«{arg}» would be cut into two tabs");
                var unescaped = arg.Replace("\\;", ";");
                parts.Add(unescaped.Contains(' ') ? "\"" + unescaped + "\"" : unescaped);
            }

            return string.Join(' ', parts);
        }
    }
}
