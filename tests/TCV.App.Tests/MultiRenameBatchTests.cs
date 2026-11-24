using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.MultiRename;

namespace TCV.App.Tests;

/// <summary>
/// Multi-Rename in the background (6.96): the window hands over a batch and closes, the queue does the
/// renaming, and the log line is how a batch is taken back.
/// </summary>
public sealed class MultiRenameBatchTests : IDisposable
{
    private readonly string _logFile = Path.Combine(Path.GetTempPath(), "tcv-mr-log-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (File.Exists(_logFile))
        {
            File.Delete(_logFile);
        }
    }

    private static async Task<List<IFileEntry>> EntriesAsync(InMemoryFileSystem fs, params string[] paths)
    {
        var entries = new List<IFileEntry>();
        foreach (var path in paths)
        {
            entries.Add((await fs.GetEntryAsync(new VfsPath(path)))!);
        }

        return entries;
    }

    private static async Task<MultiRenameBatch> AcceptedAsync(InMemoryFileSystem fs, string template, params string[] paths)
    {
        var vm = new MultiRenameDialogViewModel(await EntriesAsync(fs, paths), fs) { TemplateText = template };
        await vm.ConfirmCommand.ExecuteAsync(null);
        return vm.Accepted!;
    }

    [Fact]
    public async Task Confirm_HandsOverTheBatchAndClosesWithoutRenamingAnything()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        var vm = new MultiRenameDialogViewModel(await EntriesAsync(fs, "/d/a.txt"), fs) { TemplateText = "[N]_x.[E]" };
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        await vm.ConfirmCommand.ExecuteAsync(null);

