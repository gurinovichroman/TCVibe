using System.Net.Sockets;
using FluentAssertions;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.Network.Tests;

/// <summary>
/// The automatic reconnect (6.82), driven by a server that fails on demand: it pauses as the policy
/// says, stops at the first success, at a refusal, when the tries run out or when cancelled, and
/// Retry now cuts the pause short.
/// </summary>
public class ReconnectLoopTests
{
    private static readonly ReconnectPolicy ThreeTries = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)]);

    /// <summary>A server whose answers are scripted: each stat throws the next queued failure, then answers.</summary>
    private sealed class FlakyServer : IVirtualFileSystem
    {
        private readonly Queue<Exception> _failures;

        public FlakyServer(params Exception[] failures) => _failures = new Queue<Exception>(failures);

        public int Calls { get; private set; }

        public string Scheme => "sftp";
        public string DisplayName => "sftp://flaky";

        public event EventHandler<VfsChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
        {
            Calls++;
            if (_failures.Count > 0)
            {
                throw _failures.Dequeue();
            }

            return Task.FromResult<IFileEntry?>(new NetworkFileEntry(path, "/", true, 0, DateTime.Now));
        }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => throw new NotSupportedException();
        public bool SupportsOperation(VfsOperation operation) => false;
        public void Dispose() { }
    }

    private static SocketException Drop() => new((int)SocketError.ConnectionReset);

    /// <summary>A loop whose pauses are recorded instead of waited.</summary>
    private static ReconnectLoop Loop(FlakyServer server, List<TimeSpan> pauses, ReconnectPolicy? policy = null) =>
        new(policy ?? ThreeTries,
            (_, ct) => server.GetEntryAsync(new VfsPath("/srv"), ct),
            delay: (pause, _) =>
            {
                pauses.Add(pause);
                return Task.CompletedTask;
            });

    [Fact]
    public async Task RunAsync_ServerComesBackOnTheThirdTry_ConnectsAfterGrowingPauses()
    {
        var server = new FlakyServer(Drop(), Drop());
        var pauses = new List<TimeSpan>();
        var loop = Loop(server, pauses);

        var outcome = await loop.RunAsync(Drop(), CancellationToken.None);

        outcome.Should().Be(ReconnectOutcome.Connected);
        loop.Attempts.Should().Be(3);
        loop.LastError.Should().BeNull();
        pauses.Select(p => p.TotalSeconds).Should().Equal(1, 2, 5);
    }

    [Fact]
    public async Task RunAsync_ServerStaysAway_GivesUpAfterTheLastTry()
    {
        var server = new FlakyServer(Drop(), Drop(), Drop(), Drop());
        var loop = Loop(server, []);

        var outcome = await loop.RunAsync(Drop(), CancellationToken.None);

        outcome.Should().Be(ReconnectOutcome.GaveUp);
        server.Calls.Should().Be(3, "the policy allows three tries, no more");
        loop.LastError.Should().BeOfType<SocketException>();
    }

    [Fact]
    public async Task RunAsync_ChangedHostKey_StopsAtOnceWithoutAnotherTry()
    {
        var changed = new HostKeyChangedException("prod.example:22", "SHA256:old", "SHA256:new");
        var server = new FlakyServer(Drop(), changed, Drop());
        var loop = Loop(server, []);

        var outcome = await loop.RunAsync(Drop(), CancellationToken.None);

        outcome.Should().Be(ReconnectOutcome.Refused);
        server.Calls.Should().Be(2);
        loop.LastError.Should().BeSameAs(changed, "a changed key is reported as it is, never retried (TRAP-43)");
    }

    [Fact]
    public async Task RunAsync_CancelledWhileWaiting_MakesNoTry()
    {
        var server = new FlakyServer();
        using var cts = new CancellationTokenSource();
        var loop = new ReconnectLoop(ThreeTries, (_, ct) => server.GetEntryAsync(new VfsPath("/"), ct),
            delay: (pause, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        loop.Progress += p =>
        {
            if (p.IsWaiting)
            {
                cts.Cancel();
            }
        };

        var outcome = await loop.RunAsync(Drop(), cts.Token);

        outcome.Should().Be(ReconnectOutcome.Cancelled);
        server.Calls.Should().Be(0);
    }

    [Fact]
    public async Task RetryNow_CutsThePauseShort()
    {
        var server = new FlakyServer();
        ReconnectLoop? loop = null;
        loop = new ReconnectLoop(ThreeTries, (_, ct) => server.GetEntryAsync(new VfsPath("/"), ct),
            delay: (pause, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));
        loop.Progress += p =>
        {
            if (p.IsWaiting)
            {
                loop.RetryNow();
            }
        };

        var run = loop.RunAsync(Drop(), CancellationToken.None);
        var finished = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(10)));

        finished.Should().BeSameAs(run, "Retry now must not wait out the pause");
        (await run).Should().Be(ReconnectOutcome.Connected);
        server.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Progress_SaysWhichTryIsAwaitedAndWhenItStarts()
    {
        var now = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var server = new FlakyServer(Drop());
        var reports = new List<ReconnectProgress>();
        var cause = Drop();
        var loop = new ReconnectLoop(ThreeTries, (_, ct) => server.GetEntryAsync(new VfsPath("/"), ct),
            delay: (_, _) => Task.CompletedTask, utcNow: () => now);
        loop.Progress += reports.Add;

        await loop.RunAsync(cause, CancellationToken.None);

        reports.Select(r => (r.Attempt, r.IsWaiting)).Should().Equal((1, true), (1, false), (2, true), (2, false));
        reports[0].NextAttemptAt.Should().Be(now.AddSeconds(1));
        reports[0].LastError.Should().BeSameAs(cause, "until a try fails, the drop itself is the reason shown");
        reports[2].NextAttemptAt.Should().Be(now.AddSeconds(2));
        reports.Should().OnlyContain(r => r.MaxAttempts == 3);
    }
}
