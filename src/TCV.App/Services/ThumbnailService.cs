using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TCV.App.Collections;

namespace TCV.App.Services;

/// <summary>One picture to make: the file as it is now, at the size the screen needs.</summary>
/// <param name="Path">A Windows path on the local disk (<see cref="ThumbnailPolicy.CanPreview"/>).</param>
/// <param name="Size">The file's length, with <paramref name="Modified"/> what tells a changed file apart.</param>
/// <param name="Pixels">Longest side to decode at, in device pixels.</param>
public readonly record struct ThumbnailKey(string Path, long Size, DateTime Modified, int Pixels);

/// <summary>
/// Pictures for the Thumbnails view, decoded by Windows' imaging component at the tile's size (a JPEG
/// is scaled while it decodes, so a 40-megapixel photo costs about what its tile does), turned
/// upright from the EXIF tag, and remembered for the session. Decoding runs on the thread pool (INV-06),
/// a few at a time, so a folder of photos does not starve the disk or the CPU. The Windows shell's own
/// thumbnail handlers are not used: they are COM objects of every installed application, run in the
/// app's process on pool threads that are not STA, and hand back bitmaps whose alpha has to be rebuilt.
/// </summary>
public static class ThumbnailService
{
    /// <summary>A screenful of tiles many times over: a tile is at most ~100 KB at 200% scaling.</summary>
    private const int CacheCapacity = 400;

    /// <summary>Decodes at a time. More only queue on the same disk.</summary>
    private const int MaxConcurrentDecodes = 3;

    // A null value is a file that could not be decoded (no codec, damaged, locked): it keeps its icon
    // and is not read again until it changes.
    private static readonly LruCache<ThumbnailKey, BitmapSource?> Cache = new(CacheCapacity);
    private static readonly SemaphoreSlim Gate = new(MaxConcurrentDecodes);

    /// <summary>The remembered picture of <paramref name="key"/>, if it was made before.</summary>
    public static bool TryGetCached(ThumbnailKey key, out BitmapSource? thumbnail) => Cache.TryGet(key, out thumbnail);

    /// <summary>
    /// The picture of <paramref name="key"/>, or null when the file cannot be decoded. Cancelling drops
    /// the request while it waits for its turn; a decode already running finishes and is remembered.
    /// </summary>
    public static async Task<BitmapSource?> GetAsync(ThumbnailKey key, CancellationToken ct)
    {
        if (Cache.TryGet(key, out var cached))
        {
            return cached;
        }

        await Gate.WaitAsync(ct);
        try
        {
            ct.ThrowIfCancellationRequested();

            // Another request for the same file may have finished while this one waited.
            if (Cache.TryGet(key, out cached))
            {
                return cached;
            }

            var thumbnail = await Task.Run(() => Decode(key.Path, key.Pixels), ct);
            Cache.Set(key, thumbnail);
            return thumbnail;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Reads the file and makes a frozen picture of it no larger than <paramref name="maxSide"/>.</summary>
    private static BitmapSource? Decode(string path, int maxSide)
    {
        try
        {
            // Shared for writing and deleting: a preview must never be the reason a save or a delete fails.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, bufferSize: 64 * 1024, FileOptions.SequentialScan);

            // The header alone: size and orientation, without decoding the pixels.
            var frame = BitmapDecoder.Create(stream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            var (decodeWidth, decodeHeight) = ThumbnailPolicy.DecodeSize(frame.PixelWidth, frame.PixelHeight, maxSide);
            var orientation = ReadOrientation(frame);

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.DecodePixelWidth = decodeWidth;
            image.DecodePixelHeight = decodeHeight;
            image.StreamSource = stream;
            image.EndInit();

            return Normalize(Upright(image, orientation));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // No codec, a damaged file, no access, a file that vanished: the tile keeps its icon.
            return null;
        }
    }

    /// <summary>The EXIF orientation tag, or 1 (as stored) when there is none.</summary>
    private static int ReadOrientation(BitmapFrame frame)
    {
        // JPEG keeps the tag in its EXIF block, TIFF in its first directory. Other formats have neither,
        // and some decoders throw rather than answer — for the metadata itself or for a query they do
        // not know. A missing tag must not cost the picture.
        foreach (var query in new[] { "/app1/ifd/{ushort=274}", "/ifd/{ushort=274}" })
        {
            try
            {
                if (frame.Metadata is BitmapMetadata metadata
                    && metadata.ContainsQuery(query)
                    && metadata.GetQuery(query) is ushort value)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Not this format's place for it.
            }
        }

        return 1;
    }

    private static BitmapSource Upright(BitmapSource image, int exifOrientation)
    {
        var (mirror, rotation) = ThumbnailPolicy.Orientation(exifOrientation);
        if (!mirror && rotation == 0)
        {
            return image;
        }

        var transform = new TransformGroup();
        if (mirror)
        {
            transform.Children.Add(new ScaleTransform(-1, 1));
        }

        if (rotation != 0)
        {
            transform.Children.Add(new RotateTransform(rotation));
        }

        return new TransformedBitmap(image, transform);
    }

    /// <summary>
    /// Copies the picture into a plain 96-DPI bitmap. The DPI a file declares (a 300-DPI scan, a 72-DPI
    /// screenshot) would otherwise decide how big WPF draws it, and the copy holds only the small result,
    /// not the decoder chain behind it.
    /// </summary>
    private static BitmapSource Normalize(BitmapSource image)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }
}
