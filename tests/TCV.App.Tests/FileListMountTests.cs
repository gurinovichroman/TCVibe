using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.ViewModels;

namespace TCV.App.Tests;

/// <summary>
/// Mounting an archive or a remote file system into a panel, and getting back out of it.
/// </summary>
public class FileListMountTests
{
    private static FileListViewModel CreateList(out InMemoryFileSystem local)
    {
        local = new InMemoryFileSystem("file");
        local.AddDirectory("/C:/work");
        return new FileListViewModel(local, NullLogger<FileListViewModel>.Instance);
    }

    [Fact]
    public void FreshPanel_IsNotMounted()
    {
        var list = CreateList(out _);

        list.IsMounted.Should().BeFalse();
        list.IsRemote.Should().BeFalse();
        list.IsInsideArchive.Should().BeFalse();
    }

    [Fact]
    public void Mount_SwitchesTheActiveFileSystemAndPath()
    {
        var list = CreateList(out _);
        list.CurrentPath = "/C:/work";

        var remote = new InMemoryFileSystem("ftp");
        list.Mount(remote, "/pub");

        list.IsMounted.Should().BeTrue();
        list.ActiveVfs.Should().BeSameAs(remote);
        list.CurrentPath.Should().Be("/pub");
    }

    [Fact]
    public void MountedNonArchive_CountsAsRemoteNotArchive()
    {
        var list = CreateList(out _);
        list.Mount(new InMemoryFileSystem("sftp"));

        list.IsRemote.Should().BeTrue();
        list.IsInsideArchive.Should().BeFalse(
            "IsInsideArchive must not fire for remote hosts — archive-only code paths key off it");
    }

    [Fact]
    public void Mount_WithoutRootPath_LandsAtTheRoot()
    {
        var list = CreateList(out _);
        list.Mount(new InMemoryFileSystem("ftp"));

        list.CurrentPath.Should().Be("/");
    }

    [Fact]
    public void Unmount_RestoresThePreviousFileSystemAndPath()
    {
        var list = CreateList(out var local);
        list.CurrentPath = "/C:/work";

        list.Mount(new InMemoryFileSystem("ftp"), "/pub");
        list.Unmount();

        list.IsMounted.Should().BeFalse();
        list.ActiveVfs.Should().BeSameAs(local);
        list.CurrentPath.Should().Be("/C:/work");
    }

    [Fact]
    public void Unmount_DisposesTheMountedFileSystem()
    {
        var list = CreateList(out _);
        var remote = new InMemoryFileSystem("ftp");

        list.Mount(remote, "/pub");
        list.Unmount();

        remote.IsDisposed.Should().BeTrue("a dropped connection must release its socket");
    }

    [Fact]
    public void Unmount_WithNothingMounted_IsANoOp()
    {
        var list = CreateList(out var local);
        list.CurrentPath = "/C:/work";

        list.Unmount();

        list.ActiveVfs.Should().BeSameAs(local);
        list.CurrentPath.Should().Be("/C:/work");
    }

    [Fact]
    public void NestedMounts_UnwindOneLevelAtATime()
    {
        var list = CreateList(out var local);
        list.CurrentPath = "/C:/work";

        var remote = new InMemoryFileSystem("ftp");
        var archive = new InMemoryFileSystem("zip");
        list.Mount(remote, "/pub");
        list.Mount(archive, "/");

        list.Unmount();
        list.ActiveVfs.Should().BeSameAs(remote);
        list.CurrentPath.Should().Be("/pub");

        list.Unmount();
        list.ActiveVfs.Should().BeSameAs(local);
        list.CurrentPath.Should().Be("/C:/work");
    }

    [Fact]
    public void RootLocalPath_ReportsTheDiskPathUnderneathTheMounts()
    {
        var list = CreateList(out _);
        list.CurrentPath = "/C:/work";

        list.Mount(new InMemoryFileSystem("ftp"), "/pub");
        list.Mount(new InMemoryFileSystem("zip"), "/inner");

        // What gets persisted to settings.json — "/inner" would not resolve next launch
        list.RootLocalPath.Should().Be("/C:/work");
    }

    [Fact]
    public void RootLocalPath_EqualsCurrentPathWhenNothingIsMounted()
    {
        var list = CreateList(out _);
        list.CurrentPath = "/C:/work";

        list.RootLocalPath.Should().Be("/C:/work");
    }

    [Fact]
    public void ArchiveOverRemote_RestoresTheConnectionWhenTheArchiveIsPopped()
    {
        var list = CreateList(out _);
        var connection = new TCV.Network.ConnectionInfo { Name = "srv", Host = "example.org" };

        list.Mount(new InMemoryFileSystem("ftp"), "/pub", connection, MountKind.Remote);
        list.Mount(new InMemoryFileSystem("zip"), "/", kind: MountKind.Archive);

        list.RemoteConnection.Should().BeNull("an archive mount carries no connection of its own");

        list.Unmount();

        list.IsRemote.Should().BeTrue();
        list.RemoteConnection.Should().BeSameAs(connection,
            "backing out of the archive returns to the FTP mount the queue needs");
    }

    [Fact]
    public void UnknownScheme_IsNeitherArchiveNorRemote()
    {
        var list = CreateList(out _);
        list.Mount(new InMemoryFileSystem("reg"));

        list.IsMounted.Should().BeTrue();
        list.IsRemote.Should().BeFalse(
            "an unrecognised file system must not be mistaken for an FTP server");
        list.IsInsideArchive.Should().BeFalse();
    }

