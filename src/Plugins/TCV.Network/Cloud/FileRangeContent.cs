using System.Net;
using System.Net.Http;

namespace TCV.Network.Cloud;

/// <summary>
/// A request body that is one window of a seekable stream — a whole small file, or one chunk of an
/// upload session — reporting how many bytes of the whole upload have been handed to the connection.
/// </summary>
/// <remarks>
/// Does not own the stream. The window is sent from its start every time, so a request that is sent
/// again after a throttling answer carries the same bytes, and its progress starts over from
/// <c>progressBase</c>.
/// </remarks>
internal sealed class FileRangeContent : HttpContent
{
    private const int BufferSize = 81920;

    private readonly Stream _source;
    private readonly long _start;
    private readonly long _length;
    private readonly IProgress<long>? _progress;
    private readonly long _progressBase;

    /// <param name="progressBase">Bytes of the upload already sent before this window, added to every report.</param>
    public FileRangeContent(Stream source, long start, long length, IProgress<long>? progress, long progressBase = 0)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek)
        {
            throw new ArgumentException("The body must be seekable: its length is sent up front.", nameof(source));
        }

        _source = source;
        _start = start;
        _length = length;
        _progress = progress;
        _progressBase = progressBase;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _source.Position = _start;
        var buffer = new byte[(int)Math.Min(BufferSize, Math.Max(_length, 1))];
        long sent = 0;
        while (sent < _length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await _source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, _length - sent)), cancellationToken);
            if (read == 0)
            {
                throw new IOException("The upload's temporary copy ended early.");
            }

            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sent += read;
            _progress?.Report(_progressBase + sent);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _length;
        return true;
    }
}
