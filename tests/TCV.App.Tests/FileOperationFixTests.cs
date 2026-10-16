using System.Diagnostics;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.FileSystem.Native;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The second file-operations audit: destinations typed into F5/F6, renaming on the way, copying
/// into the same folder, the same item under two spellings, and where the queue runs its work.
/// </summary>
public sealed class FileOperationFixTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-fix-" + Guid.NewGuid().ToString("N"));
    private readonly WindowsFileSystem _disk = new(NullLogger<WindowsFileSystem>.Instance);

    public FileOperationFixTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* the OS cleans the temp folder later */ }
    }

    private string P(params string[] parts) => Path.Combine([_root, .. parts]);

    private static async Task<IFileEntry> EntryAsync(IVirtualFileSystem fs, string path) =>
        (await fs.GetEntryAsync(new VfsPath(path)))!;

    // ── The typed destination ─────────────────────────────────────────────────

    [Fact]
    public void ResolveTypedTarget_RelativePath_IsTakenFromTheActivePanelFolder()
    {
        var (path, side) = OperationPaths.ResolveTypedTarget(@"backup\new", @"C:\work\proj", true, true);

        path.Should().Be(@"C:\work\proj\backup\new", "not the process's working directory");
        side.Should().Be(OperationPaths.TargetSide.Active);
    }

    [Fact]
    public void ResolveTypedTarget_ParentSegments_AreResolved()
    {
        OperationPaths.ResolveTypedTarget(@"..\out", @"C:\work\proj", true, true).Path
            .Should().Be(@"C:\work\out");
    }

    [Fact]
    public void ResolveTypedTarget_DrivePathWhileOppositeIsAServer_MeansTheDisk()
    {
        var (path, side) = OperationPaths.ResolveTypedTarget(@"D:\backup", @"C:\work", true, oppositeIsLocal: false);

        path.Should().Be(@"D:\backup");
        side.Should().Be(OperationPaths.TargetSide.LocalDisk);
    }

    [Fact]
    public void ResolveTypedTarget_PosixPathWhileOppositeIsAServer_MeansTheServer()
    {
        OperationPaths.ResolveTypedTarget("/home/u/in", @"C:\work", true, oppositeIsLocal: false)
            .Should().Be(("/home/u/in", OperationPaths.TargetSide.Opposite));
    }

    [Fact]
    public void ResolveTypedTarget_BareDriveLetter_MeansTheDriveRoot()
    {
        OperationPaths.ResolveTypedTarget("D:", @"C:\work", true, true).Path.Should().Be(@"D:\");
    }

    [Fact]
    public void ResolveTypedTarget_RelativeOnAServerPanel_StaysOnTheServer()
    {
        OperationPaths.ResolveTypedTarget("../b/./c", "/srv/a", activeIsLocal: false, oppositeIsLocal: true)
            .Should().Be(("/srv/b/c", OperationPaths.TargetSide.Active));
    }

    [Fact]
    public void SplitTarget_ExistingFolder_IsTheDestinationFolder()
    {
        var folder = new FileEntry(new VfsPath("/d/out"), "out", true, 0, DateTime.Now, FileAttributes.Directory);

        OperationPaths.SplitTarget(new VfsPath("/d/out"), 1, folder).Should().Be((new VfsPath("/d/out"), (string?)null));
    }

    [Fact]
    public void SplitTarget_OneItemToAMissingPath_IsARename()
    {
        var (directory, name) = OperationPaths.SplitTarget(new VfsPath("/d/new.txt"), 1, null);

        directory.Should().Be(new VfsPath("/d"));
        name.Should().Be("new.txt");
    }

    [Fact]
    public void SplitTarget_SeveralItemsToAMissingPath_IsAFolderToCreate()
    {
        OperationPaths.SplitTarget(new VfsPath("/d/newdir"), 3, null).Should().Be((new VfsPath("/d/newdir"), (string?)null));
    }

    [Fact]
    public void SplitTarget_TrailingSeparator_MeansAFolderEvenForOneItem()
    {
        OperationPaths.SplitTarget(new VfsPath("/d/newdir"), 1, null, markedAsFolder: true)
            .Should().Be((new VfsPath("/d/newdir"), (string?)null));
    }

    [Theory]
    [InlineData("C:/src/a.txt", @"C:\src\a.txt")]
    [InlineData("//server/share/a", @"\\server\share\a")]
    [InlineData("/home/u/a", "/home/u/a")]
    public void ToDisplay_LocalPathsGetBackslashes(string vfs, string expected)
    {
        OperationPaths.ToDisplay(vfs).Should().Be(expected);
    }

    // ── Renaming on the way, creating the destination ─────────────────────────

    [Fact]
    public async Task Copy_WithTargetName_CopiesUnderTheNewName()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1, 2]);
        fs.AddDirectory("/b");

        var op = new CopyOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/b"),
            new SkipStrategy(), NullLogger<CopyOperation>.Instance) { TargetName = "y.txt" };
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/b/y.txt").Should().Equal(1, 2);
        fs.HasFile("/b/x.txt").Should().BeFalse();
    }

    [Fact]
    public async Task LogDescriptor_CopyWithTargetName_SaysTheFolderTheCopyArrivedIn()
    {
        // The destination names the copy itself; where it arrived is said apart (6.63).
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);

        var op = new CopyOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/b"),
            new SkipStrategy(), NullLogger<CopyOperation>.Instance) { TargetName = "y.txt" };

        op.LogDescriptor.Destination.Should().Be("/b/y.txt");
        op.LogDescriptor.ArrivedIn.Should().Be("/b");
    }

    [Fact]
    public async Task LogDescriptor_MoveWithTargetName_SaysTheFolderTheItemArrivedIn()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);

        var op = new MoveOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/b"),
            new SkipStrategy(), NullLogger<MoveOperation>.Instance) { TargetName = "y.txt" };

        op.LogDescriptor.Destination.Should().Be("/b/y.txt");
        op.LogDescriptor.ArrivedIn.Should().Be("/b");
    }

    [Fact]
    public async Task Copy_CreateDestination_CreatesTheMissingFolderFirst()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);

        var op = new CopyOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/new"),
            new SkipStrategy(), NullLogger<CopyOperation>.Instance) { CreateDestination = true };
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasDirectory("/new").Should().BeTrue();
        fs.ReadFile("/new/x.txt").Should().Equal(1);
    }

    [Fact]
    public async Task Move_WithTargetNameInTheSameFolder_Renames()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [7]);

        var op = new MoveOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/a"),
            new SkipStrategy(), NullLogger<MoveOperation>.Instance) { TargetName = "renamed.txt" };
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasFile("/a/x.txt").Should().BeFalse();
        fs.ReadFile("/a/renamed.txt").Should().Equal(7);
    }

    [Fact]
    public async Task Move_CaseOnlyTargetNameOnDisk_RenamesInsteadOfDoingNothing()
    {
        File.WriteAllText(P("readme.md"), "x");

        var op = new MoveOperation(_disk, _disk, [await EntryAsync(_disk, P("readme.md"))], new VfsPath(_root),
            new SkipStrategy(), NullLogger<MoveOperation>.Instance) { TargetName = "README.md" };
        await op.ExecuteAsync(CancellationToken.None);

        Directory.GetFiles(_root).Select(Path.GetFileName).Should().Equal("README.md");
    }

    // ── Copy into the folder the item is in ───────────────────────────────────

    [Fact]
    public async Task Copy_IntoItsOwnFolderWithDuplicateInPlace_MakesANumberedCopy()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1, 2, 3]);

        var op = new CopyOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/a"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance) { DuplicateInPlace = true };
        await op.ExecuteAsync(CancellationToken.None);

        fs.ReadFile("/a/x.txt").Should().Equal(1, 2, 3);
        fs.ReadFile("/a/x (1).txt").Should().Equal(1, 2, 3);
        op.SkippedCount.Should().Be(0);
    }

    [Fact]
    public async Task Copy_IntoItsOwnFolderWithoutDuplicate_IsSkippedWithAWarning()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);

        var op = new CopyOperation(fs, fs, [await EntryAsync(fs, "/a/x.txt")], new VfsPath("/a"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.SkippedCount.Should().Be(1);
        op.Warnings.Should().ContainSingle("the user must hear that nothing was copied");
    }

    // ── The same item under two spellings ─────────────────────────────────────

    [Fact]
    public void IsSameItem_DifferentSpellingsOfOneFile_AreTheSame()
    {
        Directory.CreateDirectory(P("dir"));
        File.WriteAllText(P("dir", "a.txt"), "x");

        FileIdentity.IsSameItem(P("dir", "a.txt"), Path.Combine(_root, "dir", ".", "A.TXT")).Should().BeTrue();
    }

    [Fact]
    public void IsSameItem_TwoFilesOrAMissingOne_AreNotTheSame()
    {
        File.WriteAllText(P("a.txt"), "x");
        File.WriteAllText(P("b.txt"), "x");

        FileIdentity.IsSameItem(P("a.txt"), P("b.txt")).Should().BeFalse();
        FileIdentity.IsSameItem(P("a.txt"), P("missing.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task Move_OntoItselfThroughAJunction_KeepsTheFile()
    {
        Directory.CreateDirectory(P("real"));
        File.WriteAllText(P("real", "a.txt"), "precious");
        if (!TryCreateJunction(P("link"), P("real"))) return;   // no mklink here: nothing to test

        FileIdentity.IsSameItem(P("real", "a.txt"), P("link", "a.txt")).Should().BeTrue();

        var op = new MoveOperation(_disk, _disk, [await EntryAsync(_disk, P("real", "a.txt"))], new VfsPath(P("link")),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        File.ReadAllText(P("real", "a.txt")).Should().Be("precious");
        op.Warnings.Should().ContainSingle();
        Directory.Delete(P("link"));   // the link only, never what it points to
    }

    private static bool TryCreateJunction(string link, string target)
    {
        try
        {
            using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            mklink.WaitForExit(10_000);
            return mklink.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    // ── Errors: Abort is not reported twice ───────────────────────────────────

    [Fact]
    public async Task Abort_InTheErrorPrompt_MarksTheFailureAsAlreadyShown()
    {
        var fs = new InMemoryFileSystem();
        var ghost = new FileEntry(new VfsPath("/a/ghost.txt"), "ghost.txt", false, 5, DateTime.Now, FileAttributes.Normal);
        OperationErrorInfo? asked = null;

        var op = new CopyOperation(fs, fs, [ghost], new VfsPath("/b"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance)
        {
            ErrorHandler = (info, _) =>
            {
                asked = info;
                return Task.FromResult(OperationErrorResolution.Abort);
            },
        };

        await op.Invoking(o => o.ExecuteAsync(CancellationToken.None)).Should().ThrowAsync<FileNotFoundException>();

        op.State.Should().Be(OperationState.Failed);
        op.FailureAlreadyShown.Should().BeTrue();
        op.HasUnreportedFailure.Should().BeFalse("the user read this error in the prompt a moment ago");
        asked.Should().NotBeNull();
    }

    // ── AI rename undo ────────────────────────────────────────────────────────

    [Fact]
    public async Task RenameOperation_CreateUndo_IsAQueueableOperationThatRestoresTheNames()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/dl/a.pdf", [1]);
        var rename = new RenameOperation(fs, new VfsPath("/dl"), [("a.pdf", "invoice.pdf")], NullLogger.Instance);
        await rename.ExecuteAsync(CancellationToken.None);

        var undo = rename.CreateUndo();
        undo.LogDescriptor.Kind.Should().Be(OperationKind.Rename, "the undo is logged like the rename");
        await undo.ExecuteAsync(CancellationToken.None);

        fs.HasFile("/dl/a.pdf").Should().BeTrue();
        fs.HasFile("/dl/invoice.pdf").Should().BeFalse();
        undo.NotPerformedCount.Should().Be(0);
    }

    // ── Rename dialog selection ───────────────────────────────────────────────

    [Theory]
    [InlineData("report.final.pdf", false, 12)]
    [InlineData("v1.2", true, 4)]
    [InlineData(".gitignore", false, 10)]
    public void RenameDialog_InitialSelection_LeavesTheExtensionOfFilesOnly(string name, bool isDirectory, int expected)
    {
        new RenameDialogViewModel(name, isDirectory).InitialSelectionLength.Should().Be(expected);
    }

    // ── The queue's worker ────────────────────────────────────────────────────

    [Fact]
    public async Task OperationQueue_BuiltOnAUiLikeContext_RunsOperationsOffThatContext()
    {
        var ui = new RecordingContext();
        var previous = SynchronizationContext.Current;
        OperationQueue queue;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            queue = new OperationQueue(NullLogger<OperationQueue>.Instance);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        using (queue)
        {
            var probe = new ContextProbe();
            await queue.EnqueueAsync(probe);

            probe.Seen.Should().NotBeSameAs(ui, "the dispatcher builds the queue, and operations must not run on it (INV-06)");
        }
    }

    /// <summary>Stands in for the dispatcher: whatever is posted to it runs with it installed.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            });
    }

    private sealed class ContextProbe : FileOperationBase
    {
        public SynchronizationContext? Seen { get; private set; }

        public override Task ExecuteAsync(CancellationToken ct)
        {
            Seen = SynchronizationContext.Current;
            State = OperationState.Completed;
            return Task.CompletedTask;
        }
    }
}
