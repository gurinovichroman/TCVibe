using FluentAssertions;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The operations whose progress window was sent to the background (ADR-0038): what the status bar
/// counts, how far it says they have got, and the one call that brings their windows back.
/// </summary>
public class BackgroundProgressTests
{
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
