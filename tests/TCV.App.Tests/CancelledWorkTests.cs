using System.IO;
using FluentAssertions;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>
/// What the UI thread's last-chance handler keeps quiet about: cancelled work nobody awaited, such as
/// an older FluentFTP's own close on a cancelled read (TRAP-93) — but not a timeout that only looks like one.
/// </summary>
public class CancelledWorkTests
{
    [Fact]
    public void IsCancellation_CancelledSocketRead_IsTrue()
    {
        CancelledWork.IsCancellation(new OperationCanceledException("Cancelled read from socket stream"))
            .Should().BeTrue();
    }

    [Fact]
    public void IsCancellation_CancelledTask_IsTrue()
    {
        CancelledWork.IsCancellation(new TaskCanceledException()).Should().BeTrue();
    }

    [Fact]
    public void IsCancellation_HttpClientTimeout_IsFalse()
    {
        var timeout = new TaskCanceledException(
            "The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.",
            new TimeoutException());

        CancelledWork.IsCancellation(timeout).Should().BeFalse("a server that did not answer is a failure to report");
    }

    [Fact]
    public void IsCancellation_Failure_IsFalse()
    {
        CancelledWork.IsCancellation(new IOException("The network name is no longer available.")).Should().BeFalse();
    }
}