        closed.Should().Be(1);
        vm.Accepted.Should().NotBeNull();
        vm.Accepted!.Changes.Should().Be(1);
        fs.HasFile("/d/a.txt").Should().BeTrue("the queue renames, after the window has gone");
    }

    [Fact]
    public async Task Confirm_NothingToRename_StaysOpenAndSaysSo()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        var vm = new MultiRenameDialogViewModel(await EntriesAsync(fs, "/d/a.txt"), fs);
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        await vm.ConfirmCommand.ExecuteAsync(null);

        closed.Should().Be(0);
        vm.Accepted.Should().BeNull();
        vm.StatusText.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Confirm_AConflictInTheBatch_IsCountedAndLeftOut()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        fs.AddFile("/d/b.txt", [2]);
        fs.AddFile("/d/b_x.txt", [3]);

        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt", "/d/b.txt");

        batch.Changes.Should().Be(1);
        batch.Conflicts.Should().Be(1);
    }

    [Fact]
    public async Task ByFolder_HitsFromSeveralFolders_OneOperationPerFolder()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/one/a.txt", [1]);
        fs.AddFile("/one/b.txt", [2]);
        fs.AddFile("/two/c.txt", [3]);

        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/one/a.txt", "/two/c.txt", "/one/b.txt");
        var folders = batch.ByFolder(fs);

        folders.Select(f => f.Source).Should().BeEquivalentTo(["/one", "/two"]);
        folders.Single(f => f.Source == "/one").Files.Select(e => e.Name).Should().Equal("a.txt", "b.txt");
        folders.Single(f => f.Source == "/one").Previews.Select(p => p.NewName).Should().Equal("a_x.txt", "b_x.txt");
        folders.Single(f => f.Source == "/two").Changes.Should().Be(1);
    }

    [Fact]
    public async Task ByFolder_AFileSystemThatRenamesAllAtOnce_KeepsTheOneBatch()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/one/a.txt", [1]);
        fs.AddFile("/two/c.txt", [3]);
        var entries = await EntriesAsync(fs, "/one/a.txt", "/two/c.txt");
        var vm = new MultiRenameDialogViewModel(entries, fs) { TemplateText = "[N]_x.[E]" };
        await vm.ConfirmCommand.ExecuteAsync(null);

        var archive = new Moq.Mock<IVirtualFileSystem>();
        archive.As<IBatchRenameFileSystem>().SetupGet(b => b.CanRenameAsBatch).Returns(true);

        vm.Accepted!.ByFolder(archive.Object).Should().ContainSingle()
            .Which.Files.Should().HaveCount(2, "an archive's batch is one rewrite, all or nothing, as confirmed");
    }

    private async Task<(RenameBatchOperation Operation, OperationLogEntry Line)> RunAsync(
        IVirtualFileSystem fs, MultiRenameFolder folder, bool undoable, string? server = null)
    {
        var engine = new RenameEngine(fs);
        var operation = new RenameBatchOperation(
            ct => engine.ExecuteBatchAsync(folder.Files, folder.Previews, ct), folder.Source, folder.Changes, fs)
        {
            Undoable = undoable,
            Server = server,
        };
        var log = new OperationLog(_logFile);
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);

        await queue.EnqueueAsync(operation);

        return (operation, log.LoadAll().Single());
    }

    [Fact]
    public async Task LogLine_OfABatchThatRewroteAnArchive_NamesIt_AndKeepsTheUndoRecord()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt");
        var folder = batch.ByFolder(fs).Single();
        var engine = new RenameEngine(fs);
        var operation = new RenameBatchOperation(
            ct => engine.ExecuteBatchAsync(folder.Files, folder.Previews, ct), folder.Source, folder.Changes, fs)
        {
            Undoable = true,
            ChangedArchive = @"D:\repo\dist.zip",
        };
        var log = new OperationLog(_logFile);
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);

        await queue.EnqueueAsync(operation);

        var line = log.LoadAll().Single();
        line.ChangedArchive.Should().Be(@"D:\repo\dist.zip", "the agent dashboard reads the changed archive from here (TRAP-62)");
        line.Renames.Should().NotBeNull("the two fields of the descriptor do not exclude each other");
    }

    [Fact]
    public async Task LogLine_OfADiskBatch_NamesWhatWasDoneSoItCanBeTakenBack()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        fs.AddFile("/d/b.txt", [2]);
        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt", "/d/b.txt");

        var (operation, line) = await RunAsync(fs, batch.ByFolder(fs).Single(), undoable: true);

        operation.State.Should().Be(OperationState.Completed);
        fs.HasFile("/d/a_x.txt").Should().BeTrue();
        line.Kind.Should().Be(OperationKind.Rename);
        line.Renames.Should().NotBeNull();
        line.Renames!.IsUsable.Should().BeTrue();
        line.Renames.Directory.Should().Be("/d");
        line.Renames.Server.Should().BeNull();
        line.Renames.Pairs.Should().BeEquivalentTo(new[] { new RenamedName("a.txt", "a_x.txt"), new RenamedName("b.txt", "b_x.txt") });
    }

    [Fact]
    public async Task LogLine_OfAServerBatch_SaysWhichServer()
    {
        var fs = new InMemoryFileSystem("sftp");
        fs.AddFile("/d/a.txt", [1]);
        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt");

        var (_, line) = await RunAsync(fs, batch.ByFolder(fs).Single(), undoable: true, server: "sftp://me@host");

        line.Renames!.Server.Should().Be("sftp://me@host");
    }

    [Fact]
    public async Task LogLine_OfAnArchiveBatch_OffersNoUndoItCouldNotDo()
    {
        var fs = new InMemoryFileSystem("zip");
        fs.AddFile("/d/a.txt", [1]);
        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt");

        var (_, line) = await RunAsync(fs, batch.ByFolder(fs).Single(), undoable: false);

        line.Renames.Should().BeNull();
    }

    [Theory]
    [InlineData("sftp", OperationLane.Network)]
    [InlineData("ftp", OperationLane.Network)]
    [InlineData("mem", OperationLane.Local)]
    public void Lane_FollowsWhereTheFilesAre(string scheme, OperationLane lane)
    {
        var operation = new RenameBatchOperation(
            _ => Task.FromResult(new RenameBatchResult([], [], false)), "/d", 1, new InMemoryFileSystem(scheme));

        operation.Lane.Should().Be(lane);
    }

    [Fact]
    public async Task Run_APartOfTheBatchFails_TheRestIsDoneAndReported()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        fs.AddFile("/d/b.txt", [2]);
        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt", "/d/b.txt");

        // Somebody took the name after the window closed: the server refuses this one.
        var server = new Moq.Mock<IVirtualFileSystem>();
        server.SetupGet(v => v.Scheme).Returns("sftp");
        server.Setup(v => v.RenameAsync(Moq.It.Is<VfsPath>(x => x.FileName == "a.txt"), "a_x.txt", Moq.It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        server.Setup(v => v.RenameAsync(Moq.It.Is<VfsPath>(x => x.FileName == "b.txt"), "b_x.txt", Moq.It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("taken"));

        var (operation, line) = await RunAsync(server.Object, batch.ByFolder(fs).Single(), undoable: true);

        operation.State.Should().Be(OperationState.Failed);
        operation.Result!.Performed.Should().ContainSingle();
        operation.Result.Failures.Should().ContainSingle().Which.Error.Should().Be("taken");
        line.Renames!.Pairs.Should().ContainSingle("what did get renamed can still be taken back");
    }

    [Fact]
    public async Task ExecuteBatchAsync_SaysHowManyAreDone()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/d/a.txt", [1]);
        fs.AddFile("/d/b.txt", [2]);
        var batch = await AcceptedAsync(fs, "[N]_x.[E]", "/d/a.txt", "/d/b.txt");
        var seen = new List<int>();

        await new RenameEngine(fs).ExecuteBatchAsync(batch.Files, batch.Previews, default, new SyncProgress(seen));

        seen.Should().Equal(1, 2);
    }

    private sealed class SyncProgress(List<int> sink) : IProgress<int>
    {
        public void Report(int value) => sink.Add(value);
    }
}
