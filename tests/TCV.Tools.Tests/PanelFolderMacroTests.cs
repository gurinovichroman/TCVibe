using FluentAssertions;
using Moq;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.Tools.Tests;

/// <summary>A file system that is only its scheme — all the macros look at.</summary>
internal static class TestFileSystems
{
    public static IVirtualFileSystem Of(string scheme)
    {
        var fs = new Mock<IVirtualFileSystem>();
        fs.SetupGet(f => f.Scheme).Returns(scheme);
        return fs.Object;
    }
}

/// <summary>
/// %P and %T name a panel's folder, and only a panel on the disk has one (ROADMAP 6.55): an
/// archive's or a server's "/pub" used to reach the tool as "\pub", a folder on the current drive.
/// </summary>
public class PanelFolderMacroTests
{
    private static IToolContext Context(string activeScheme, string activePath, string oppositeScheme, string oppositePath)
    {
        var ctx = new Mock<IToolContext>();
        ctx.SetupGet(c => c.SelectedFiles).Returns([]);
        ctx.SetupGet(c => c.ActiveFileSystem).Returns(TestFileSystems.Of(activeScheme));
        ctx.SetupGet(c => c.ActivePath).Returns(new VfsPath(activePath));
        ctx.SetupGet(c => c.OppositeFileSystem).Returns(TestFileSystems.Of(oppositeScheme));
        ctx.SetupGet(c => c.OppositePath).Returns(new VfsPath(oppositePath));
        return ctx.Object;
    }

    [Theory]
    [InlineData("sftp", "/pub")]
    [InlineData("zip", "/docs")]
    [InlineData("search", "/")]
    public void Expand_TargetPanelNotOnDisk_RefusesTheTargetMacro(string scheme, string path)
    {
        var ctx = Context("file", @"D:\repo", scheme, path);

        var act = () => UserToolsMacroExpander.Expand("copy %S %T", ctx);

        act.Should().Throw<PanelFolderUnavailableException>().Which.Macro.Should().Be('T');
    }

    [Fact]
    public void Expand_TargetPanelOnDisk_GivesItsWindowsPath()
    {
        var ctx = Context("file", @"D:\repo", "file", "D:/other/dir");

        UserToolsMacroExpander.Expand("%T", ctx).Should().Be(@"D:\other\dir");
    }

    [Fact]
    public void Expand_TemplateWithoutTheTargetMacro_RunsBesideAMountedPanel()
    {
        // Only a macro that is used is refused: an open archive on the other side must not stop a
        // tool that never asks for it.
        var ctx = Context("file", @"D:\repo", "ftp", "/pub");

        UserToolsMacroExpander.Expand("build %P", ctx).Should().Be(@"build D:\repo");
    }

    [Fact]
    public void Expand_ActivePanelNotOnDisk_RefusesTheActiveMacro()
    {
        var ctx = Context("zip", "/inner", "file", @"D:\other");

        var act = () => UserToolsMacroExpander.ExpandRaw("%P", ctx);

        act.Should().Throw<PanelFolderUnavailableException>().Which.Macro.Should().Be('P');
    }

    [Theory]
    [InlineData("file", "D:/repo/src", @"D:\repo\src")]
    [InlineData("FILE", "D:/repo", @"D:\repo")]
    [InlineData("ftp", "/pub", null)]
    [InlineData("search", "/", null)]
    public void DiskFolderOf_IsAWindowsPathOnlyOnTheDisk(string scheme, string path, string? expected)
    {
        UserToolsMacroExpander.DiskFolderOf(TestFileSystems.Of(scheme), new VfsPath(path)).Should().Be(expected);
    }

    [Fact]
    public void DiskFolderOf_NoFileSystem_IsNoFolder()
    {
        UserToolsMacroExpander.DiskFolderOf(null, new VfsPath(@"D:\repo")).Should().BeNull();
    }
}
