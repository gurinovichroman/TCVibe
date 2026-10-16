using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.Tools.Tests;

/// <summary>
/// The macro contract user tools are launched through (task 6.29). Until then nothing in the
/// application implemented <see cref="IToolContext"/>, so nothing exercised these expansions.
/// </summary>
public class UserToolsMacroExpanderTests
{
    private static IFileEntry File(string path, string name)
    {
        var entry = new Mock<IFileEntry>();
        entry.SetupGet(e => e.Path).Returns(new VfsPath(path));
        entry.SetupGet(e => e.Name).Returns(name);
        entry.SetupGet(e => e.IsDirectory).Returns(false);
        return entry.Object;
    }

    private static IToolContext Context(params IFileEntry[] selected)
    {
        var ctx = new Mock<IToolContext>();
        ctx.SetupGet(c => c.SelectedFiles).Returns(selected);
        ctx.SetupGet(c => c.ActiveFileSystem).Returns(TestFileSystems.Of("file"));
        ctx.SetupGet(c => c.ActivePath).Returns(new VfsPath(@"D:\repo\src"));
        ctx.SetupGet(c => c.OppositeFileSystem).Returns(TestFileSystems.Of("file"));
        ctx.SetupGet(c => c.OppositePath).Returns(new VfsPath(@"D:\repo\docs"));
        return ctx.Object;
    }

    [Fact]
    public void Panel_paths_expand_with_windows_separators()
    {
        // VfsPath normalises to forward slashes; a process started with those would be handed a
        // path Windows tools do not recognise as the same folder.
        UserToolsMacroExpander.Expand("%P", Context()).Should().Be(@"D:\repo\src");
        UserToolsMacroExpander.Expand("%T", Context()).Should().Be(@"D:\repo\docs");
    }

    [Fact]
    public void Focused_file_expands_to_its_name_and_extension()
    {
        var ctx = Context(File(@"D:/repo/src/Program.cs", "Program.cs"));

        UserToolsMacroExpander.Expand("%N", ctx).Should().Be("Program");
        UserToolsMacroExpander.Expand("%E", ctx).Should().Be("cs");
    }

    [Fact]
    public void Selection_expands_to_space_separated_paths_and_quotes_the_ones_with_spaces()
    {
        var ctx = Context(
            File(@"D:/repo/src/Program.cs", "Program.cs"),
            File(@"D:/repo/src/My Notes.md", "My Notes.md"));

        UserToolsMacroExpander.Expand("%S", ctx)
            .Should().Be(@"D:\repo\src\Program.cs ""D:\repo\src\My Notes.md""");
    }

    [Fact]
    public void With_nothing_selected_the_file_macros_expand_to_nothing()
    {
        // A tool bound to a folder («open a terminal here») is launched with no selection; it must
        // get an empty argument rather than the literal "%N".
        var ctx = Context();

        UserToolsMacroExpander.Expand("%N %E %S", ctx).Should().Be("  ");
    }

    [Fact]
    public void A_template_without_macros_is_returned_unchanged()
    {
        UserToolsMacroExpander.Expand("--verbose", Context()).Should().Be("--verbose");
        UserToolsMacroExpander.Expand(string.Empty, Context()).Should().BeEmpty();
    }
}
