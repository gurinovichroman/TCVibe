using System.IO;
using FluentAssertions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>The rules behind the file dialogs, tested without a window.</summary>
public class DialogLogicTests
{
    private static IFileEntry File(string name, bool directory = false, DateTime? modified = null) =>
        new FileEntry(new VfsPath("/x/" + name), name, directory, 10, modified ?? new DateTime(2025, 1, 1),
            directory ? FileAttributes.Directory : FileAttributes.Normal);

    // ── Delete confirmation ───────────────────────────────────────────────────

    [Fact]
    public void DeleteDialog_EverythingRecycled_IsTheOnlyCaseWhereEnterConfirms()
    {
        var vm = new DeleteDialogViewModel([File("a"), File("b")], recycleCount: 2);

        vm.IsAllRecycled.Should().BeTrue();
        vm.DeleteIsDefault.Should().BeTrue();
        vm.CancelIsDefault.Should().BeFalse();
        vm.WarnsPermanent.Should().BeFalse();
        vm.RecycleBinNote.Should().Contain("Delete_NoteRecycle");
    }

    [Fact]
    public void DeleteDialog_NothingRecycled_SaysPermanentAndMakesCancelTheDefault()
    {
        var vm = new DeleteDialogViewModel([File("a")], recycleCount: 0);

        vm.IsPermanent.Should().BeTrue();
        vm.RecycleBinNote.Should().Contain("Delete_NotePermanent");
        vm.WarnsPermanent.Should().BeTrue();
        vm.DeleteIsDefault.Should().BeFalse("Enter must not confirm an irreversible delete");
        vm.CancelIsDefault.Should().BeTrue();
    }

    [Fact]
    public void DeleteDialog_SomeRecycled_SaysSoAndWarns()
    {
        var vm = new DeleteDialogViewModel([File("a"), File("b"), File("c")], recycleCount: 1);

        vm.IsMixed.Should().BeTrue();
        vm.RecycleBinNote.Should().Contain("Delete_NoteMixed");
        vm.WarnsPermanent.Should().BeTrue();
        vm.DeleteIsDefault.Should().BeFalse();
    }

    [Fact]
    public void DeleteDialog_ListsAtMostTenNamesAndCountsTheRest()
    {
        var files = Enumerable.Range(0, 14).Select(i => File($"f{i}.txt")).ToList();

        var vm = new DeleteDialogViewModel(files, 14);

        vm.Preview.Should().HaveCount(DeleteDialogViewModel.PreviewLimit + 1);
        vm.Preview.Last().Should().Contain("Delete_More");
    }

    [Fact]
    public void DeleteDialog_Folders_GetTheirOwnWarningAndATrailingSlash()
    {
        var vm = new DeleteDialogViewModel([File("src", directory: true)], 1);

        vm.HasFolderNote.Should().BeTrue();
        vm.Preview.Should().Equal("src\\");
    }

    [Fact]
    public void DeleteDialog_RecycleCountBeyondTheItems_IsClamped()
    {
        var vm = new DeleteDialogViewModel([File("a")], recycleCount: 9);

        vm.RecycleCount.Should().Be(1);
    }

    // ── Recycle Bin availability ──────────────────────────────────────────────

    [Fact]
    public void RecycleBin_NetworkPath_HasNoBin()
    {
        RecycleBin.IsAvailableForWindowsPath(@"\\server\share\file.txt").Should().BeFalse();
    }

