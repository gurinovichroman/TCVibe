using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Readers;

namespace TCV.Archives.Engines;

/// <summary>RAR engine using SharpCompress (read-only; RAR is a proprietary write format).</summary>
public sealed class RarEngine : SharpCompressEngineBase
{
    public RarEngine(string? password = null) : base(password)
    {
    }

    protected override IArchive OpenArchive(Stream stream, ReaderOptions options) =>
        RarArchive.OpenArchive(stream, options);
}
