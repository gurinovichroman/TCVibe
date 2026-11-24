using System.IO;
using FluentAssertions;
using Moq;
using TCV.App.FileSystem;
using TCV.App.Viewer;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>
/// The viewer's jump to an offset (TRAP-106): a server that can start there does, so End on a 2 GB file
/// does not download 2 GB; one that cannot, is read up to it as before.
/// </summary>
public sealed class ViewerRangeReadTests
{
    private const int Size = 1_000_000;

    private static readonly byte[] Data = Enumerable.Range(0, Size).Select(i => (byte)(i % 251)).ToArray();

    /// <summary>A stream that cannot seek and counts what is read from it: the wire.</summary>
    private sealed class WireStream(byte[] data, int start) : MemoryStream(data, start, data.Length - start, writable: false)
    {
        public long BytesRead { get; private set; }

        public override bool CanSeek => false;

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }
    }

    private abstract class ServerBase : IVirtualFileSystem
    {
        public WireStream? Opened { get; protected set; }

        public string Scheme => "ftp";

        public string DisplayName => "test";

        public event EventHandler<VfsChangedEventArgs>? Changed { add { } remove { } }

        public Task<IReadOnlyList<IFileEntry>> ListAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IFileEntry?> GetEntryAsync(VfsPath path, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(VfsPath path, CancellationToken ct = default)
        {
            Opened = new WireStream(Data, 0);
            return Task.FromResult<Stream>(Opened);
        }

        public Task<Stream> OpenWriteAsync(VfsPath path, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyAsync(VfsPath source, VfsPath destination, IProgress<FileProgress>? progress, CancellationToken ct) => throw new NotSupportedException();

        public Task MoveAsync(VfsPath source, VfsPath destination, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(VfsPath path, bool recursive, CancellationToken ct) => throw new NotSupportedException();

        public Task CreateDirectoryAsync(VfsPath path, CancellationToken ct) => throw new NotSupportedException();

        public Task RenameAsync(VfsPath path, string newName, CancellationToken ct) => throw new NotSupportedException();

        public bool SupportsOperation(VfsOperation operation) => true;

        public void Dispose()
        {
        }
    }

    /// <summary>Takes the offset, as a server with REST does.</summary>
    private sealed class RangedServer : ServerBase, IRangeReader
    {
        public List<long> Asked { get; } = [];

        public Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
            VfsPath path, long offset, string? validator, CancellationToken ct)
        {
            Asked.Add(offset);
            Opened = new WireStream(Data, (int)offset);
            return Task.FromResult<(Stream, long, long, string?)>((Opened, offset, 0, null));
        }
    }

    /// <summary>Answers a ranged open with the whole file: a server that refused REST.</summary>
    private sealed class RefusingServer : ServerBase, IRangeReader
    {
        public Task<(Stream Stream, long Offset, long Total, string? Validator)> OpenReadFromAsync(
            VfsPath path, long offset, string? validator, CancellationToken ct)
        {
            Opened = new WireStream(Data, 0);
            return Task.FromResult<(Stream, long, long, string?)>((Opened, 0, 0, null));
        }
    }

    private sealed class PlainServer : ServerBase;

    [Fact]
    public async Task ReadAsync_ServerStartsAtTheOffset_ReadsNoMoreThanTheWindow()
    {
        var server = new RangedServer();
        var source = new VfsViewerSource(server, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(900_000, 4096, null, CancellationToken.None);

        server.Asked.Should().Equal(900_000);
        bytes.Should().Equal(Data.Skip(900_000).Take(4096));
        server.Opened!.BytesRead.Should().Be(4096, "End on a big file must not fetch what lies before it");
    }

    [Fact]
    public async Task ReadAsync_ServerRefusedTheOffset_SkipsUpToItAsBefore()
    {
        var server = new RefusingServer();
        var source = new VfsViewerSource(server, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(900_000, 4096, null, CancellationToken.None);

        bytes.Should().Equal(Data.Skip(900_000).Take(4096));
        server.Opened!.BytesRead.Should().Be(904_096);
    }

    [Fact]
    public async Task ReadAsync_NoRangeReader_SkipsUpToItAsBefore()
    {
        var server = new PlainServer();
        var source = new VfsViewerSource(server, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(500_000, 100, null, CancellationToken.None);

        bytes.Should().Equal(Data.Skip(500_000).Take(100));
    }

    [Fact]
    public async Task ReadAsync_TheStart_IsAPlainOpen()
    {
        var server = new RangedServer();
        var source = new VfsViewerSource(server, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(0, 100, null, CancellationToken.None);

        server.Asked.Should().BeEmpty();
        bytes.Should().Equal(Data.Take(100));
    }

    [Fact]
    public async Task ReadAsync_OffsetAtTheKnownEnd_DoesNotAskTheServerForARangeThatIsNotThere()
    {
        var server = new RangedServer();
        var source = new VfsViewerSource(server, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(Size, 100, null, CancellationToken.None);

        server.Asked.Should().BeEmpty();
        bytes.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_SizeNotKnown_ReadsToTheOffset()
    {
        // A listing that did not say the size: nothing tells a range from one past the end.
        var server = new RangedServer();
        var source = new VfsViewerSource(server, new VfsPath("/big.log"), 0);

        var bytes = await source.ReadAsync(500_000, 100, null, CancellationToken.None);

        server.Asked.Should().BeEmpty();
        bytes.Should().Equal(Data.Skip(500_000).Take(100));
    }

    [Fact]
    public async Task ReadAsync_ThroughSearchResults_TheServerStillStartsAtTheOffset()
    {
        var server = new RangedServer();
        var view = new SearchResultsFileSystem(server, []);
        var source = new VfsViewerSource(view, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(900_000, 100, null, CancellationToken.None);

        server.Asked.Should().Equal(900_000);
        bytes.Should().Equal(Data.Skip(900_000).Take(100));
    }

    [Fact]
    public async Task ReadAsync_ThroughSearchResultsOfThePlainDisk_SeeksAsBefore()
    {
        var disk = new Mock<IVirtualFileSystem>();
        disk.Setup(v => v.OpenReadAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(Data, writable: false));
        var view = new SearchResultsFileSystem(disk.Object, []);
        var source = new VfsViewerSource(view, new VfsPath("/big.log"), Size);

        var bytes = await source.ReadAsync(900_000, 100, null, CancellationToken.None);

        bytes.Should().Equal(Data.Skip(900_000).Take(100));
    }
}
