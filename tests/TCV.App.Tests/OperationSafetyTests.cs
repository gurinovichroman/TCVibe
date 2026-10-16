using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Guards found by the feature audit: each one is a way an ordinary keystroke used to lose data or
/// leave an operation running with no way to stop it.
/// </summary>
public class OperationSafetyTests
{
    [Fact]
    public async Task Move_OntoItselfWithOverwrite_LeavesTheFolderAlone()
    {
        // Both panels on the same folder is the default layout: F6, Enter, then Overwrite as the
        // default answer used to delete the "destination" — which was the source.
        var fs = new InMemoryFileSystem();
        fs.AddDirectory("/proj");
        fs.AddFile("/proj/a.txt", [1, 2, 3]);
        var dir = (await fs.GetEntryAsync(new VfsPath("/proj")))!;

        var op = new MoveOperation(fs, fs, [dir], new VfsPath("/"),
            new OverwriteStrategy(), NullLogger<MoveOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        fs.HasDirectory("/proj").Should().BeTrue();
        fs.HasFile("/proj/a.txt").Should().BeTrue();
        fs.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Copy_SkippedItem_IsCountedSoCutDoesNotDeleteTheSource()
    {
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);
        var source = (await fs.GetEntryAsync(new VfsPath("/a/x.txt")))!;

        var op = new CopyOperation(fs, fs, [source], new VfsPath("/b"),
            new SkipStrategy(), NullLogger<CopyOperation>.Instance);
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        op.SkippedCount.Should().Be(1);
        fs.ReadFile("/b/x.txt").Should().Equal(2);
    }

    [Fact]
    public void LinkCancellation_CancellingTheDialogToken_CancelsTheOperation()
    {
        var fs = new InMemoryFileSystem();
        var op = new CopyOperation(fs, fs, [], new VfsPath("/b"),
            new SkipStrategy(), NullLogger<CopyOperation>.Instance);
        using var dialogCts = new CancellationTokenSource();

        op.LinkCancellation(dialogCts.Token);
        op.Cts.IsCancellationRequested.Should().BeFalse();
        dialogCts.Cancel();

        op.Cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ConflictDialog_ClosedWithoutAnAnswer_ResolvesToCancel()
    {
        // X and Alt+F4 never reach a command; the enum's zero value is Overwrite.
        var fs = new InMemoryFileSystem();
        fs.AddFile("/a/x.txt", [1]);
        fs.AddFile("/b/x.txt", [2]);
        var source = (await fs.GetEntryAsync(new VfsPath("/a/x.txt")))!;
        var existing = (await fs.GetEntryAsync(new VfsPath("/b/x.txt")))!;

        var vm = new ConflictDialogViewModel(source, existing);

        vm.Resolution.Should().Be(ConflictResolution.Cancel);
    }
}