    [Fact]
    public void Unmount_WhileLeased_DefersTheDisposeUntilTheOperationLetsGo()
    {
        var list = CreateList(out _);
        var remote = new InMemoryFileSystem("ftp");
        list.Mount(remote, "/pub");

        var lease = list.LeaseActiveVfs();
        list.Unmount();

        list.IsMounted.Should().BeFalse("the panel itself comes back immediately");
        remote.IsDisposed.Should().BeFalse("a running copy is still streaming through it");

        lease.Dispose();
        remote.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Unmount_LeaseOnAnEarlierMount_DoesNotPinALaterOne()
    {
        var list = CreateList(out _);
        var edited = new InMemoryFileSystem("sftp");
        list.Mount(edited, "/");

        // A remote-edit session holds this for as long as the app runs.
        var sessionLease = list.LeaseActiveVfs();
        list.Unmount();

        var later = new InMemoryFileSystem("ftp");
        list.Mount(later, "/pub");
        list.Unmount();

        later.IsDisposed.Should().BeTrue("nothing streams from the later connection, so disconnecting closes it");
        edited.IsDisposed.Should().BeFalse("the session still uploads through its own connection");

        sessionLease.Dispose();
        edited.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Unmount_LeaseTakenInsideAnArchiveOnAServer_KeepsTheServerToo()
    {
        var list = CreateList(out _);
        var remote = new InMemoryFileSystem("ftp");
        var archive = new InMemoryFileSystem("zip");
        list.Mount(remote, "/pub");
        list.Mount(archive, "/", kind: MountKind.Archive);

        var lease = list.LeaseActiveVfs();
        list.UnmountAll();

        remote.IsDisposed.Should().BeFalse("the archive being read lives on that server");
        archive.IsDisposed.Should().BeFalse();

        lease.Dispose();
        remote.IsDisposed.Should().BeTrue();
        archive.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void LeaveConnection_InsideAnArchiveOnAServer_ClosesBothAndReturnsToTheDisk()
    {
        var list = CreateList(out _);
        list.CurrentPath = "/C:/work";
        var server = new TCV.Network.ConnectionInfo { Protocol = TCV.Network.NetworkProtocol.Sftp, Host = "h" };
        var remote = new InMemoryFileSystem("sftp");
        list.Mount(remote, "/pub", server, MountKind.Remote);
        var archive = new InMemoryFileSystem("zip");
        list.Mount(archive, "/", kind: MountKind.Archive);

        list.ActiveConnection.Should().BeSameAs(server, "the archive is read through the server");
        list.ActiveConnectionPath.Should().Be("/pub");
        list.LeaveConnection().Should().BeTrue();

        list.IsMounted.Should().BeFalse("Disconnect used to pop only the archive and leave the server open");
        list.CurrentPath.Should().Be("/C:/work");
        archive.IsDisposed.Should().BeTrue();
        remote.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void LeaveConnection_OnTheDisk_DoesNothing()
    {
        var list = CreateList(out _);
        list.CurrentPath = "/C:/work";
        list.Mount(new InMemoryFileSystem("zip"), "/", kind: MountKind.Archive);

        list.LeaveConnection().Should().BeFalse();
        list.IsInsideArchive.Should().BeTrue("an archive on the disk is no connection");
    }

    [Fact]
    public void ParkMounts_ThenRestore_BringsBackTheWholeStack()
    {
        var list = CreateList(out _);
        list.CurrentPath = "/C:/work";
        var server = new TCV.Network.ConnectionInfo { Protocol = TCV.Network.NetworkProtocol.Ftp, Host = "h" };
        var remote = new InMemoryFileSystem("ftp");
        remote.AddDirectory("/pub");
        list.Mount(remote, "/pub", server, MountKind.Remote);
        var archive = new InMemoryFileSystem("zip");
        archive.AddDirectory("/docs");
        list.Mount(archive, "/docs", kind: MountKind.Archive);

        var parked = list.ParkMounts();
        list.IsMounted.Should().BeFalse();
        parked!.RootLocalPath.Should().Be("/C:/work");
        parked.Connection.Should().BeSameAs(server);
        list.CurrentPath = "/C:/work";

        list.RestoreMounts(parked);

        list.ActiveVfs.Should().BeSameAs(archive);
        list.IsInsideArchive.Should().BeTrue();
        list.CurrentPath.Should().Be("/docs");
        list.RemoteConnection.Should().BeNull("the top is still the archive, not the server");
        list.Unmount();
        list.ActiveVfs.Should().BeSameAs(remote);
        list.RemoteConnection.Should().BeSameAs(server);
        list.Unmount();
        list.CurrentPath.Should().Be("/C:/work");
    }

    [Fact]
    public void ParkMounts_OnTheDisk_ParksNothing()
    {
        var list = CreateList(out _);

        list.ParkMounts().Should().BeNull();
    }

    [Fact]
    public void DisposeParked_ClosesEveryMountOfTheStack()
    {
        var list = CreateList(out _);
        var remote = new InMemoryFileSystem("ftp");
        list.Mount(remote, "/", new TCV.Network.ConnectionInfo(), MountKind.Remote);
        var archive = new InMemoryFileSystem("zip");
        list.Mount(archive, "/", kind: MountKind.Archive);
        var parked = list.ParkMounts();

        list.DisposeParked(parked);

        remote.IsDisposed.Should().BeTrue();
        archive.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public void NavigateToArchive_IsMountAtTheRoot()
    {
        var list = CreateList(out _);
        var archive = new InMemoryFileSystem("zip");

        list.NavigateToArchive(archive);

        list.IsMounted.Should().BeTrue();
        list.ActiveVfs.Should().BeSameAs(archive);
        list.CurrentPath.Should().Be("/");
    }
}
