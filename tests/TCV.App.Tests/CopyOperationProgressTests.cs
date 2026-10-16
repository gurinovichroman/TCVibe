using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.FileSystem;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

public class CopyOperationProgressTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tcv_copy_{Guid.NewGuid():N}");
    private readonly WindowsFileSystem _fs = new(NullLogger<WindowsFileSystem>.Instance);

    public CopyOperationProgressTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ProgressRecorder : IProgress<FileProgress>
    {
        public List<FileProgress> Reports { get; } = new();
        public void Report(FileProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task Copy_DirectoryTree_ProgressCountsNestedFiles()
    {
        // src/                       — 4 KB across two levels; a non-recursive size scan
        //   top.txt (1 KB)             would report a 0-byte total and blow past 100%
        //   nested/deep.txt (3 KB)
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(Path.Combine(src, "nested"));
        await File.WriteAllBytesAsync(Path.Combine(src, "top.txt"), new byte[1024]);
        await File.WriteAllBytesAsync(Path.Combine(src, "nested", "deep.txt"), new byte[3072]);

        var dest = Path.Combine(_root, "dest");
        Directory.CreateDirectory(dest);

        var entry = await _fs.GetEntryAsync(new VfsPath(src));
        var recorder = new ProgressRecorder();

        var op = new CopyOperation(_fs, _fs, [entry!], new VfsPath(dest),
            new OverwriteStrategy(), NullLogger<CopyOperation>.Instance)
        {
            Progress = recorder,
        };

        await op.ExecuteAsync(CancellationToken.None);

        op.State.Should().Be(OperationState.Completed);
        File.Exists(Path.Combine(dest, "src", "nested", "deep.txt")).Should().BeTrue();

        recorder.Reports.Should().NotBeEmpty();
        recorder.Reports.Should().OnlyContain(r => r.TotalBytes == 4096);
        recorder.Reports.Should().OnlyContain(r => r.BytesCopied <= r.TotalBytes);
        recorder.Reports[^1].BytesCopied.Should().Be(4096);
    }
}
