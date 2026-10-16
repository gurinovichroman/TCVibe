using System.Windows.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Commands;
using TCV.App.Settings;
using TCV.App.ViewModels;

namespace TCV.App.Tests;

/// <summary>
/// The path bar (6.52): at the root of an archive or a server it names the mount instead of being
/// empty, and it can be edited from the keyboard (EditPath, Ctrl+L) as well as by a click.
/// </summary>
public class PathBarTests
{
    // ── The mount's root segment ──────────────────────────────────────────────

    [Fact]
    public void BuildBreadcrumbs_AtTheRootOfAMount_ShowsTheMountsName()
    {
        var segments = FileListViewModel.BuildBreadcrumbs("/", mounted: true, mountRootLabel: "dist.zip");

        segments.Should().ContainSingle();
        segments[0].DisplayName.Should().Be("dist.zip");
        segments[0].Path.Should().Be("/", "clicking the name goes back to the mount's root");
    }

    [Fact]
    public void BuildBreadcrumbs_InsideAMount_StartsWithTheMountsName()
    {
        var segments = FileListViewModel.BuildBreadcrumbs("/logs/2026", mounted: true, mountRootLabel: "sftp://me@host");

        segments.Select(s => s.DisplayName).Should().Equal("sftp://me@host", "logs", "2026");
        segments.Select(s => s.Path).Should().Equal("/", "/logs", "/logs/2026");
    }

    [Theory]
    [InlineData(MountKind.Archive, "zip", "dist.zip", "dist.zip")]
    [InlineData(MountKind.Remote, "sftp", "sftp://me@host", "sftp://me@host")]
    [InlineData(MountKind.Remote, "ftp", "me@host", "ftp://me@host")]          // FTP names itself without a scheme
    [InlineData(MountKind.Remote, "davs", "host:8443", "davs://host:8443")]
    [InlineData(MountKind.Other, "search", "", "/")]
    public void DescribeMountRoot_NamesTheMountAsTheRestOfTheProgramDoes(
        MountKind kind, string scheme, string displayName, string expected)
    {
        FileListViewModel.DescribeMountRoot(kind, scheme, displayName).Should().Be(expected);
    }

    [Fact]
    public void BreadcrumbSegments_OfAMountedPanelAtItsRoot_AreNotEmpty()
    {
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        list.Mount(new InMemoryFileSystem("zip"), "/", kind: MountKind.Archive);

        list.BreadcrumbSegments.Should().ContainSingle()
            .Which.DisplayName.Should().Be("zip://test");
    }

    // ── Editing from the keyboard ─────────────────────────────────────────────

    [Fact]
    public void BeginEditPath_OpensTheBoxWithTheCurrentPath()
    {
        var list = new FileListViewModel(new InMemoryFileSystem("file"), NullLogger<FileListViewModel>.Instance);
        var archive = new InMemoryFileSystem("zip");
        archive.AddDirectory("/docs");
        list.Mount(archive, "/docs", kind: MountKind.Archive);

        list.BeginEditPath();

        list.IsEditingPath.Should().BeTrue();
        list.EditablePath.Should().Be("/docs", "a path typed inside a mount is looked up in the mount");
    }

    [Fact]
    public void EditPath_IsCtrlL_AndAPaletteCommand()
    {
        var hotkeys = new HotkeyManager(new AppSettings());

        hotkeys.GetCommandId(Key.L, ModifierKeys.Control).Should().Be("EditPath");
        CommandCatalog.All.Should().Contain(c => c.Id == "EditPath");
    }
}
