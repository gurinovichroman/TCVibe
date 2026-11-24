using System.IO;
using FluentAssertions;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;

namespace TCV.App.Tests;

/// <summary>Enter and F4 on a file of a server: in the background, with progress, leaving nothing behind (ADR-0038).</summary>
public sealed class RemoteFetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-fetch-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryFileSystem _server = new("ftp");

    public RemoteFetchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup.
        }
    }

    private static byte[] Payload(int size) => Enumerable.Range(0, size).Select(i => (byte)(i % 251)).ToArray();

    private sealed class Sink : IProgress<FileProgress>
    {
        public List<FileProgress> Reports { get; } = [];

        public void Report(FileProgress value)
        {
            lock (Reports)
            {
                Reports.Add(value);
            }
        }
    }

    [Fact]
    public async Task CopyToFileAsync_WritesTheFileAndSaysHowFar()
    {
        var data = Payload(500_000);
        _server.AddFile("/a/big.bin", data);
        var target = Path.Combine(_dir, "big.bin");
        var sink = new Sink();

        await RemoteFetch.CopyToFileAsync(_server, new VfsPath("/a/big.bin"), target, data.Length, "big.bin", sink, CancellationToken.None);

        File.ReadAllBytes(target).Should().Equal(data);
        sink.Reports[^1].BytesCopied.Should().Be(data.Length);
        sink.Reports[^1].TotalBytes.Should().Be(data.Length);
        sink.Reports[^1].CurrentFile.Should().Be("big.bin");
    }

    [Fact]
    public async Task CopyToFileAsync_SizeNotKnown_TheTotalFollowsWhatCame()
    {
        _server.AddFile("/a/b.bin", Payload(1000));
        var sink = new Sink();

        await RemoteFetch.CopyToFileAsync(_server, new VfsPath("/a/b.bin"), Path.Combine(_dir, "b.bin"), 0, "b.bin", sink, CancellationToken.None);

        sink.Reports[^1].Percentage.Should().Be(100);
    }

    [Fact]
    public async Task CopyToFileAsync_Cancelled_RemovesThePartialFile()
    {
        _server.AddFile("/a/big.bin", Payload(1_000_000));
        var target = Path.Combine(_dir, "big.bin");
        using var cancel = new CancellationTokenSource();
        _server.OnFirstRead = cancel.Cancel;

        var fetch = () => RemoteFetch.CopyToFileAsync(_server, new VfsPath("/a/big.bin"), target, 0, "big.bin", null, cancel.Token);

        await fetch.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task CopyToFileAsync_ServerFails_RemovesThePartialFileAndSaysWhy()
    {
        var target = Path.Combine(_dir, "gone.bin");

        var fetch = () => RemoteFetch.CopyToFileAsync(_server, new VfsPath("/a/gone.bin"), target, 0, "gone.bin", null, CancellationToken.None);

        await fetch.Should().ThrowAsync<Exception>();
        File.Exists(target).Should().BeFalse();
    }

    [Fact]
    public async Task CopyToFileAsync_RunsOnThePool()
    {
        // A server's open is a connection: not on the thread that called (INV-06).
        var callerThread = Environment.CurrentManagedThreadId;
        int? openThread = null;
        var fs = new Moq.Mock<TCV.Core.Interfaces.IVirtualFileSystem>();
        fs.Setup(v => v.OpenReadAsync(Moq.It.IsAny<VfsPath>(), Moq.It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                openThread = Environment.CurrentManagedThreadId;
                return Task.FromResult<Stream>(new MemoryStream([1, 2, 3]));
            });

        await RemoteFetch.CopyToFileAsync(fs.Object, new VfsPath("/x"), Path.Combine(_dir, "x"), 0, "x", null, CancellationToken.None);

        openThread.Should().NotBeNull();
        openThread.Should().NotBe(callerThread);
    }

    [Fact]
    public void TryBegin_SameFileAlreadyOnItsWay_GivesNoSecondTicket()
    {
        var tracker = new RemoteFetchTracker();
        var path = new VfsPath("/a/b.txt");

        var first = tracker.TryBegin(_server, path, RemoteFetchPurpose.Open);

        first.Should().NotBeNull();
        tracker.TryBegin(_server, path, RemoteFetchPurpose.Open).Should().BeNull();
        tracker.Find(_server, path, RemoteFetchPurpose.Open).Should().BeSameAs(first);
    }

    [Fact]
    public void TryBegin_OverTheFirstFetch_AnotherFileOrPurposeOrServerGoesAhead()
    {
        var tracker = new RemoteFetchTracker();
        var path = new VfsPath("/a/b.txt");
        tracker.TryBegin(_server, path, RemoteFetchPurpose.Open);

        tracker.TryBegin(_server, new VfsPath("/a/c.txt"), RemoteFetchPurpose.Open).Should().NotBeNull();
        tracker.TryBegin(_server, path, RemoteFetchPurpose.Edit).Should().NotBeNull();
        tracker.TryBegin(new InMemoryFileSystem("ftp"), path, RemoteFetchPurpose.Open).Should().NotBeNull();
    }

    [Fact]
    public void Dispose_TheFetchIsOver_TheNextEnterFetchesAgain()
    {
        var tracker = new RemoteFetchTracker();
        var path = new VfsPath("/a/b.txt");

        tracker.TryBegin(_server, path, RemoteFetchPurpose.Open)!.Dispose();

        tracker.Count.Should().Be(0);
        tracker.TryBegin(_server, path, RemoteFetchPurpose.Open).Should().NotBeNull();
    }

    [Fact]
    public void CancelAll_Exit_StopsEveryFetch()
    {
        var tracker = new RemoteFetchTracker();
        var one = tracker.TryBegin(_server, new VfsPath("/a"), RemoteFetchPurpose.Open)!;
        var two = tracker.TryBegin(_server, new VfsPath("/b"), RemoteFetchPurpose.Edit)!;

        tracker.CancelAll();

        one.Token.IsCancellationRequested.Should().BeTrue();
        two.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Attach_TheWindowsCancelStopsTheFetch()
    {
        var ticket = new RemoteFetchTracker().TryBegin(_server, new VfsPath("/a"), RemoteFetchPurpose.Open)!;
        var window = new ProgressDialogViewModel("t");

        ticket.Attach(window);
        window.CancelCommand.Execute(null);

        ticket.Token.IsCancellationRequested.Should().BeTrue();
        ticket.Window.Should().BeSameAs(window);
        window.CanPause.Should().BeFalse("nothing here pauses, and a Pause button would promise that it does");
    }

    [Fact]
    public void Attach_WindowClosedAfterTheFetchEnded_DoesNotThrow()
    {
        var ticket = new RemoteFetchTracker().TryBegin(_server, new VfsPath("/a"), RemoteFetchPurpose.Open)!;
        var window = new ProgressDialogViewModel("t");
        ticket.Attach(window);

        ticket.Dispose();

        // A window closes after its fetch, and closing it cancels (ProgressDialog.Closed → AbortOnClose).
        var close = window.AbortOnClose;
        close.Should().NotThrow();
    }
}
