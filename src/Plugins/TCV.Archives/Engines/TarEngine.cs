using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Writers;
using TCV.Core.PluginSdk;

namespace TCV.Archives.Engines;

/// <summary>
/// TAR/GZ/BZ2 engine using SharpCompress. A TAR can be created but not modified in place, so
/// <see cref="CanWrite"/> is false: the UI must not offer "add to archive" or "delete entry" for it.
/// </summary>
/// <remarks>
/// <see cref="IArchiveEngine"/> is listed again on purpose: the base class maps the cancellable
/// <c>Pack</c> overload to the interface's default body, which ignores the token, so without the
/// re-implementation Cancel during a TAR pack did nothing until the whole archive was written.
/// </remarks>
public sealed class TarEngine : SharpCompressEngineBase, IArchiveEngine
{
    public TarEngine() : base(null)
    {
    }

    protected override IArchive OpenArchive(Stream stream, SharpCompress.Readers.ReaderOptions options) =>
        ArchiveFactory.OpenArchive(stream, options);

    public override void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files) =>
        Pack(archivePath, files, System.IO.Compression.CompressionLevel.Optimal, CancellationToken.None);

    /// <summary>
    /// Writes a new TAR (compression picked from the extension: .gz/.tgz, .bz2/.tbz2, else none).
    /// Directory entries (names ending in <c>/</c>) are skipped: SharpCompress' TAR writer can only
    /// emit files, so an empty directory does not survive a TAR round trip.
    /// </summary>
    public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files,
        System.IO.Compression.CompressionLevel compressionLevel, CancellationToken ct)
    {
        var completed = false;
        try
        {
            using (var outStream = File.Create(archivePath))
            {
                var ext = Path.GetExtension(archivePath).ToLowerInvariant();
                var compType = ext switch
                {
                    ".gz" or ".tgz" => CompressionType.GZip,
                    ".bz2" or ".tbz2" => CompressionType.BZip2,
                    _ => CompressionType.None,
                };

                var opts = new SharpCompress.Writers.Tar.TarWriterOptions(compType, finalizeArchiveOnClose: true);
                using var writer = WriterFactory.OpenWriter(outStream, ArchiveType.Tar, opts);

                foreach (var (localPath, entryName) in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (entryName.EndsWith('/'))
                    {
                        continue;
                    }

                    writer.Write(entryName, localPath);
                }
            }

            completed = true;
        }
        finally
        {
            if (!completed)
            {
                TryDelete(archivePath);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: the caller reports the original failure.
        }
    }
}
