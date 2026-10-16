using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>
/// Taking a logged rename batch back against the folder as it is now (6.55): what still carries its
/// new name gets the old one back, nothing is overwritten, and everything else is counted.
/// </summary>
public class RenameUndoPlanTests
{
    private static RenamedName R(string from, string to) => new(from, to);

    [Fact]
    public void Build_EveryFileStillRenamed_RestoresAll()
    {
        var plan = RenameUndoPlan.Build([R("a.pdf", "invoice.pdf"), R("b.pdf", "scan.pdf")], ["invoice.pdf", "scan.pdf"]);

        plan.Pairs.Should().Equal(("invoice.pdf", "a.pdf"), ("scan.pdf", "b.pdf"));
        plan.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void Build_FileRenamedAgainOrDeleted_IsMissing()
    {
        var plan = RenameUndoPlan.Build([R("a.pdf", "invoice.pdf"), R("b.pdf", "scan.pdf")], ["scan.pdf", "other.pdf"]);

        plan.Pairs.Should().Equal(("scan.pdf", "b.pdf"));
        plan.Missing.Should().Equal("invoice.pdf");
        plan.Blocked.Should().BeEmpty();
    }

    [Fact]
    public void Build_OldNameTakenByAnotherFile_IsBlocked_NotOverwritten()
    {
        var plan = RenameUndoPlan.Build([R("a.pdf", "invoice.pdf")], ["invoice.pdf", "A.PDF"]);

        plan.Pairs.Should().BeEmpty();
        plan.Blocked.Should().Equal("invoice.pdf");
        plan.SkippedCount.Should().Be(1);
    }

    [Fact]
    public void Build_Chain_FreesItsOwnOldNames()
    {
        // a → b and b → c: undoing needs c → b while b → a vacates b.
        var plan = RenameUndoPlan.Build([R("a.pdf", "b.pdf"), R("b.pdf", "c.pdf")], ["b.pdf", "c.pdf"]);

        plan.Pairs.Should().BeEquivalentTo(new[] { ("b.pdf", "a.pdf"), ("c.pdf", "b.pdf") });
        plan.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void Build_Swap_IsRestored()
    {
        var plan = RenameUndoPlan.Build([R("a.pdf", "b.pdf"), R("b.pdf", "a.pdf")], ["a.pdf", "b.pdf"]);

        plan.Pairs.Should().HaveCount(2);
        plan.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void Build_CaseOnlyRename_HoldsItsOwnOldName()
    {
        var plan = RenameUndoPlan.Build([R("scan.pdf", "Scan.pdf")], ["Scan.pdf"]);

        plan.Pairs.Should().Equal(("Scan.pdf", "scan.pdf"));
    }

    [Fact]
    public void Build_ChainWhoseHeadIsBlocked_BlocksTheRestToo()
    {
        // a → b, b → c; since then someone created a new "a.pdf". b cannot go back to a, so b is not
        // vacated, so c cannot go back to b either.
        var plan = RenameUndoPlan.Build([R("a.pdf", "b.pdf"), R("b.pdf", "c.pdf")], ["a.pdf", "b.pdf", "c.pdf"]);

        plan.Pairs.Should().BeEmpty();
        plan.Blocked.Should().BeEquivalentTo("b.pdf", "c.pdf");
    }

    [Fact]
    public void Build_HandEditedLineWithEmptyEntries_IgnoresThem()
    {
        var plan = RenameUndoPlan.Build([null, R("", "x.pdf"), R("a.pdf", "b.pdf")], ["b.pdf", "x.pdf"]);

        plan.Pairs.Should().Equal(("b.pdf", "a.pdf"));
        plan.SkippedCount.Should().Be(0);
    }

    [Fact]
    public void IsUsable_WithoutFolderOrRenames_IsFalse()
    {
        new RenameBatchRecord("C:/dl", [R("a", "b")]).IsUsable.Should().BeTrue();
        new RenameBatchRecord("", [R("a", "b")]).IsUsable.Should().BeFalse();
        new RenameBatchRecord("C:/dl", []).IsUsable.Should().BeFalse();
        new RenameBatchRecord("C:/dl", null!).IsUsable.Should().BeFalse();
    }
}

/// <summary>
/// The AI rename's undo outliving its window (6.55): the batch's log line records what was renamed,
/// survives the JSONL round trip, and is offered for undo in the log window.
/// </summary>
public sealed class LoggedRenameUndoTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"tcv-oplog-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    private static InMemoryFileSystem Folder(params string[] names)
    {
        var fs = new InMemoryFileSystem("file");
        fs.AddDirectory("/dl");
        foreach (var name in names)
        {
            fs.AddFile("/dl/" + name, System.Text.Encoding.UTF8.GetBytes(name));
        }

        return fs;
    }

    [Fact]
    public async Task LogDescriptor_AfterARun_RecordsWhatWasRenamed()
    {
        var fs = Folder("a.pdf", "b.pdf");
        var op = new RenameOperation(
            fs, new VfsPath("/dl"), [("a.pdf", "x.pdf"), ("b.pdf", "y.pdf")], NullLogger.Instance);

        await op.ExecuteAsync(CancellationToken.None);

        var renames = op.LogDescriptor!.Renames;
        renames.Should().NotBeNull();
        renames!.Directory.Should().Be("/dl");
        renames.Pairs.Should().Equal(new RenamedName("a.pdf", "x.pdf"), new RenamedName("b.pdf", "y.pdf"));
    }

    [Fact]
    public void LogDescriptor_NothingRenamed_RecordsNothingToUndo()
    {
        var op = new RenameOperation(Folder("a.pdf"), new VfsPath("/dl"), [("a.pdf", "x.pdf")], NullLogger.Instance);

        op.LogDescriptor!.Renames.Should().BeNull();
    }

    [Fact]
    public async Task Queue_LogsTheRenames_AndTheyRoundTripThroughTheFile()
    {
        var log = new OperationLog(_logPath);
        using (var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log))
        {
            var op = new RenameOperation(Folder("a.pdf"), new VfsPath("/dl"), [("a.pdf", "x.pdf")], NullLogger.Instance);
            await queue.EnqueueAsync(op);
        }

        var entry = new OperationLog(_logPath).LoadAll().Should().ContainSingle().Subject;
        entry.Renames.Should().NotBeNull();
        entry.Renames!.Directory.Should().Be("/dl");
        entry.Renames.Pairs.Should().Equal(new RenamedName("a.pdf", "x.pdf"));
    }

    [Fact]
    public void Append_EntryWithoutRenames_WritesNoRenamesField()
    {
        new OperationLog(_logPath).Append(new OperationLogEntry { Kind = OperationKind.Copy, Source = "C:\\a" });

        File.ReadAllText(_logPath).Should().NotContain("Renames", "other operations' lines stay as they were");
    }

    [Fact]
    public async Task UndoFromTheLog_AfterTheDialogIsGone_PutsTheNamesBack()
    {
        // The whole path the log window takes: the logged record, the plan against the folder now,
        // and a new batch through the queue.
        var fs = Folder("a.pdf", "b.pdf");
        var log = new OperationLog(_logPath);
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance, log);
        await queue.EnqueueAsync(new RenameOperation(
            fs, new VfsPath("/dl"), [("a.pdf", "b.pdf"), ("b.pdf", "c.pdf")], NullLogger.Instance));

        var record = log.LoadAll().Single().Renames!;
        var names = (await fs.ListAsync(new VfsPath(record.Directory))).Select(e => e.Name);
        var plan = RenameUndoPlan.Build(record.Pairs, names);
        var undo = new RenameOperation(fs, new VfsPath(record.Directory), plan.Pairs, NullLogger.Instance);
        await queue.EnqueueAsync(undo);

        undo.Failures.Should().BeEmpty();
        System.Text.Encoding.UTF8.GetString(fs.ReadFile("/dl/a.pdf")).Should().Be("a.pdf");
        System.Text.Encoding.UTF8.GetString(fs.ReadFile("/dl/b.pdf")).Should().Be("b.pdf");
        fs.HasFile("/dl/c.pdf").Should().BeFalse();
        log.LoadAll().Should().HaveCount(2, "the undo is logged like the batch it takes back (INV-10)");
    }

