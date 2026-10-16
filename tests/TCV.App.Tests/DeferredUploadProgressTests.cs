using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>
/// A destination that collects the body and uploads it afterwards (WebDAV): the copy's bar must follow
/// the upload, not reach 100 % once the body has merely been collected.
/// </summary>
public class DeferredUploadProgressTests
{
    private const int FileSize = 3 * 1024 * 1024; // three of the pipeline's 1 MB chunks

    [Fact]
    public async Task Copy_ToADeferredUpload_ReachesTheTotalOnlyOnceTheUploadIsDone()
    {
        var source = new InMemoryFileSystem("mem");
        source.AddFile("/src/big.bin", new byte[FileSize]);
        var server = new InMemoryFileSystem("dav");
        server.AddDirectory("/pub");
        var destination = new DeferredUploadFileSystem(server);
        var entry = (await source.GetEntryAsync(new VfsPath("/src/big.bin")))!;
        var reports = new List<(long Shown, long Sent, long Collected)>();

        var op = new CopyOperation(source, destination, [entry], new VfsPath("/pub"),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance)
        {
            Progress = new InlineProgress(p => reports.Add(
                (p.BytesCopied, destination.Upload?.Sent ?? 0, destination.Upload?.Length ?? 0))),
        };
        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        server.ReadFile("/pub/big.bin").Should().HaveCount(FileSize);
        reports.Where(r => r.Shown == FileSize).Should().NotBeEmpty()
            .And.OnlyContain(r => r.Sent == r.Collected, "the file counts as copied only once it is uploaded");
        reports.Should().Contain(r => r.Shown > FileSize / 2 && r.Shown < FileSize,
            "the bar keeps moving through the upload");
    }

    private sealed class InlineProgress(Action<FileProgress> report) : IProgress<FileProgress>
    {
        public void Report(FileProgress value) => report(value);
    }

    /// <summary>Hands out <see cref="DeferredUpload"/> streams; everything else goes to the inner file system.</summary>
    private sealed class DeferredUploadFileSystem(InMemoryFileSystem inner) : IVirtualFileSystem
    {
        public DeferredUpload? Upload { get; private set; }

        public string Scheme => inner.Scheme;
        public string DisplayName => inner.DisplayName;

        public event EventHandler<VfsChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public async Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) =>
            Upload = new DeferredUpload(await inner.OpenWriteAsync(path, overwrite, ct));

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => inner.ListAsync(path, ct);
        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => inner.GetEntryAsync(path, ct);
        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default) => inner.OpenReadAsync(path, ct);
        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) =>
            inner.CopyAsync(source, destination, progress, ct);
        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => inner.MoveAsync(source, destination, ct);
        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => inner.DeleteAsync(path, recursive, ct);
        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => inner.CreateDirectoryAsync(path, ct);
        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => inner.RenameAsync(path, newName, ct);
        public bool SupportsOperation(VfsOperation operation) => inner.SupportsOperation(operation);
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Collects what is written, then "uploads" it to the server in four slow steps when committed.</summary>
    private sealed class DeferredUpload(Stream server) : MemoryStream, IDeferredUploadStream
    {
        public long Sent { get; private set; }

        public void Abort()
        {
        }

        public async Task CommitAsync(IProgress<long>? progress, CancellationToken ct)
        {
            var body = ToArray();
            var step = body.Length / 4;
            while (Sent < body.Length)
            {
                // Slower than the operation's 100 ms report throttle, so every step reaches the recorder.
                await Task.Delay(150, ct);
                var count = (int)Math.Min(step, body.Length - Sent);
                await server.WriteAsync(body.AsMemory((int)Sent, count), ct);
                Sent += count;
                progress?.Report(Sent);
            }

            await server.DisposeAsync();
        }
    }
}
