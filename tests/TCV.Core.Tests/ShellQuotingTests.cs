using FluentAssertions;
using TCV.Core.Domain;

namespace TCV.Core.Tests;

public class ShellQuotingTests
{
    private const string Quote = "\"";

    private static string Bs(int count) => new('\u005c', count);

    [Fact]
    public void QuoteArgument_PlainPath_IsWrappedWithoutTouchingInnerBackslashes()
    {
        ShellQuoting.QuoteArgument(@"C:\a\b").Should().Be(Quote + @"C:\a\b" + Quote);
    }

    [Fact]
    public void QuoteArgument_DriveRoot_DoublesTheTrailingBackslashSoItCannotEscapeTheQuote()
    {
        ShellQuoting.QuoteArgument(@"D:\").Should().Be(Quote + "D:" + Bs(2) + Quote);
    }

    [Fact]
    public void QuoteArgument_DirectoryWithTrailingBackslash_DoublesOnlyTheTrailingRun()
    {
        ShellQuoting.QuoteArgument(@"C:\dir\").Should().Be(Quote + @"C:\dir" + Bs(2) + Quote);
    }

    [Fact]
    public void QuoteArgument_InnerQuote_IsEscaped()
    {
        ShellQuoting.QuoteArgument("say \"hi\"").Should().Be(Quote + "say " + Bs(1) + Quote + "hi" + Bs(1) + Quote + Quote);
    }

    [Fact]
    public void QuoteArgument_BackslashesBeforeAQuote_AreDoubledPlusOne()
    {
        // one backslash followed by a quote: 2n+1 backslashes then the quote
        ShellQuoting.QuoteArgument("a" + Bs(1) + Quote + "b")
            .Should().Be(Quote + "a" + Bs(3) + Quote + "b" + Quote);
    }

    [Theory]
    [InlineData("plain.txt")]
    [InlineData(@"C:\src\app.cs")]
    public void QuoteIfNeeded_LeavesPlainValuesAlone(string value)
    {
        ShellQuoting.QuoteIfNeeded(value).Should().Be(value);
    }

    [Theory]
    [InlineData("a b.txt")]
    [InlineData("a&calc.txt")]
    [InlineData("a|b")]
    [InlineData("a^b")]
    [InlineData("a>b")]
    [InlineData("(x86)")]
    [InlineData("a,b")]
    [InlineData("50%")]
    [InlineData("hi!")]
    public void QuoteIfNeeded_QuotesEverythingCmdWouldInterpret(string value)
    {
        var quoted = ShellQuoting.QuoteIfNeeded(value);

        quoted.Should().StartWith(Quote).And.EndWith(Quote);
    }

    [Fact]
    public void QuoteIfNeeded_EmptyValue_BecomesAnEmptyArgument()
    {
        ShellQuoting.QuoteIfNeeded(string.Empty).Should().Be(Quote + Quote);
    }

    [Theory]
    [InlineData("100%.txt", false)]
    [InlineData("hello!.txt", false)]
    [InlineData("a\"b", false)]
    [InlineData("line\nbreak", false)]
    [InlineData("a&b.txt", true)]      // fine inside quotes; the quoting layer handles it
    [InlineData(@"C:\Program Files (x86)\x", true)]
    public void IsSafeForCmd_RefusesWhatCmdExpandsEvenInsideQuotes(string value, bool expected)
    {
        ShellQuoting.IsSafeForCmd(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\bin\code.cmd", true)]
    [InlineData(@"C:\bin\build.BAT", true)]
    [InlineData("code", true)]          // PATH may resolve it to code.cmd
    [InlineData("\"code\"", true)]
    [InlineData(@"C:\bin\tool.exe", false)]
    [InlineData("", false)]
    [InlineData("cmd.exe", true)]       // cmd.exe /k type %S parses its arguments itself
    [InlineData(@"C:\Windows\System32\CMD.EXE", true)]
    [InlineData(@"C:\bin\cmdtool.exe", false)]
    public void MayRunThroughCmd_DetectsBatchShims(string command, bool expected)
    {
        ShellQuoting.MayRunThroughCmd(command).Should().Be(expected);
    }
}

public class MacroTemplateTests
{
    private const string Quote = "\"";

    private static string Bs(int count) => new('\u005c', count);

    private static IReadOnlyList<string>? Resolve(char macro, params string[] files) => macro switch
    {
        'F' => files,
        'P' => [@"C:\my dir\"],
        _ => null,
    };

    [Fact]
    public void Expand_ValueContainingAMacro_IsNotExpandedAgain()
    {
        var result = MacroTemplate.Expand("%F %P", m => Resolve(m, @"C:\x\a%Fb.txt"));

        result.Should().Be(Quote + @"C:\x\a%Fb.txt" + Quote + " " + Quote + @"C:\my dir" + Bs(2) + Quote);
    }

    [Fact]
    public void Expand_InsideTemplateQuotes_EscapesWithoutWrapping()
    {
        // "%P" with a trailing-backslash value: the backslash must not swallow the closing quote
        var result = MacroTemplate.Expand(Quote + "%P" + Quote + " x", m => Resolve(m));

        result.Should().Be(Quote + @"C:\my dir" + Bs(2) + Quote + " x");
    }

    [Fact]
    public void Expand_FileWithAmpersand_IsQuoted()
    {
        var result = MacroTemplate.Expand("%F", m => Resolve(m, @"C:\x\a&calc.txt"));

        result.Should().Be(Quote + @"C:\x\a&calc.txt" + Quote);
    }

    [Fact]
    public void Expand_UnknownMacro_IsKeptAsWritten()
    {
        MacroTemplate.Expand("100%Z", m => Resolve(m)).Should().Be("100%Z");
    }

    [Fact]
    public void Expand_Validator_RejectsUnsafeValues()
    {
        var act = () => MacroTemplate.Expand(
            "%F", m => Resolve(m, @"C:\x\50%.txt"), validate: ShellQuoting.IsSafeForCmd);

        act.Should().Throw<UnsafeMacroValueException>().Which.Value.Should().Be(@"C:\x\50%.txt");
    }

    [Fact]
    public void Expand_EmptyMacroWithEatSpace_TakesTheBlankBeforeIt()
    {
        MacroTemplate.Expand("open %F", m => Resolve(m), eatSpaceBeforeEmpty: true).Should().Be("open");
    }

    [Fact]
    public void Expand_Raw_InsertsValuesUnquoted()
    {
        MacroTemplate.Expand("%P", m => Resolve(m), raw: true).Should().Be(@"C:\my dir\");
    }

    [Theory]
    [InlineData("echo %PATH%", "echo %PATH%")]
    [InlineData("%FOO%%F", "%FOO%x")]                         // closing '%' is not the start of a macro
    [InlineData("%ProgramFiles(x86)%\\t %F", "%ProgramFiles(x86)%\\t x")]
    [InlineData("%Pfoo", "%Pfoo")]                            // a macro is one letter standing alone
    [InlineData("%F.bak", "x.bak")]
    public void Expand_EnvironmentVariables_AreLeftForTheShell(string template, string expected)
    {
        MacroTemplate.Expand(template, m => Resolve(m, "x"), raw: true).Should().Be(expected);
    }
}