    [Fact]
    public async Task LogWindow_RenameLine_OffersUndo_AndHandsTheBatchOver()
    {
        var batch = new RenameBatchRecord("C:/dl", [new RenamedName("a.pdf", "x.pdf")]);
        var log = new OperationLog(_logPath);
        log.Append(new OperationLogEntry { Kind = OperationKind.Copy, Source = "C:\\a" });
        log.Append(new OperationLogEntry { Kind = OperationKind.Rename, Source = "C:/dl/a.pdf", Renames = batch });

        RenameBatchRecord? handed = null;
        using var vm = new OperationLogDialogViewModel(log, undoRename: b =>
        {
            handed = b;
            return Task.CompletedTask;
        });

        vm.Selected = vm.Entries.Single(e => e.Entry.Kind == OperationKind.Copy);
        vm.CanUndoSelected.Should().BeFalse("only a rename batch records what to take back");

        vm.Selected = vm.Entries.Single(e => e.Entry.Kind == OperationKind.Rename);
        vm.CanUndoSelected.Should().BeTrue();

        await vm.UndoSelectedCommand.ExecuteAsync(null);

        handed.Should().NotBeNull();
        handed!.Directory.Should().Be("C:/dl");
        handed.Pairs.Should().Equal(batch.Pairs);
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void LogWindow_WithoutAnUndoCallback_OffersNoUndo()
    {
        var log = new OperationLog(_logPath);
        log.Append(new OperationLogEntry
        {
            Kind = OperationKind.Rename,
            Source = "C:/dl/a.pdf",
            Renames = new RenameBatchRecord("C:/dl", [new RenamedName("a.pdf", "x.pdf")]),
        });

        using var vm = new OperationLogDialogViewModel(log);
        vm.Selected = vm.Entries.Single();

        vm.CanUndoSelected.Should().BeFalse();
    }
}
