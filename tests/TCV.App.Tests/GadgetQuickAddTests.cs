using FluentAssertions;
using TCV.App.Settings;

namespace TCV.App.Tests;

/// <summary>
/// The one-line gadget description (6.50): a program path with spaces used to be cut at the first
/// space, so <c>C:\Program Files\app.exe</c> became the command <c>C:\Program</c>.
/// </summary>
public class GadgetQuickAddTests
{
    private static Func<string, bool> Existing(params string[] files)
    {
        var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        return set.Contains;
    }

    private static readonly Func<string, bool> Nothing = _ => false;

    [Fact]
    public void Parse_BareCommand_SplitsAtTheFirstSpace()
    {
        var gadget = GadgetQuickAdd.Parse("build = dotnet build -c Release", Nothing);

        gadget.Should().NotBeNull();
        gadget!.Name.Should().Be("build");
        gadget.Command.Should().Be("dotnet");
        gadget.Arguments.Should().Be("build -c Release");
        gadget.InTerminal.Should().BeTrue();
    }

    [Fact]
    public void Parse_QuotedPath_KeepsItWhole_AndStoresItWithoutQuotes()
    {
        var gadget = GadgetQuickAdd.Parse(@"edit = ""C:\Program Files\Notepad++\notepad++.exe"" %F", Nothing);

        gadget!.Command.Should().Be(@"C:\Program Files\Notepad++\notepad++.exe");
        gadget.Arguments.Should().Be("%F");
    }

    [Fact]
    public void Parse_UnquotedPathThatExists_IsTakenWhole()
    {
        var exists = Existing(@"C:\Program Files\Notepad++\notepad++.exe");

        var gadget = GadgetQuickAdd.Parse(@"edit = C:\Program Files\Notepad++\notepad++.exe -multiInst %F", exists);

        gadget!.Command.Should().Be(@"C:\Program Files\Notepad++\notepad++.exe");
        gadget.Arguments.Should().Be("-multiInst %F");
    }

    [Fact]
    public void Parse_UnquotedPathWithoutArguments_IsTheWholeCommand()
    {
        var exists = Existing(@"C:\Program Files\Tool\tool.exe");

        var gadget = GadgetQuickAdd.Parse(@"t = C:\Program Files\Tool\tool.exe", exists);

        gadget!.Command.Should().Be(@"C:\Program Files\Tool\tool.exe");
        gadget.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_UnquotedPathFoundOnlyWithAnExtension_KeepsWhatWasTyped()
    {
        var exists = Existing(@"C:\Program Files\nodejs\npm.cmd");

        var gadget = GadgetQuickAdd.Parse(@"ci = C:\Program Files\nodejs\npm ci", exists, [".exe", ".cmd"]);

        gadget!.Command.Should().Be(@"C:\Program Files\nodejs\npm");
        gadget.Arguments.Should().Be("ci");
    }

    [Fact]
    public void Parse_LongestExistingPrefixWins()
    {
        // The classic trap: a stray C:\Program.exe must not capture "C:\Program Files\…".
        var exists = Existing(@"C:\Program.exe", @"C:\Program Files\App\app.exe");

        var (command, arguments) = GadgetQuickAdd.SplitCommand(@"C:\Program Files\App\app.exe --x", exists);

        command.Should().Be(@"C:\Program Files\App\app.exe");
        arguments.Should().Be("--x");
    }

    [Fact]
    public void Parse_AnArgumentThatIsAPathWithSpaces_StaysAnArgument()
    {
        var exists = Existing(@"D:\My Tools\run.exe");

        var (command, arguments) = GadgetQuickAdd.SplitCommand(@"D:\My Tools\run.exe D:\data file.txt", exists);

        command.Should().Be(@"D:\My Tools\run.exe");
        arguments.Should().Be(@"D:\data file.txt");
    }

    [Fact]
    public void Parse_UnquotedPathThatDoesNotExist_FallsBackToTheFirstSpace()
    {
        var (command, arguments) = GadgetQuickAdd.SplitCommand(@"C:\Missing Dir\x.exe a", Nothing);

        command.Should().Be(@"C:\Missing");
        arguments.Should().Be(@"Dir\x.exe a");
    }

    [Fact]
    public void Parse_BareName_NeverAsksTheDisk()
    {
        var asked = 0;

        GadgetQuickAdd.SplitCommand("dotnet test --no-build", _ => { asked++; return true; });

        asked.Should().Be(0);
    }

    [Fact]
    public void Parse_UnclosedQuote_TakesTheRestAsTheProgram()
    {
        var (command, arguments) = GadgetQuickAdd.SplitCommand(@"""C:\Program Files\app.exe", Nothing);

        command.Should().Be(@"C:\Program Files\app.exe");
        arguments.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EqualsSignInTheArguments_BelongsToTheArguments()
    {
        var gadget = GadgetQuickAdd.Parse("fmt = dotnet format --severity=warn", Nothing);

        gadget!.Name.Should().Be("fmt");
        gadget.Command.Should().Be("dotnet");
        gadget.Arguments.Should().Be("format --severity=warn");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("dotnet build")]
    [InlineData("= dotnet build")]
    [InlineData("build =   ")]
    [InlineData(@"build = """"")]
    public void Parse_NotNameEqualsCommand_IsNull(string line)
    {
        GadgetQuickAdd.Parse(line, Nothing).Should().BeNull();
    }
}
