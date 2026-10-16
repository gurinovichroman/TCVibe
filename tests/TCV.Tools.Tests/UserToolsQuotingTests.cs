using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.Tools.Tests;

/// <summary>Argument safety of user tools: a file name is data, never a second command.</summary>
public class UserToolsQuotingTests
{
    private static IFileEntry File(string path)
    {
        var entry = new Mock<IFileEntry>();
        entry.SetupGet(e => e.Path).Returns(new VfsPath(path));
        entry.SetupGet(e => e.Name).Returns(path[(path.LastIndexOf('/') + 1)..]);
        return entry.Object;
    }

    private static IToolContext Context(string active, params IFileEntry[] selected)
    {
        var ctx = new Mock<IToolContext>();
        ctx.SetupGet(c => c.SelectedFiles).Returns(selected);
        ctx.SetupGet(c => c.ActiveFileSystem).Returns(TestFileSystems.Of("file"));
        ctx.SetupGet(c => c.ActivePath).Returns(new VfsPath(active));
        ctx.SetupGet(c => c.OppositeFileSystem).Returns(TestFileSystems.Of("file"));
        ctx.SetupGet(c => c.OppositePath).Returns(new VfsPath(@"D:\opp"));
        return ctx.Object;
    }

    [Fact]
    public void Expand_FileNameWithAmpersand_IsQuotedSoCmdCannotSplitIt()
    {
        var ctx = Context(@"D:\repo", File("D:/repo/a&calc.txt"));

        UserToolsMacroExpander.Expand("%S", ctx).Should().Be("""
            "D:\repo\a&calc.txt"
            """);
    }

    [Fact]
    public void Expand_DriveRootInsideTemplateQuotes_KeepsTheClosingQuote()
    {
        var ctx = Context(@"C:\");

        UserToolsMacroExpander.Expand("\"%P\" --flag", ctx).Should().Be("""
            "C:\\" --flag
            """);
    }

    [Fact]
    public void Expand_FileNameHoldingAMacro_IsNotExpandedTwice()
    {
        // a chained Replace expanded the name first, so "%S" inside it was replaced again
        var ctx = Context(@"D:\repo", File("D:/repo/x%Sy.txt"), File("D:/repo/b.txt"));

        UserToolsMacroExpander.Expand("%N|%S", ctx).Should().Be("""
            "x%Sy"|"D:\repo\x%Sy.txt" D:\repo\b.txt
            """);
    }

    [Fact]
    public void Expand_ForCmd_RefusesPercentInAPath()
    {
        var ctx = Context(@"D:\repo", File("D:/repo/50%.txt"));

        var act = () => UserToolsMacroExpander.Expand("%S", ctx, forCmd: true);

        act.Should().Throw<UnsafeMacroValueException>();
    }

    [Fact]
    public void Expand_NotForCmd_AllowsPercentInAPath()
    {
        var ctx = Context(@"D:\repo", File("D:/repo/50%.txt"));

        UserToolsMacroExpander.Expand("%S", ctx, forCmd: false).Should().Be("""
            "D:\repo\50%.txt"
            """);
    }

    [Fact]
    public void ExpandRaw_LeavesTheProgramPathUnquoted()
    {
        var ctx = Context(@"D:\my repo");

        UserToolsMacroExpander.ExpandRaw("%P", ctx).Should().Be(@"D:\my repo");
    }
}
