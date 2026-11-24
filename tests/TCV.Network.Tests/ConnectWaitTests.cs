using System.IO;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

/// <summary>Cancel works at once, also on a connect that cannot hear it, and what that connect brings back is closed.</summary>
public class ConnectWaitTests
{
    private static TaskCompletionSource<string> Step() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task UntilCancelled_StepDeafToTheToken_EndsAtOnce_AndWhatItBringsBackIsDiscarded()
    {
        using var cts = new CancellationTokenSource();
        var step = Step();
        var discarded = Step();

        var waiting = ConnectWait.UntilCancelledAsync(step.Task, cts.Token, discarded.SetResult);
        cts.Cancel();

        await FluentActions.Awaiting(() => waiting.WaitAsync(TimeSpan.FromSeconds(10)))
            .Should().ThrowAsync<OperationCanceledException>();
        discarded.Task.IsCompleted.Should().BeFalse("the step still runs, and still uses what it opened");

        step.SetResult("connection");

        (await discarded.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("connection");
    }

    [Fact]
    public async Task UntilCancelled_AbandonedStepFails_NothingToDiscard()
    {
        using var cts = new CancellationTokenSource();
        var step = Step();
        var discarded = 0;

        var waiting = ConnectWait.UntilCancelledAsync(step.Task, cts.Token, _ => discarded++);
        cts.Cancel();
        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();

        step.SetException(new IOException("timed out"));
        await Task.Delay(50);

        discarded.Should().Be(0);
    }

    [Fact]
    public async Task UntilCancelled_StepThatHearsTheToken_IsCancelled()
    {
        using var cts = new CancellationTokenSource();
        var discarded = 0;

        var waiting = ConnectWait.UntilCancelledAsync(
            Task.Delay(Timeout.Infinite, cts.Token).ContinueWith(_ => "never", TaskContinuationOptions.OnlyOnRanToCompletion),
            cts.Token,
            _ => discarded++);
        cts.Cancel();

        await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
        discarded.Should().Be(0);
    }

    [Fact]
    public async Task UntilCancelled_StepEnds_ItsResultIsTheCallers()
    {
        using var cts = new CancellationTokenSource();
        var discarded = 0;

        var result = await ConnectWait.UntilCancelledAsync(Task.FromResult("connection"), cts.Token, _ => discarded++);

        result.Should().Be("connection");
        discarded.Should().Be(0);
    }

    [Fact]
    public async Task UntilCancelled_StepFails_TheFailureIsTheCallers()
    {
        using var cts = new CancellationTokenSource();

        await FluentActions
            .Awaiting(() => ConnectWait.UntilCancelledAsync(Task.FromException<string>(new IOException("refused")), cts.Token))
            .Should().ThrowAsync<IOException>();
    }
}
