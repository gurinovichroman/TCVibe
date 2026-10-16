using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The parts of the file operations that only mean something on a real disk: attributes, locked
/// files, partial files after a cancel, and delete. Each test works in its own temp folder.
/// </summary>
public sealed class FileOperationDiskTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-ops-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _fs = new(NullLogger<WindowsFileSystem>.Instance);

    public FileOperationDiskTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { /* a leaked handle in a failed test; the OS cleans the temp folder later */ }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private async Task<IFileEntry> EntryAsync(string windowsPath) =>
        (await _fs.GetEntryAsync(new VfsPath(windowsPath)))!;

    private CopyOperation NewCopy(IFileEntry source, string destDir, IConflictStrategy? strategy = null) =>
        new(_fs, _fs, [source], new VfsPath(destDir), strategy ?? new OverwriteStrategy(),
            new CopyOptions(), NullLogger<CopyOperation>.Instance);

    // ── Copy keeps what Explorer keeps ────────────────────────────────────────

    [Fact]
    public async Task Copy_ByDefault_KeepsTheModificationTimeAndTheReadOnlyFlag()
    {
        var source = P("src.txt");
        File.WriteAllText(source, "hello");
        var stamp = new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Local);
        File.SetLastWriteTime(source, stamp);
        File.SetAttributes(source, FileAttributes.ReadOnly);
        Directory.CreateDirectory(P("out"));

        await NewCopy(await EntryAsync(source), P("out")).ExecuteAsync(CancellationToken.None);

        var copy = new FileInfo(P("out", "src.txt"));
        copy.LastWriteTime.Should().BeCloseTo(stamp, TimeSpan.FromSeconds(2));
        copy.Attributes.HasFlag(FileAttributes.ReadOnly).Should().BeTrue();
    }

    [Fact]
    public async Task Copy_OverANewerReadOnlyFile_ReplacesItInsteadOfFailing()
    {
        var source = P("src.txt");
        File.WriteAllText(source, "new content");
        Directory.CreateDirectory(P("out"));
        var target = P("out", "src.txt");
        File.WriteAllText(target, "old");
        File.SetAttributes(target, FileAttributes.ReadOnly);

        var op = NewCopy(await EntryAsync(source), P("out"));
        await op.ExecuteAsync(CancellationToken.None);

        File.ReadAllText(target).Should().Be("new content");
        op.State.Should().Be(OperationState.Completed);
    }

    // ── A failed overwrite must not destroy the old file ──────────────────────

    [Fact]
    public async Task Copy_WhenTheSwapCannotHappen_KeepsTheOldFileAndLeavesNoTemporaryFile()
    {
        var source = P("src.txt");
        File.WriteAllText(source, "new content");
        Directory.CreateDirectory(P("out"));
        var target = P("out", "src.txt");
        File.WriteAllText(target, "old content");

        // Someone has the destination open: the new content can be written beside it, but not swapped in.
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var op = NewCopy(await EntryAsync(source), P("out"));
            var error = await Record.ExceptionAsync(() => op.ExecuteAsync(CancellationToken.None));
            // Windows reports a swap onto a file held open as a sharing violation or as access denied.
            (error is IOException or UnauthorizedAccessException).Should().BeTrue($"got {error}");
            op.State.Should().Be(OperationState.Failed);
        }

        File.ReadAllText(target).Should().Be("old content");
        Directory.GetFiles(P("out"), "*.tcvtmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Copy_CancelledWhileStreaming_RemovesThePartialNewFile()
    {
        // The source stands in for a remote file: it is read in chunks, and the user cancels after the first.
        var source = new InMemoryFileSystem();
        source.AddFile("/big.bin", new byte[3 * 1024 * 1024]);
        var entry = (await source.GetEntryAsync(new VfsPath("/big.bin")))!;
        Directory.CreateDirectory(P("out"));
        var target = P("out", "big.bin");

        using var cts = new CancellationTokenSource();
        source.OnFirstRead = () => cts.Cancel();

        var op = new CopyOperation(source, _fs, [entry], new VfsPath(P("out")), new OverwriteStrategy(),
            new CopyOptions(), NullLogger<CopyOperation>.Instance);
        await op.Invoking(o => o.ExecuteAsync(cts.Token)).Should().ThrowAsync<OperationCanceledException>();

        File.Exists(target).Should().BeFalse("a half-written file looks like the real thing and must not be left");
        op.State.Should().Be(OperationState.Cancelled);
    }

    // ── Error handling per item ───────────────────────────────────────────────

    [Fact]
    public async Task Copy_LockedSource_IsSkippedAndTheRestStillCopies()
    {
        File.WriteAllText(P("a.txt"), "a");
        File.WriteAllText(P("b.txt"), "b");
        Directory.CreateDirectory(P("out"));
        var a = await EntryAsync(P("a.txt"));
        var b = await EntryAsync(P("b.txt"));

        var asked = new List<string>();
        using (new FileStream(P("a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var op = new CopyOperation(_fs, _fs, [a, b], new VfsPath(P("out")), new OverwriteStrategy(),
                new CopyOptions(), NullLogger<CopyOperation>.Instance)
            {
                ErrorHandler = (error, _) =>
                {
                    asked.Add(error.Path);
                    return Task.FromResult(OperationErrorResolution.Skip);
                },
            };
            await op.ExecuteAsync(CancellationToken.None);

            op.SkippedCount.Should().Be(1);
            op.State.Should().Be(OperationState.Completed);
        }

        asked.Should().ContainSingle();
        File.Exists(P("out", "b.txt")).Should().BeTrue();
        File.Exists(P("out", "a.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Copy_LockedSource_RetryRunsTheItemAgain()
    {
        File.WriteAllText(P("a.txt"), "a");
        Directory.CreateDirectory(P("out"));
        var a = await EntryAsync(P("a.txt"));

        var lockStream = new FileStream(P("a.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var attempts = 0;
        var op = new CopyOperation(_fs, _fs, [a], new VfsPath(P("out")), new OverwriteStrategy(),
            new CopyOptions(), NullLogger<CopyOperation>.Instance)
        {
            ErrorHandler = (_, _) =>
            {
                attempts++;
                lockStream.Dispose();   // the user closed the other program, then pressed Retry
                return Task.FromResult(OperationErrorResolution.Retry);
            },
        };

        await op.ExecuteAsync(CancellationToken.None);

        attempts.Should().Be(1);
        File.ReadAllText(P("out", "a.txt")).Should().Be("a");
        op.SkippedCount.Should().Be(0);
    }

    // ── Delete ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_Permanent_RemovesAReadOnlyFile()
    {
        var file = P("locked.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var op = new DeleteOperation(_fs, [await EntryAsync(file)], moveToRecycleBin: false, NullLogger<DeleteOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        File.Exists(file).Should().BeFalse();
        op.State.Should().Be(OperationState.Completed);
    }

    [Fact]
    public async Task Delete_Permanent_RemovesAFolderTreeWithReadOnlyFiles()
    {
        // Git keeps its object store read-only; deleting a checkout has to cope with that.
        Directory.CreateDirectory(P("repo", ".git", "objects"));
        var blob = P("repo", ".git", "objects", "ab");
        File.WriteAllText(blob, "x");
        File.SetAttributes(blob, FileAttributes.ReadOnly);

        var op = new DeleteOperation(_fs, [await EntryAsync(P("repo"))], moveToRecycleBin: false, NullLogger<DeleteOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        Directory.Exists(P("repo")).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_LockedFile_IsSkippedWhenTheUserSaysSkipAndTheOthersGo()
    {
        File.WriteAllText(P("busy.txt"), "x");
        File.WriteAllText(P("free.txt"), "y");
        var busy = await EntryAsync(P("busy.txt"));
        var free = await EntryAsync(P("free.txt"));

        using (new FileStream(P("busy.txt"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var op = new DeleteOperation(_fs, [busy, free], moveToRecycleBin: false, NullLogger<DeleteOperation>.Instance)
            {
                ErrorHandler = (_, _) => Task.FromResult(OperationErrorResolution.SkipAll),
            };
            await op.ExecuteAsync(CancellationToken.None);
            op.SkippedCount.Should().Be(1);
        }

        File.Exists(P("free.txt")).Should().BeFalse();
        File.Exists(P("busy.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task Delete_OnAFileSystemWithoutABin_DeletesPermanentlyEvenWhenTheBinIsAsked()
    {
        var mem = new InMemoryFileSystem("file");
        mem.AddFile("/x/a.txt", [1]);
        var entry = (await mem.GetEntryAsync(new VfsPath("/x/a.txt")))!;

        var op = new DeleteOperation(mem, [entry], moveToRecycleBin: true, NullLogger<DeleteOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        mem.HasFile("/x/a.txt").Should().BeFalse();
        op.LogDescriptor.Destination.Should().BeNull("the log must not claim a Recycle Bin that was not used");
    }

    // ── Rename ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rename_OnlyTheCaseOfAFolder_Works()
    {
        Directory.CreateDirectory(P("src"));

        var op = new RenameEntryOperation(_fs, await EntryAsync(P("src")), "Src", NullLogger.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        new DirectoryInfo(P("Src")).Parent!.GetDirectories().Select(d => d.Name).Should().Contain("Src");
        op.LogDescriptor.Kind.Should().Be(OperationKind.Rename);
    }

    [Fact]
    public async Task Rename_ToANameThatExists_FailsInsteadOfOverwriting()
    {
        File.WriteAllText(P("a.txt"), "a");
        File.WriteAllText(P("b.txt"), "b");

        var op = new RenameEntryOperation(_fs, await EntryAsync(P("a.txt")), "b.txt", NullLogger.Instance);

        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None)).Should().ThrowAsync<IOException>();
        File.ReadAllText(P("b.txt")).Should().Be("b");
        File.Exists(P("a.txt")).Should().BeTrue();
    }

    // ── Create folder ─────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDirectory_CreatesTheFolderAndLogsIt()
    {
        var op = new CreateDirectoryOperation(_fs, new VfsPath(P("made", "deep")), NullLogger.Instance);

        await op.ExecuteAsync(CancellationToken.None);

        Directory.Exists(P("made", "deep")).Should().BeTrue();
        op.State.Should().Be(OperationState.Completed);
        op.LogDescriptor.Kind.Should().Be(OperationKind.CreateFolder);
    }

    // ── Junction on the real disk ─────────────────────────────────────────────

    [Fact]
    public async Task Copy_RealJunction_IsNotFollowed()
    {
        Directory.CreateDirectory(P("proj", "real"));
        File.WriteAllText(P("proj", "real", "f.txt"), "x");
        Directory.CreateDirectory(P("elsewhere"));
        File.WriteAllText(P("elsewhere", "secret.txt"), "s");
        var link = P("proj", "link");
        try
        {
            Directory.CreateSymbolicLink(link, P("elsewhere"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return;   // creating a symbolic link needs Developer Mode or elevation; nothing to test here
        }
        Directory.CreateDirectory(P("out"));

        var op = NewCopy(await EntryAsync(P("proj")), P("out"));
        await op.ExecuteAsync(CancellationToken.None);

        File.Exists(P("out", "proj", "real", "f.txt")).Should().BeTrue();
        Directory.Exists(P("out", "proj", "link")).Should().BeFalse();
        op.SkippedCount.Should().Be(1);
    }
}
