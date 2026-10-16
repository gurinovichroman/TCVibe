using System.Net;
using System.Net.Http;

namespace TCV.Network;

/// <summary>
/// A request body read from a seekable stream that reports, chunk by chunk, how many bytes have been
/// handed to the connection. <see cref="StreamContent"/> sends the same bytes but says nothing until it
/// is done, so a large upload sat at its last figure for as long as the network took.
/// </summary>
/// <remarks>
/// Does not own the stream: the caller disposes it. The body is sent from the position the stream had
/// when this content was made, every time — the handler sends it again when the server first answers
/// with an authentication challenge, and the progress then starts over from zero.
/// </remarks>
internal sealed class ProgressStreamContent : HttpContent
{
    private const int BufferSize = 81920;

    private readonly Stream _source;
    private readonly IProgress<long>? _progress;
    private readonly long _start;

    public ProgressStreamContent(Stream source, IProgress<long>? progress)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanSeek)
        {
            throw new ArgumentException("The body must be seekable: its length is sent up front.", nameof(source));
        }

        _source = source;
        _progress = progress;
        _start = source.Position;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        _source.Position = _start;
        var buffer = new byte[BufferSize];
        long sent = 0;
        int read;
        while ((read = await _source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            sent += read;
            _progress?.Report(sent);
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = _source.Length - _start;
        return true;
    }
}
