using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Closing with F4 edits on their way (TRAP-105): the wait must not hold the UI thread, no edit may be
/// lost whichever way it ends, and stopping it says where the edits are.
/// </summary>
public sealed class RemoteEditFlushTests : IDisposable
{
    private static readonly UTF8Encoding NoBom = new(false);

    private readonly string _copies = Path.Combine(Path.GetTempPath(), "tcv-edit-tests-" + Guid.NewGuid().ToString("N"));

    private readonly RemoteEditService _service;

    private RemoteEditService NewService() => new(NullLogger<RemoteEditService>.Instance, _ => true)
    {
        CopiesRoot = _copies,
        // Long enough that the flush, not the watcher, starts the upload.
        SettleDelay = TimeSpan.FromSeconds(30),
        RetryDelays = [TimeSpan.FromMilliseconds(10)],
    };

    private readonly List<RemoteEditEventArgs> _events = [];
    private readonly GatedServer _server = new();

    public RemoteEditFlushTests()
    {
        Directory.CreateDirectory(_copies);
        _service = NewService();
        _service.Uploaded += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }
        };
    }

    public void Dispose()
    {
        _service.Dispose();
        try
        {
            Directory.Delete(_copies, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An editor stand-in or a scanner holds a file: not this test's failure.
        }
    }

    private async Task<RemoteEditSession> EditedSessionAsync(string name = "app.conf")
    {
        var path = "/etc/" + name;
        _server.Inner.AddFile(path, Encoding.UTF8.GetBytes("original"));
        var session = await _service.BeginAsync(_server, new VfsPath(path), name);
        session.Should().NotBeNull();
        File.WriteAllText(session!.LocalPath, "edited", NoBom);
        return session;
    }

    [Fact]
    public async Task HasPendingUploads_ASaveNotYetUploaded_IsTrue()
    {
        var session = await EditedSessionAsync();

        _service.HasPendingUploads.Should().BeTrue();
        _service.UnuploadedCopies().Should().Equal(session.LocalPath);

        (await _service.FlushAllAsync()).Should().BeTrue();

        _service.HasPendingUploads.Should().BeFalse();
        Encoding.UTF8.GetString(_server.Inner.ReadFile("/etc/app.conf")).Should().Be("edited");
        session.ServerCopyInDoubt.Should().BeFalse();
    }

    [Fact]
    public async Task FlushAllAsync_ServerKeepsItWaiting_ComesBackToTheCallerAtOnce()
    {
        await EditedSessionAsync();
        _server.Hold();

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var flush = _service.FlushAllAsync();
        clock.Stop();

        // The window's thread gets the task back while the bytes wait: it can go on pumping messages.
        clock.ElapsedMilliseconds.Should().BeLessThan(1000);
        flush.IsCompleted.Should().BeFalse();

        _server.Release();
        (await flush.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue();
        Encoding.UTF8.GetString(_server.Inner.ReadFile("/etc/app.conf")).Should().Be("edited");
    }

    [Fact]
    public async Task FlushAllAsync_Stopped_EndsAtOnceAndNoEditIsLost()
    {
        var session = await EditedSessionAsync();
        _server.Hold();
        using var stop = new CancellationTokenSource();

        var flush = _service.FlushAllAsync(null, stop.Token);
        await _server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();

        // The server still holds the upload: the exit does not wait for its stream to close.
        (await flush.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeFalse();

        session.Dispose();

        session.KeptLocalCopy.Should().BeTrue();
        File.ReadAllText(session.LocalPath).Should().Be("edited");
        session.ServerCopyInDoubt.Should().BeTrue("the exit warns that the server may hold part of the file");

        _server.Release();
    }

    [Fact]
    public async Task FlushAllAsync_Stopped_IsNotPublishedByAStreamThatCommitsOnClose()
    {
        await EditedSessionAsync();
        _server.Hold();
        using var stop = new CancellationTokenSource();

        var flush = _service.FlushAllAsync(null, stop.Token);
        await _server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await flush.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForEventAsync(RemoteEditOutcome.Interrupted);
        (await _server.Stream.Task.WaitAsync(TimeSpan.FromSeconds(5))).Aborted.Should().BeTrue();
    }

    [Fact]
    public async Task FlushAllAsync_Stopped_IsReportedAsInterrupted()
    {
        await EditedSessionAsync();
        _server.Hold();
        using var stop = new CancellationTokenSource();

        var flush = _service.FlushAllAsync(null, stop.Token);
        await _server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await flush;

        await WaitForEventAsync(RemoteEditOutcome.Interrupted);
        _service.IsClosing.Should().BeTrue("while closing, the per-save dialogs stay quiet: one summary names the copies");
    }

    private async Task WaitForEventAsync(RemoteEditOutcome outcome)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            lock (_events)
            {
                if (_events.Any(e => e.Outcome == outcome))
                {
                    return;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("No " + outcome + " outcome was reported.");
    }

    [Fact]
    public async Task ResumeAfterFlush_AfterAStop_UploadsAndAsksAgain()
    {
        var session = await EditedSessionAsync();
        _server.Hold();
        using var stop = new CancellationTokenSource();
        var flush = _service.FlushAllAsync(null, stop.Token);
        await _server.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await stop.CancelAsync();
        await flush;
        _server.Release();
        await WaitForEventAsync(RemoteEditOutcome.Interrupted);

        // An update refused because of this: the window stays open, and the edits still have to go up.
        _service.ResumeAfterFlush();
        _service.IsClosing.Should().BeFalse();

        await session.FlushAsync();

        Encoding.UTF8.GetString(_server.Inner.ReadFile("/etc/app.conf")).Should().Be("edited");
        session.HasUnuploadedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task FlushAllAsync_UploadFails_TheCopyStaysAndIsNamed()
    {
        var session = await EditedSessionAsync();
        _server.FailWrites = true;

        (await _service.FlushAllAsync()).Should().BeTrue("it ended by itself; the failure is the session's to tell");

        _service.UnuploadedCopies().Should().Equal(session.LocalPath);
        session.Dispose();
        session.KeptLocalCopy.Should().BeTrue();
    }

    [Fact]
    public async Task ServerCopyInDoubt_AWriteThatGotNowhere_StaysUpUntilOneIsConfirmed()
    {
        var session = await EditedSessionAsync();
        _server.TruncateWrites = true;

        await session.FlushAsync();
        session.ServerCopyInDoubt.Should().BeTrue("the server holds part of the file now");

        _server.TruncateWrites = false;
        File.WriteAllText(session.LocalPath, "edited again", NoBom);
        await session.FlushAsync();

        session.ServerCopyInDoubt.Should().BeFalse();
    }

    [Fact]
    public async Task FlushAllAsync_ReportsWhatIsOnItsWayAndHowMany()
    {
        await EditedSessionAsync("a.conf");
        await EditedSessionAsync("b.conf");
        var reports = new List<RemoteEditFlushProgress>();

        await _service.FlushAllAsync(new InlineProgress(reports));

        lock (reports)
        {
            reports[0].Total.Should().Be(2);
            reports[0].Current.Should().NotBeEmpty();
            reports[^1].Done.Should().Be(2);
        }
    }

    private sealed class InlineProgress(List<RemoteEditFlushProgress> sink) : IProgress<RemoteEditFlushProgress>
    {
        public void Report(RemoteEditFlushProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }

    [Fact]
    public async Task FlushAll_OneThatCannotBePutOff_StopsAtItsLimitAndKeepsTheCopy()
    {
        var session = await EditedSessionAsync();
        _server.Hold();

        // Windows is shutting down: this one does block, but no longer than it says.
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _service.FlushAll(TimeSpan.FromMilliseconds(200));
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));

        session.Dispose();
        session.KeptLocalCopy.Should().BeTrue();
        File.ReadAllText(session.LocalPath).Should().Be("edited");
        _server.Release();
    }

    [Fact]
    public void WindowViewModel_Stop_ClosesAndCancels()
    {
        var vm = new RemoteEditFlushDialogViewModel();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.StopCommand.Execute(null);

        closed.Should().Be(1);
        vm.StopToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).Should().BeTrue();
    }

    [Fact]
    public void WindowViewModel_Report_NamesTheFileAndCountsOnlySeveral()
    {
        var vm = new RemoteEditFlushDialogViewModel();

        vm.Report(new RemoteEditFlushProgress(0, 1, "a.conf"));
        vm.CurrentFile.Should().Be("a.conf");
        vm.CountText.Should().BeEmpty();

        vm.Report(new RemoteEditFlushProgress(1, 3, "b.conf"));
        vm.CountText.Should().Be("2 / 3");
    }

    // ── F4: the fetch before the edit ─────────────────────────────────────────

    [Fact]
    public async Task BeginAsync_Cancelled_LeavesNothingBehindAndReleasesTheLease()
    {
        // A name of its own: other tests run alongside, with folders of their own in the same place.
        var name = "cancel-" + Guid.NewGuid().ToString("N") + ".bin";
        _server.Inner.AddFile("/" + name, new byte[300_000]);
        var lease = new CountingLease();
        using var cancel = new CancellationTokenSource();
        string? folder = null;
        _server.Inner.OnFirstRead = () =>
        {
            folder = Directory.GetDirectories(_copies, "tcv-edit-*").Single(d => File.Exists(Path.Combine(d, name)));
            cancel.Cancel();
        };

        var begin = () => _service.BeginAsync(_server, new VfsPath("/" + name), name, lease, cancel.Token);

        await begin.Should().ThrowAsync<OperationCanceledException>();
        folder.Should().NotBeNull("the copy had started when it was cancelled");
        Directory.Exists(folder).Should().BeFalse();
        lease.Released.Should().Be(1);
    }

    [Fact]
    public async Task BeginAsync_ReportsTheFetch()
    {
        _server.Inner.AddFile("/big.bin", new byte[300_000]);
        var reports = new List<FileProgress>();

        var session = await _service.BeginAsync(
            _server, new VfsPath("/big.bin"), "big.bin", progress: new SyncProgress(reports));

        session.Should().NotBeNull();
        lock (reports)
        {
            reports[^1].BytesCopied.Should().Be(300_000);
            reports[^1].TotalBytes.Should().Be(300_000);
            reports[^1].CurrentFile.Should().Be("big.bin");
        }
    }

    private sealed class SyncProgress(List<FileProgress> sink) : IProgress<FileProgress>
    {
        public void Report(FileProgress value)
        {
            lock (sink)
            {
                sink.Add(value);
            }
        }
    }

    private sealed class CountingLease : IDisposable
    {
        public int Released { get; private set; }

        public void Dispose() => Released++;
    }

    /// <summary>A server whose uploads wait until released, like a slow line: the writes stop at the first byte.</summary>
    private sealed class GatedServer : IVirtualFileSystem
    {
        private TaskCompletionSource? _gate;

        public InMemoryFileSystem Inner { get; } = new("sftp");

        public bool FailWrites { get; set; }

        /// <summary>A write goes through and the server keeps nothing of it, so the size check fails.</summary>
        public bool TruncateWrites { get; set; }

        /// <summary>Completed when an upload has reached the wait.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>The stream of the upload that reached the wait.</summary>
        public TaskCompletionSource<GatedStream> Stream { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            var gate = _gate;
            _gate = null;
            gate?.TrySetResult();
        }

        public string Scheme => Inner.Scheme;

        public string DisplayName => Inner.DisplayName;

        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => Inner.ListAsync(path, ct);

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => Inner.GetEntryAsync(path, ct);

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => Inner.OpenReadAsync(path, ct);

        public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
        {
            if (FailWrites)
            {
                throw new IOException("connection reset");
            }

            if (TruncateWrites)
            {
                return new MemoryStream();
            }

            var target = await Inner.OpenWriteAsync(path, overwrite, ct);
            return _gate is { } gate ? new GatedStream(target, gate, this) : target;
        }

        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
            Inner.CopyAsync(source, destination, progress, ct);

        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => Inner.MoveAsync(source, destination, ct);

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => Inner.DeleteAsync(path, recursive, ct);

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => Inner.CreateDirectoryAsync(path, ct);

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => Inner.RenameAsync(path, newName, ct);

        public bool SupportsOperation(VfsOperation operation) => true;

        public void Dispose() => Inner.Dispose();
    }

    private sealed class GatedStream(Stream inner, TaskCompletionSource gate, GatedServer owner) : MemoryStream, IAbortableWriteStream
    {
        public bool Aborted { get; private set; }

        public void Abort() => Aborted = true;

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            owner.Stream.TrySetResult(this);
            owner.Entered.TrySetResult();
            await gate.Task.WaitAsync(cancellationToken);
            await inner.WriteAsync(buffer, cancellationToken);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask DisposeAsync()
        {
            if (!Aborted)
            {
                await inner.DisposeAsync();
            }

            await base.DisposeAsync();
        }
    }
}
