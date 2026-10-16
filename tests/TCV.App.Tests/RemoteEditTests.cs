using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Remote editing must never lose an edit silently: outcomes are reported, an atomic save is seen, a
/// short or failed upload is retried and then kept locally, and a server copy changed by somebody else
/// is not overwritten without asking.
/// </summary>
public sealed class RemoteEditTests : IDisposable
{
    private readonly RemoteEditService _service = new(NullLogger<RemoteEditService>.Instance, _ => true)
    {
        SettleDelay = TimeSpan.FromMilliseconds(50),
        RetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
    };

    private static readonly UTF8Encoding NoBom = new(false);

    private readonly FakeServer _server = new();
    private readonly List<RemoteEditEventArgs> _events = new();
    private readonly SemaphoreSlim _signal = new(0);

    public RemoteEditTests()
    {
        _service.Uploaded += (_, e) =>
        {
            lock (_events)
            {
                _events.Add(e);
            }

            _signal.Release();
        };
    }

    public void Dispose() => _service.Dispose();

    private async Task<RemoteEditEventArgs> NextEventAsync()
    {
        (await _signal.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue("an outcome must be reported");
        lock (_events)
        {
            return _events[^1];
        }
    }

    private async Task<RemoteEditSession> BeginAsync(IDisposable? lease = null)
    {
        _server.Put("/etc/app.conf", "original");
        var session = await _service.BeginAsync(_server, new VfsPath("/etc/app.conf"), "app.conf", lease);
        session.Should().NotBeNull();
        return session!;
    }

    private static void Save(RemoteEditSession session, string text) =>
        File.WriteAllText(session.LocalPath, text, NoBom);

    // ── Reporting and content ─────────────────────────────────────────────────

    [Fact]
    public async Task Begin_FetchesTheFileIntoATemporaryCopyWithTheOriginalName()
    {
        var session = await BeginAsync();

        Path.GetFileName(session.LocalPath).Should().Be("app.conf");
        File.ReadAllText(session.LocalPath).Should().Be("original");
    }

    [Fact]
    public async Task Save_UploadsAndReportsSuccess()
    {
        var session = await BeginAsync();

        Save(session, "edited");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        outcome.Success.Should().BeTrue();
        _server.Text("/etc/app.conf").Should().Be("edited");
        session.UploadCount.Should().Be(1);
    }

    [Fact]
    public async Task AtomicSave_RenameOverTheOriginal_IsUploadedToo()
    {
        var session = await BeginAsync();
        var temp = session.LocalPath + ".tmp-swap";

        // what JetBrains "safe write" and vim do: write beside, then rename over the file
        File.WriteAllText(temp, "saved atomically", NoBom);
        File.Move(temp, session.LocalPath, overwrite: true);
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        _server.Text("/etc/app.conf").Should().Be("saved atomically");
    }

    [Fact]
    public async Task SecondSave_IsUploadedAgain()
    {
        var session = await BeginAsync();

        Save(session, "first");
        await NextEventAsync();
        Save(session, "second version");
        await NextEventAsync();

        _server.Text("/etc/app.conf").Should().Be("second version");
        session.UploadCount.Should().Be(2);
    }

    // ── Failure ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task FailedUpload_IsRetriedThenReported_AndTheLocalCopyIsKept()
    {
        var session = await BeginAsync();
        _server.FailWrites = int.MaxValue;

        Save(session, "precious edit");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Failed);
        outcome.Error.Should().NotBeNullOrEmpty();
        _server.WriteAttempts.Should().Be(3, "one try and two retries");

        session.Dispose();
        File.Exists(outcome.LocalPath).Should().BeTrue("the only copy of the edit must survive the session");
        File.ReadAllText(outcome.LocalPath).Should().Be("precious edit");
    }

    [Fact]
    public async Task ShortUpload_IsDetectedAndRetried()
    {
        var session = await BeginAsync();
        _server.TruncateNextWrites = 1;   // first transfer "succeeds" but leaves half a file

        Save(session, "the whole content of the file");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        _server.Text("/etc/app.conf").Should().Be("the whole content of the file");
        _server.WriteAttempts.Should().Be(2);
    }

    [Fact]
    public async Task TransientFailure_RecoversOnRetry()
    {
        var session = await BeginAsync();
        _server.FailWrites = 1;

        Save(session, "edited");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        _server.Text("/etc/app.conf").Should().Be("edited");
    }

    // ── Somebody else changed the server copy ─────────────────────────────────

