using System.IO;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// Every write-back of an F4 edit on a server is a line in the operation log (INV-10, 6.49): one per
/// save, whatever happened to it, and retries do not multiply it.
/// </summary>
public sealed class RemoteEditJournalTests : IDisposable
{
    private static readonly UTF8Encoding NoBom = new(false);

    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"tcv-edit-log-{Guid.NewGuid():N}.json");
    private readonly OperationLog _log;
    private readonly RemoteEditService _service;
    private readonly Server _server = new();
    private readonly SemaphoreSlim _outcomes = new(0);

    public RemoteEditJournalTests()
    {
        _log = new OperationLog(_logPath);
        _service = new RemoteEditService(NullLogger<RemoteEditService>.Instance, _ => true, _log)
        {
            SettleDelay = TimeSpan.FromMilliseconds(50),
            RetryDelays = [TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10)],
        };
        _service.Uploaded += (_, _) => _outcomes.Release();
    }

    public void Dispose()
    {
        _service.Dispose();
        if (File.Exists(_logPath))
        {
            File.Delete(_logPath);
        }
    }

    private async Task<RemoteEditSession> BeginAsync()
    {
        _server.Put("/etc/app.conf", "original");
        var session = await _service.BeginAsync(_server, new VfsPath("/etc/app.conf"), "app.conf");
        session.Should().NotBeNull();
        return session!;
    }

    private async Task SaveAndWaitAsync(RemoteEditSession session, string text)
    {
        File.WriteAllText(session.LocalPath, text, NoBom);
        (await _outcomes.WaitAsync(TimeSpan.FromSeconds(15))).Should().BeTrue("the save must have an outcome");
    }

    [Fact]
    public async Task Save_Uploaded_IsLoggedAsACompletedUploadOfTheCopy()
    {
        var session = await BeginAsync();

        await SaveAndWaitAsync(session, "edited");

        var entry = _log.LoadAll().Should().ContainSingle().Subject;
        entry.Kind.Should().Be(OperationKind.Upload);
        entry.Outcome.Should().Be(OperationOutcome.Completed);
        entry.Source.Should().Be(session.LocalPath);
        entry.Destination.Should().Be("/etc");
        entry.Error.Should().BeNull();
    }

    [Fact]
    public async Task Save_EverySave_IsItsOwnLine()
    {
        var session = await BeginAsync();

        await SaveAndWaitAsync(session, "first");
        await SaveAndWaitAsync(session, "second, longer");

        _log.LoadAll().Should().HaveCount(2).And.OnlyContain(e => e.Outcome == OperationOutcome.Completed);
    }

    [Fact]
    public async Task Save_FailedAfterRetries_IsOneFailedLineWithTheReason()
    {
        var session = await BeginAsync();
        _server.FailWrites = int.MaxValue;

        await SaveAndWaitAsync(session, "precious edit");

        var entry = _log.LoadAll().Should().ContainSingle("three attempts are one save").Subject;
        entry.Outcome.Should().Be(OperationOutcome.Failed);
        entry.Error.Should().Be("connection reset");
        entry.Source.Should().Be(session.LocalPath, "the log says where the edits were kept");
        session.Dispose();
        Directory.Delete(Path.GetDirectoryName(session.LocalPath)!, recursive: true);
    }

    [Fact]
    public async Task Save_DeclinedBecauseTheServerCopyChanged_IsLoggedAsCancelled()
    {
        var session = await BeginAsync();
        _server.Put("/etc/app.conf", "changed by a colleague, longer");

        await SaveAndWaitAsync(session, "my edit");

        var entry = _log.LoadAll().Should().ContainSingle().Subject;
        entry.Outcome.Should().Be(OperationOutcome.Cancelled);
        _server.Text("/etc/app.conf").Should().Be("changed by a colleague, longer");
        session.Dispose();
        Directory.Delete(Path.GetDirectoryName(session.LocalPath)!, recursive: true);
    }

    // ── A server that can be told to fail ─────────────────────────────────────

    private sealed class Server : IVirtualFileSystem
    {
        private readonly Dictionary<string, (byte[] Data, DateTime Modified)> _files = new(StringComparer.OrdinalIgnoreCase);
        private int _clock;

        public string Scheme => "sftp";
        public string DisplayName => "fake";
        public int FailWrites { get; set; }

        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

        public void Put(string path, string text)
        {
            lock (_files)
            {
                _files[path] = (Encoding.UTF8.GetBytes(text), new DateTime(2026, 1, 1).AddMinutes(_clock += 10));
            }
        }

        public string Text(string path)
        {
            lock (_files)
            {
                return Encoding.UTF8.GetString(_files[path].Data);
            }
        }

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default)
        {
            lock (_files)
            {
                return Task.FromResult<IFileEntry?>(_files.TryGetValue(path.Value, out var f)
                    ? new FileEntry(path, path.FileName, false, f.Data.Length, f.Modified, FileAttributes.Normal)
                    : null);
            }
        }

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
        {
            lock (_files)
            {
                return Task.FromResult<Stream>(new MemoryStream(_files[path.Value].Data, writable: false));
            }
        }

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default)
        {
            if (FailWrites > 0)
            {
                FailWrites--;
                throw new IOException("connection reset");
            }

            return Task.FromResult<Stream>(new Sink(this, path.Value));
        }

        public bool SupportsOperation(VfsOperation operation) => true;

        private sealed class Sink(Server owner, string path) : MemoryStream
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    owner.Put(path, Encoding.UTF8.GetString(ToArray()));
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
