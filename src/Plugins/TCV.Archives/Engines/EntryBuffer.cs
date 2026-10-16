namespace TCV.Archives.Engines;

/// <summary>
/// Seekable backing store for one extracted archive entry: memory for small entries,
/// a self-deleting temp file for large ones. Archive entries can exceed 2 GB, which
/// neither <see cref="MemoryStream"/>'s int capacity nor a byte[] can hold.
/// </summary>
internal static class EntryBuffer
{
    private const long MaxInMemoryBytes = 64L * 1024 * 1024;

    public static Stream Create(long expectedLength)
    {
        if (expectedLength >= 0 && expectedLength <= MaxInMemoryBytes)
            return new MemoryStream((int)expectedLength);

        return new FileStream(
            Path.Combine(Path.GetTempPath(), $"TCV_entry_{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 81920, FileOptions.DeleteOnClose);
    }

    /// <summary>Fills a new buffer via <paramref name="fill"/> and rewinds it; disposes it if filling throws.</summary>
    public static Stream Fill(long expectedLength, Action<Stream> fill)
    {
        var buffer = Create(expectedLength);
        try
        {
            fill(buffer);
            buffer.Position = 0;
            return buffer;
        }
        catch
        {
            buffer.Dispose();
            throw;
        }
    }
}
