using SharpCompress.Archives;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Readers;

namespace TCV.Archives.Engines;

/// <summary>7z engine using SharpCompress (read-only; creating 7z goes through 7z.exe).</summary>
public sealed class SevenZipEngine : SharpCompressEngineBase
{
    public SevenZipEngine(string? password = null) : base(password)
    {
    }

    // SharpCompress does not report the encryption flag for 7z entries, so the data is probed.
    protected override bool FlagsAreReliable => false;

    protected override IArchive OpenArchive(Stream stream, ReaderOptions options) =>
        SevenZipArchive.OpenArchive(stream, options);
}
