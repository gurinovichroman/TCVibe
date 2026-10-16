using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Copy/Move between two different file systems — the path an FTP or SFTP panel takes.
/// The local disk stands in for one side and an in-memory VFS for the other.
/// </summary>
public class CrossFileSystemTransferTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tcv_xfer_{Guid.NewGuid():N}");
    private readonly WindowsFileSystem _local = new(NullLogger<WindowsFileSystem>.Instance);
    private readonly InMemoryFileSystem _remote = new("ftp");

    public CrossFileSystemTransferTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private async Task<IFileEntry> WriteLocalFileAsync(string name, string content)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
        return (await _local.GetEntryAsync(new VfsPath(path)))!;
    }

    // ── Copy ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_LocalToRemote_StreamsTheBytesOver()
    {
        var entry = await WriteLocalFileAsync("upload.txt", "hello remote");

        var op = new CopyOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        _remote.HasFile("/pub/upload.txt").Should().BeTrue();
        Encoding.UTF8.GetString(_remote.ReadFile("/pub/upload.txt")).Should().Be("hello remote");
    }

    [Fact]
    public async Task Copy_RemoteToLocal_WritesTheFileToDisk()
    {
        _remote.AddFile("/pub/download.txt", Encoding.UTF8.GetBytes("hello local"));
        var entry = (await _remote.GetEntryAsync(new VfsPath("/pub/download.txt")))!;

        var op = new CopyOperation(_remote, _local, [entry], new VfsPath(_root),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        var written = Path.Combine(_root, "download.txt");
        File.Exists(written).Should().BeTrue();
        (await File.ReadAllTextAsync(written)).Should().Be("hello local");
    }

    [Fact]
    public async Task Copy_AcrossFileSystems_DoesNotUseTheSameFsFastPath()
    {
        // CopyAsync is a same-VFS shortcut; across file systems the bytes must go
        // through OpenRead/OpenWrite or the destination never receives them
        var entry = await WriteLocalFileAsync("streamed.bin", new string('x', 5000));

        var op = new CopyOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        _remote.WritesOpened.Should().Be(1);
        _remote.ReadFile("/pub/streamed.bin").Should().HaveCount(5000);
    }

    [Fact]
    public async Task Copy_OverAServerFileThatBreaksOffMidway_LeavesTheOriginalIntact()
    {
        var source = new InMemoryFileSystem("mem");
        source.AddFile("/src/site.css", Encoding.UTF8.GetBytes("new content"));
        source.OnFirstRead = () => throw new IOException("connection dropped");
        _remote.AddFile("/pub/site.css", Encoding.UTF8.GetBytes("original"));
        var entry = (await source.GetEntryAsync(new VfsPath("/src/site.css")))!;

        var op = new CopyOperation(source, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        var act = () => op.ExecuteAsync(CancellationToken.None);
        await act.Should().ThrowAsync<IOException>();

        Encoding.UTF8.GetString(_remote.ReadFile("/pub/site.css")).Should().Be("original",
            "the new bytes go to a temporary sibling first; a server has no Recycle Bin");
        (await _remote.ListAsync(new VfsPath("/pub"))).Should().ContainSingle("the temporary copy is cleaned up");
    }

    [Fact]
    public async Task Copy_OverAServerFile_ReplacesItWithoutLeavingATemporaryCopy()
    {
        var entry = await WriteLocalFileAsync("site.css", "new content");
        _remote.AddFile("/pub/site.css", Encoding.UTF8.GetBytes("original"));

        var op = new CopyOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        Encoding.UTF8.GetString(_remote.ReadFile("/pub/site.css")).Should().Be("new content");
        (await _remote.ListAsync(new VfsPath("/pub"))).Should().ContainSingle();
    }

    [Fact]
    public async Task Copy_DirectoryToRemote_RecreatesTheTree()
    {
        await WriteLocalFileAsync(Path.Combine("tree", "a.txt"), "A");
        await WriteLocalFileAsync(Path.Combine("tree", "sub", "b.txt"), "B");
        var dir = (await _local.GetEntryAsync(new VfsPath(Path.Combine(_root, "tree"))))!;

        var op = new CopyOperation(_local, _remote, [dir], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        _remote.HasFile("/pub/tree/a.txt").Should().BeTrue();
        _remote.HasFile("/pub/tree/sub/b.txt").Should().BeTrue();
    }

    // ── Move ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Move_LocalToRemote_UploadsThenRemovesTheSource()
    {
        var entry = await WriteLocalFileAsync("moved.txt", "in transit");

        var op = new MoveOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        _remote.HasFile("/pub/moved.txt").Should().BeTrue();
        File.Exists(Path.Combine(_root, "moved.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_RemoteToLocal_DownloadsThenDeletesRemotely()
    {
        _remote.AddFile("/pub/gone.txt", Encoding.UTF8.GetBytes("bye"));
        var entry = (await _remote.GetEntryAsync(new VfsPath("/pub/gone.txt")))!;

        var op = new MoveOperation(_remote, _local, [entry], new VfsPath(_root),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        File.Exists(Path.Combine(_root, "gone.txt")).Should().BeTrue();
        _remote.HasFile("/pub/gone.txt").Should().BeFalse();
    }

    [Fact]
    public async Task Move_DirectoryAcrossFileSystems_TransfersThenRemovesTheTree()
    {
        _remote.AddFile("/pub/box/inner.txt", Encoding.UTF8.GetBytes("payload"));
        var dir = (await _remote.GetEntryAsync(new VfsPath("/pub/box")))!;

        var op = new MoveOperation(_remote, _local, [dir], new VfsPath(_root),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        File.Exists(Path.Combine(_root, "box", "inner.txt")).Should().BeTrue();
        _remote.HasFile("/pub/box/inner.txt").Should().BeFalse();
        _remote.HasDirectory("/pub/box").Should().BeFalse();
    }

    [Fact]
    public async Task Move_DirectoryAcrossFileSystems_RemovesTheSourceInOneRecursiveDelete()
    {
        _remote.AddFile("/pub/box/a.txt", Encoding.UTF8.GetBytes("A"));
        _remote.AddFile("/pub/box/sub/b.txt", Encoding.UTF8.GetBytes("B"));
        var dir = (await _remote.GetEntryAsync(new VfsPath("/pub/box")))!;

        var op = new MoveOperation(_remote, _local, [dir], new VfsPath(_root),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        _remote.HasDirectory("/pub/box").Should().BeFalse();
        _remote.DeleteCalls.Should().Be(1,
            "the whole subtree is gone in one server-side walk, not one round trip per child");
    }

    [Fact]
    public async Task Move_AcrossFileSystems_ReportsProgress()
    {
        var entry = await WriteLocalFileAsync("big.bin", new string('x', 5000));
        var progress = new RecordingProgress();

        var op = new MoveOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance)
        {
            Progress = progress,
        };
        await op.ExecuteAsync(CancellationToken.None);

        progress.Reports.Should().NotBeEmpty(
            "the move dialog shows a progress bar — a bare CopyTo would leave it at 0%");
        progress.Reports[^1].BytesCopied.Should().Be(5000);
    }

    // ── Conflict handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task Copy_RenameAuto_ProbesTheDestinationFileSystem()
    {
        // Both names are taken on the remote. A local-disk probe finds neither and hands back
        // "clash (1).txt", overwriting a file that is already there.
        _remote.AddFile("/pub/clash.txt", Encoding.UTF8.GetBytes("original"));
        _remote.AddFile("/pub/clash (1).txt", Encoding.UTF8.GetBytes("also original"));
        var entry = await WriteLocalFileAsync("clash.txt", "incoming");

        var op = new CopyOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new RenameAutoStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        Encoding.UTF8.GetString(_remote.ReadFile("/pub/clash (1).txt")).Should().Be("also original");
        Encoding.UTF8.GetString(_remote.ReadFile("/pub/clash (2).txt")).Should().Be("incoming");
    }

    [Fact]
    public async Task Copy_PreserveTimestamps_AcrossFileSystems_IsDroppedNotFailed()
    {
        var entry = await WriteLocalFileAsync("stamped.txt", "content");

        var op = new CopyOperation(_local, _remote, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), new CopyOptions(PreserveTimestamps: true),
            NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed,
            "a remote path cannot be stamped through the BCL — the option is skipped, " +
            "not applied to a fabricated local path after the bytes already landed");
        _remote.HasFile("/pub/stamped.txt").Should().BeTrue();
    }

    private sealed class RecordingProgress : IProgress<FileProgress>
    {
        public List<FileProgress> Reports { get; } = [];
        public void Report(FileProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Move_WithinOneFileSystem_StillUsesTheAtomicRename()
    {
        _remote.AddDirectory("/dest");
        _remote.AddFile("/pub/same.txt", Encoding.UTF8.GetBytes("stays local to the fs"));
        var entry = (await _remote.GetEntryAsync(new VfsPath("/pub/same.txt")))!;

        var op = new MoveOperation(_remote, _remote, [entry], new VfsPath("/dest"),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        _remote.HasFile("/dest/same.txt").Should().BeTrue();
        _remote.HasFile("/pub/same.txt").Should().BeFalse();
        _remote.WritesOpened.Should().Be(0, "a same-file-system move needs no byte streaming");
    }
}
