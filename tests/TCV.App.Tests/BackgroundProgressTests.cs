using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// The operations whose progress window was sent to the background (ADR-0038): what the status bar
/// counts, how far it says they have got, what its list shows, and the one call that brings their
/// windows back.
/// </summary>
public class BackgroundProgressTests
{
    /// <summary>Started by the test, as the queue's worker would start it, or run by a queue until told to finish.</summary>
    private sealed class ManualOperation(OperationLane lane = OperationLane.Local) : FileOperationBase
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finish = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override OperationLane Lane => lane;

        public Task Started => _started.Task;

        public void Start()
        {
            State = OperationState.Running;
            _started.TrySetResult();
        }

        public void Finish() => _finish.TrySetResult();

        public override async Task ExecuteAsync(CancellationToken ct)
        {
            Start();
            await _finish.Task.WaitAsync(ct);
            State = OperationState.Completed;
        }
    }

    [Fact]
    public void Add_TellsWhichWindowWasHidden()
    {
        var background = new BackgroundProgress();
        var progress = new ProgressDialogViewModel("Copying");
        ProgressDialogViewModel? hidden = null;
        background.Hidden += (_, p) => hidden = p;

        background.Add(progress);

        hidden.Should().BeSameAs(progress);
        background.Items.Should().Equal(progress);
    }

    [Fact]
    public void End_HiddenOperation_LeavesTheListAndSaysItEnded()
    {
        var background = new BackgroundProgress();
        var progress = new ProgressDialogViewModel("Copying");
        background.Add(progress);
        ProgressDialogViewModel? ended = null;
        var changes = 0;
        background.Ended += (_, p) => ended = p;
        background.Changed += (_, _) => changes++;

        background.End(progress);

        ended.Should().BeSameAs(progress);
        changes.Should().Be(1);
        background.Items.Should().BeEmpty();
    }

    [Fact]
    public void End_WindowThatWasNotHidden_SaysNothing()
    {
        var background = new BackgroundProgress();
        var ended = false;
        background.Ended += (_, _) => ended = true;

        background.End(new ProgressDialogViewModel("Copying"));

        ended.Should().BeFalse();
    }

    [Fact]
    public void Remove_WindowShownAgain_IsNotAnEnd()
    {
        var background = new BackgroundProgress();
        var progress = new ProgressDialogViewModel("Copying");
        background.Add(progress);
        var ended = false;
        background.Ended += (_, _) => ended = true;

        background.Remove(progress);

        ended.Should().BeFalse();
        background.Count.Should().Be(0);
    }

    [Fact]
    public void IsWaiting_UntilTheQueueStartsTheOperation()
    {
        var progress = new ProgressDialogViewModel("Copying");
        var op = new ManualOperation();
        progress.Attach(op);

        progress.IsWaiting.Should().BeTrue();

        op.Start();

        progress.IsWaiting.Should().BeFalse("the window hears the start from the operation, nobody has to ask");
    }

    [Fact]
    public void IsWaiting_WindowWithoutAnOperation_IsFalse()
    {
        new ProgressDialogViewModel("Packing").IsWaiting.Should().BeFalse();
    }

    [Theory]
    [InlineData(OperationLane.Network, "[Dlg_Progress_WaitingServer]")]
    [InlineData(OperationLane.Local, "[Dlg_Progress_WaitingLocal]")]
    public void WaitingText_NamesTheLaneItWaitsIn(OperationLane lane, string key)
    {
        var progress = new ProgressDialogViewModel("Copying");

        progress.Attach(new ManualOperation(lane));

        progress.WaitingText.Should().Be(key);
    }

    [Fact]
    public async Task IsWaiting_BehindAnotherServerOperation_UntilThatOneEnds()
    {
        using var queue = new OperationQueue(NullLogger<OperationQueue>.Instance);
        var ahead = new ManualOperation(OperationLane.Network);
        var behind = new ManualOperation(OperationLane.Network);
        var progress = new ProgressDialogViewModel("Copying");
        progress.Attach(behind);

        var aheadDone = queue.EnqueueAsync(ahead);
        await ahead.Started.WaitAsync(TimeSpan.FromSeconds(3));
        var behindDone = queue.EnqueueAsync(behind);
        await Task.Delay(100);

        progress.IsWaiting.Should().BeTrue("the server lane runs one operation at a time");

        ahead.Finish();
        await behind.Started.WaitAsync(TimeSpan.FromSeconds(3));

        progress.IsWaiting.Should().BeFalse();

        behind.Finish();
        await Task.WhenAll(aheadDone, behindDone).WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void Cancel_FromTheList_CancelsTheAttachedOperation()
    {
        var progress = new ProgressDialogViewModel("Copying");
        var op = new ManualOperation();
        progress.Attach(op);

        progress.CancelCommand.Execute(null);

        op.Cts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void SendToBackground_IsWhatTheWindowAndTheListFollow()
    {
        var progress = new ProgressDialogViewModel("Copying");

        progress.SendToBackgroundCommand.Execute(null);
        progress.IsInBackground.Should().BeTrue();

        progress.BringBack();
        progress.IsInBackground.Should().BeFalse();
    }

    [Fact]
    public void Add_SameOperationTwice_CountsItOnce()
    {
        var background = new BackgroundProgress();
        var progress = new ProgressDialogViewModel("Copying");
        var changes = 0;
        background.Changed += (_, _) => changes++;

        background.Add(progress);
        background.Add(progress);

        background.Count.Should().Be(1);
        changes.Should().Be(1);
    }

    [Fact]
    public void Remove_AnOperationThatIsNotThere_ChangesNothing()
    {
        var background = new BackgroundProgress();
        var changes = 0;
        background.Changed += (_, _) => changes++;

        background.Remove(new ProgressDialogViewModel("Copying"));

        changes.Should().Be(0);
    }

    [Fact]
    public void Percent_IsTheMeanOfTheOperations()
    {
        var background = new BackgroundProgress();
        background.Add(new ProgressDialogViewModel("Copying") { Percentage = 20 });
        background.Add(new ProgressDialogViewModel("Deleting") { Percentage = 71 });

        background.Percent.Should().Be(46);
    }

    [Fact]
    public void Percent_NothingInTheBackground_IsZero()
    {
        new BackgroundProgress().Percent.Should().Be(0);
    }

    [Fact]
    public void BringBackAll_ShowsEveryHiddenWindow()
    {
        var background = new BackgroundProgress();
        var copy = new ProgressDialogViewModel("Copying");
        var delete = new ProgressDialogViewModel("Deleting");
        copy.SendToBackgroundCommand.Execute(null);
        delete.SendToBackgroundCommand.Execute(null);
        background.Add(copy);
        background.Add(delete);

        var shown = background.BringBackAll();

        shown.Should().Be(2);
        copy.IsInBackground.Should().BeFalse();
        delete.IsInBackground.Should().BeFalse();
    }
}