    [Fact]
    public async Task ServerCopyChanged_UserDeclines_NothingIsOverwritten()
    {
        var session = await BeginAsync();
        _service.ConfirmOverwrite = _ => Task.FromResult(false);
        _server.Put("/etc/app.conf", "changed by a colleague, longer");

        Save(session, "my edit");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Declined);
        _server.Text("/etc/app.conf").Should().Be("changed by a colleague, longer");
        File.ReadAllText(session.LocalPath).Should().Be("my edit");
    }

    [Fact]
    public async Task ServerCopyChanged_UserAgrees_ItIsOverwritten_AndTheQuestionCarriesBothStates()
    {
        var session = await BeginAsync();
        RemoteEditConflict? asked = null;
        _service.ConfirmOverwrite = c => { asked = c; return Task.FromResult(true); };
        _server.Put("/etc/app.conf", "changed by a colleague, longer");

        Save(session, "my edit");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        _server.Text("/etc/app.conf").Should().Be("my edit");
        asked.Should().NotBeNull();
        asked!.OpenedSize.Should().Be("original".Length);
        asked.CurrentSize.Should().Be("changed by a colleague, longer".Length);
    }

    [Fact]
    public async Task ServerCopyChanged_AnswerGivenOnAnotherThread_UploadDoesNotRunOnThatThread()
    {
        // ROADMAP 6.62, INV-06: the answer comes from the UI thread, and an await may continue on the
        // thread that completed it — then the upload after «yes» ran there. A plain thread stands in
        // for the UI thread: a continuation is free to run inline on it.
        var session = await BeginAsync();
        var answerThread = -1;
        _service.ConfirmOverwrite = _ =>
        {
            var answer = new TaskCompletionSource<bool>();
            var ui = new Thread(() =>
            {
                answerThread = Environment.CurrentManagedThreadId;
                Thread.Sleep(200);   // the user reads the question: the save is awaiting the answer by now
                answer.SetResult(true);
            });
            ui.Start();
            return answer.Task;
        };
        _server.Put("/etc/app.conf", "changed by a colleague, longer");

        Save(session, "my edit");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        _server.WriteThreads.Should().ContainSingle()
            .Which.Should().Match<(int Id, bool Pool)>(t => t.Id != answerThread && t.Pool,
                "the network write after the answer belongs on the thread pool");
    }

    [Fact]
    public async Task ServerCopyChanged_WithNoHandler_IsNeverOverwritten()
    {
        var session = await BeginAsync();
        _server.Put("/etc/app.conf", "changed by a colleague, longer");

        Save(session, "my edit");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Declined);
        _server.Text("/etc/app.conf").Should().Be("changed by a colleague, longer");
    }

    [Fact]
    public async Task FlushAll_ServerCopyChanged_DoesNotAskAndKeepsTheLocalCopy()
    {
        var session = await BeginAsync();
        var asked = 0;
        _service.SettleDelay = TimeSpan.FromMinutes(5);   // the save is still "settling" when the app closes
        _service.ConfirmOverwrite = _ => { asked++; return Task.FromResult(true); };
        _server.Put("/etc/app.conf", "changed by a colleague, longer");
        Save(session, "my edit");

        // On shutdown the UI thread is the one blocked in FlushAll: a question could never be shown.
        _service.FlushAll(TimeSpan.FromSeconds(5));
        session.Dispose();

        asked.Should().Be(0);
        _service.IsClosing.Should().BeTrue();
        _server.Text("/etc/app.conf").Should().Be("changed by a colleague, longer");
        session.KeptLocalCopy.Should().BeTrue();
        File.ReadAllText(session.LocalPath).Should().Be("my edit");
        Directory.Delete(Path.GetDirectoryName(session.LocalPath)!, recursive: true);
    }

    [Fact]
    public async Task OwnPreviousUpload_IsNotMistakenForAnExternalChange()
    {
        var session = await BeginAsync();
        var asked = 0;
        _service.ConfirmOverwrite = _ => { asked++; return Task.FromResult(false); };

        Save(session, "first");
        await NextEventAsync();
        Save(session, "second, a bit longer");
        var outcome = await NextEventAsync();

        outcome.Outcome.Should().Be(RemoteEditOutcome.Uploaded);
        asked.Should().Be(0);
    }

    // ── Lifetime ──────────────────────────────────────────────────────────────

    private sealed class Lease : IDisposable
    {
        public bool Released { get; private set; }
        public void Dispose() => Released = true;
    }

    [Fact]
    public async Task Lease_IsHeldForTheSessionAndReleasedWithIt()
    {
        var lease = new Lease();
        var session = await BeginAsync(lease);

        lease.Released.Should().BeFalse("the mount must stay alive for later saves (TRAP-05/29)");
        session.Dispose();
        lease.Released.Should().BeTrue();
    }

    [Fact]
    public async Task Lease_IsReleasedWhenTheSessionCannotStart()
    {
        var readOnly = new FakeServer { CanWrite = false };
        var lease = new Lease();

        var session = await _service.BeginAsync(readOnly, new VfsPath("/x"), "x", lease);

        session.Should().BeNull();
        lease.Released.Should().BeTrue();
    }

    [Fact]
    public async Task FlushAll_UploadsASaveThatIsStillWaitingOutItsSettleDelay()
    {
        _service.SettleDelay = TimeSpan.FromSeconds(30);   // the application is closing long before this
        var session = await BeginAsync();

        Save(session, "saved a moment before exit");
        _service.FlushAll(TimeSpan.FromSeconds(15));

        _server.Text("/etc/app.conf").Should().Be("saved a moment before exit");
    }

    [Fact]
    public async Task Dispose_WithEditsThatNeverReachedTheServer_KeepsTheCopy()
    {
        _service.SettleDelay = TimeSpan.FromSeconds(30);
        var session = await BeginAsync();
        Save(session, "unsaved to server");

        session.Dispose();

        File.Exists(session.LocalPath).Should().BeTrue();
    }

    [Fact]
    public async Task Dispose_WhenEverythingWasUploaded_DeletesTheTemporaryCopy()
    {
        var session = await BeginAsync();
        Save(session, "done");
        await NextEventAsync();

        session.Dispose();

        Directory.Exists(Path.GetDirectoryName(session.LocalPath)).Should().BeFalse();
    }

    [Fact]
    public async Task Begin_ReadOnlyFileSystem_IsRefused()
    {
        var session = await _service.BeginAsync(new FakeServer { CanWrite = false }, new VfsPath("/x"), "x");

        session.Should().BeNull();
    }

    // ── A server we can steer ─────────────────────────────────────────────────

    private sealed class FakeServer : IVirtualFileSystem
    {
        private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new(StringComparer.OrdinalIgnoreCase);
        private int _clock;

        public string Scheme => "sftp";
        public string DisplayName => "fake";
        public bool CanWrite { get; set; } = true;
        public int FailWrites { get; set; }
        public int TruncateNextWrites { get; set; }
        public int WriteAttempts { get; private set; }

        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

        public void Put(string path, string text) =>
            _files[path] = (Encoding.UTF8.GetBytes(text), new DateTime(2026, 1, 1).AddMinutes(_clock += 10));

        public string Text(string path) => Encoding.UTF8.GetString(_files[path].Data);

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
        {
            if (!_files.TryGetValue(path.Value, out var f))
            {
                return Task.FromResult<IFileEntry?>(null);
            }

            return Task.FromResult<IFileEntry?>(new FileEntry(path, path.FileName, false, f.Data.Length, f.Modified, FileAttributes.Normal));
        }

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(_files[path.Value].Data, writable: false));

        /// <summary>The thread each write was opened on, and whether it was a thread-pool thread.</summary>
        public List<(int Id, bool Pool)> WriteThreads { get; } = new();

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
        {
            WriteAttempts++;
            WriteThreads.Add((Environment.CurrentManagedThreadId, Thread.CurrentThread.IsThreadPoolThread));
            if (FailWrites > 0)
            {
                FailWrites--;
                throw new IOException("connection reset");
            }

            var truncate = TruncateNextWrites > 0;
            if (truncate)
            {
                TruncateNextWrites--;
            }

            return Task.FromResult<Stream>(new Sink(this, path.Value, truncate));
        }

        public bool SupportsOperation(VfsOperation operation) => CanWrite || operation == VfsOperation.Read;

        private sealed class Sink(FakeServer owner, string path, bool truncate) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    var data = ToArray();
                    if (truncate)
                    {
                        data = data[..(data.Length / 2)];
                    }

                    owner.Put(path, Encoding.UTF8.GetString(data));
                }
                base.Dispose(disposing);
            }
        }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CopyAsync(VfsPath s, VfsPath d, IProgress<FileProgress>? p, CancellationToken ct) => throw new NotSupportedException();
        public Task MoveAsync(VfsPath s, VfsPath d, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(VfsPath p, bool r, CancellationToken ct) => throw new NotSupportedException();
        public Task CreateDirectoryAsync(VfsPath p, CancellationToken ct) => throw new NotSupportedException();
        public Task RenameAsync(VfsPath p, string n, CancellationToken ct) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