    [Fact]
    public void RecycleBin_PathBeyondTheShellLimit_HasNoBin()
    {
        RecycleBin.IsAvailableForWindowsPath(@"C:\" + new string('a', 300)).Should().BeFalse();
    }

    [Fact]
    public void RecycleBin_AFileSystemThatIsNotTheDisk_HasNoBin()
    {
        RecycleBin.IsAvailableFor(new InMemoryFileSystem("file"), new VfsPath("/C:/x")).Should().BeFalse();
        RecycleBin.CountRecyclable(new InMemoryFileSystem("file"), [File("a")]).Should().Be(0);
    }

    [Fact]
    public void RecycleBin_ARelativeOrEmptyPath_HasNoBin()
    {
        RecycleBin.IsAvailableForWindowsPath("").Should().BeFalse();
    }

    // ── Conflict dialog ───────────────────────────────────────────────────────

    [Fact]
    public void ConflictDialog_ShowsWhichSideIsNewer()
    {
        var older = File("x.txt", modified: new DateTime(2020, 1, 1));
        var newer = File("x.txt", modified: new DateTime(2025, 1, 1));

        new ConflictDialogViewModel(newer, older).Hint.Should().Contain("Conflict_SourceNewer");
        new ConflictDialogViewModel(older, newer).Hint.Should().Contain("Conflict_SourceOlder");
        new ConflictDialogViewModel(older, older).Hint.Should().Contain("Conflict_SameTime");
    }

    [Fact]
    public void ConflictDialog_ForFolders_HidesSizeAndDateAndUsesTheFolderHeadline()
    {
        var vm = new ConflictDialogViewModel(File("d", directory: true), File("d", directory: true));

        vm.ShowSizeAndDate.Should().BeFalse();
        vm.Headline.Should().Contain("Conflict_HeadlineFolder");
        vm.Hint.Should().BeEmpty();
    }

    [Fact]
    public void ConflictDialog_ShowsTheFullPathsOfBothSides()
    {
        var vm = new ConflictDialogViewModel(File("x.txt"), File("x.txt"));

        vm.SourcePath.Should().Be("/x/x.txt");
        vm.DestPath.Should().Be("/x/x.txt");
    }

    // ── Names ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("readme.md", null)]
    [InlineData(".gitignore", null)]
    [InlineData("my file (1).txt", null)]
    [InlineData("", null)]
    [InlineData("a:b", "Name_InvalidChars")]
    [InlineData("a/b", "Name_InvalidChars")]
    [InlineData("what?", "Name_InvalidChars")]
    [InlineData(".", "Name_Dots")]
    [InlineData("..", "Name_Dots")]
    [InlineData("trailing.", "Name_TrailingDot")]
    [InlineData("trailing ", "Name_TrailingDot")]
    [InlineData("CON", "Name_Reserved")]
    [InlineData("nul.txt", "Name_Reserved")]
    [InlineData("Com1", "Name_Reserved")]
    public void FileNameRules_GetErrorKey_NamesTheProblem(string name, string? expected)
    {
        FileNameRules.GetErrorKey(name).Should().Be(expected);
    }

    [Fact]
    public void FileNameRules_IsValid_RejectsEmptyAndBadNames()
    {
        FileNameRules.IsValid("ok.txt").Should().BeTrue();
        FileNameRules.IsValid("  ").Should().BeFalse();
        FileNameRules.IsValid("a|b").Should().BeFalse();
    }

    [Fact]
    public void RenameDialog_SameNameOrBadName_CannotBeConfirmed()
    {
        var vm = new RenameDialogViewModel("a.txt");
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse("nothing changed");

        vm.NewName = "b.txt";
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();

        vm.NewName = "b:txt";
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
        vm.HasValidationMessage.Should().BeTrue();
    }

    [Fact]
    public void NewFolderDialog_ReservedName_CannotBeConfirmed()
    {
        var vm = new NewFolderDialogViewModel { FolderName = "aux" };

        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
        vm.HasValidationMessage.Should().BeTrue();
    }

    [Fact]
    public void InputDialog_EmptyValue_CannotBeConfirmed()
    {
        var vm = new InputDialogViewModel("t", "p", "");
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();

        vm.Value = "name";
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
    }

    // ── Copy dialog ───────────────────────────────────────────────────────────

    [Fact]
    public void CopyDialog_ByDefault_PreservesTimestamps()
    {
        var vm = new CopyDialogViewModel([File("a")], "/dest", isMove: false);

        vm.BuildOptions().PreserveTimestamps.Should().BeTrue();
    }

    [Fact]
    public void CopyDialog_ForAMove_HidesAndIgnoresTheCopyOnlyOptions()
    {
        var vm = new CopyDialogViewModel([File("a")], "/dest", isMove: true)
        {
            OnlyIfNewer = true,
            ParallelCopy = true,
            ConflictModeSkip = true,
        };

        vm.ShowCopyOptions.Should().BeFalse();
        var options = vm.BuildOptions();
        options.OnlyIfNewer.Should().BeFalse();
        options.Threads.Should().Be(1);
        options.ConflictMode.Should().Be(ConflictMode.Skip, "the conflict answer still applies to a move");
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(6, 6)]
    [InlineData(500, CopyDialogViewModel.MaxThreads)]
    public void CopyDialog_Threads_AreKeptInRange(int typed, int expected)
    {
        var vm = new CopyDialogViewModel([File("a")], "/dest", isMove: false) { ParallelCopy = true, Threads = typed };

        vm.BuildOptions().Threads.Should().Be(expected);
    }

    // ── Clipboard buffer ──────────────────────────────────────────────────────

    [Fact]
    public void ClipboardBuffer_WhenTheSystemClipboardMovedOn_IsStale()
    {
        var buffer = new ClipboardBufferService();
        buffer.SetCopy([new ClipboardEntry("C:\\a.txt", "a.txt", false, 1, DateTime.Now)]);
        buffer.Stamp = 41;

        buffer.IsStale(41).Should().BeFalse();
        buffer.IsStale(42).Should().BeTrue("another program copied something after TCV filled its buffer");
    }

    [Fact]
    public void ClipboardBuffer_WithoutARecordedStamp_IsNeverStale()
    {
        var buffer = new ClipboardBufferService();
        buffer.SetCut([new ClipboardEntry("C:\\a.txt", "a.txt", false, 1, DateTime.Now)]);

        buffer.IsStale(99).Should().BeFalse();
    }

    [Fact]
    public void ClipboardBuffer_Clear_ForgetsTheStamp()
    {
        var buffer = new ClipboardBufferService();
        buffer.SetCopy([new ClipboardEntry("C:\\a.txt", "a.txt", false, 1, DateTime.Now)]);
        buffer.Stamp = 5;

        buffer.Clear();

        buffer.Stamp.Should().Be(0);
        buffer.HasEntries.Should().BeFalse();
        buffer.IsStale(6).Should().BeFalse("an empty buffer has nothing to be stale");
    }

    // ── Enter on files ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("backup.zip", true)]
    [InlineData("BACKUP.ZIP", true)]
    [InlineData("release.tar.gz", true)]
    [InlineData("disk.iso", true)]
    [InlineData("report.docx", false)]
    [InlineData("book.xlsx", false)]
    [InlineData("app.jar", false)]
    [InlineData("tool.exe", false)]
    [InlineData("noextension", false)]
    public void KnownArchiveExtensions_OnlyRealArchivesOpenAsFolders(string name, bool expected)
    {
        KnownArchiveExtensions.IsArchiveFileName(name).Should().Be(expected);
    }

    // ── Operation log labels ──────────────────────────────────────────────────

    [Theory]
    [InlineData(OperationLogDescriptor.RecycleBinToken, "[Log_RecycleBin]")]
    [InlineData("Корзина", "[Log_RecycleBin]")]
    [InlineData(@"D:\backup", @"D:\backup")]
    [InlineData(null, "")]
    public void OperationLogRow_DestinationLabel_LocalisesTheRecycleBinToken(string? destination, string expected)
    {
        OperationLogRow.DestinationLabel(destination).Should().Be(expected);
    }
}
