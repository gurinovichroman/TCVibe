using System.IO;

namespace TCV.App.Services;

/// <summary>
/// Which files the Thumbnails view draws a picture of, and at what size — the decisions behind
/// <see cref="ThumbnailService"/>, kept WPF-free so they can be tested.
/// </summary>
public static class ThumbnailPolicy
{
    /// <summary>Side of the square a picture is fitted into, in device-independent pixels. The tile in
    /// <c>FileListControl.xaml</c> reserves exactly this much.</summary>
    public const double BoxSize = 80;

    /// <summary>Bigger files keep their icon: a decode that size costs seconds and hundreds of megabytes.</summary>
    public const long MaxFileBytes = 64L * 1024 * 1024;

    // Cloud placeholders (OneDrive "online-only" and the like): reading one downloads it. Explorer's own
    // thumbnails leave them alone for the same reason. Not named in FileAttributes.
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes NotLocal = FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;

    /// <summary>
    /// What Windows' imaging component decodes out of the box or with the codecs Windows ships from the
    /// Store (WebP, HEIF, AVIF). A file whose codec is missing fails to decode and keeps its icon.
    /// </summary>
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        "png", "jpg", "jpeg", "jpe", "jfif", "gif", "bmp", "dib", "ico", "tif", "tiff",
        "wdp", "jxr", "hdp", "webp", "heic", "heif", "avif",
    };

    /// <summary>
    /// True when the entry gets a picture rather than its type's icon: an image file on the local disk,
    /// not empty, not huge, and really there. Archive entries and server files are not read for a
    /// picture — they have no Windows path (INV-07), and a listing would turn into a download.
    /// </summary>
    /// <param name="extension">Without the dot, as <c>FileEntryViewModel.Extension</c> has it.</param>
    public static bool CanPreview(string extension, bool isDirectory, long size, FileAttributes attributes, bool onLocalDisk) =>
        onLocalDisk
        && !isDirectory
        && size > 0
        && size <= MaxFileBytes
        && (attributes & NotLocal) == 0
        && Extensions.Contains(extension);

    /// <summary>The longest side to decode at: the box in device pixels on this screen.</summary>
    public static int TargetPixels(double boxSize, double dpiScale) =>
        (int)Math.Ceiling(boxSize * Math.Max(1.0, dpiScale));

    /// <summary>
    /// The decode size for an image of <paramref name="pixelWidth"/> × <paramref name="pixelHeight"/>:
    /// its longer side brought down to <paramref name="maxSide"/>, the other left at zero so the decoder
    /// keeps the aspect ratio. A smaller image is decoded as it is (0, 0) — never enlarged.
    /// </summary>
    public static (int Width, int Height) DecodeSize(int pixelWidth, int pixelHeight, int maxSide)
    {
        if (pixelWidth <= maxSide && pixelHeight <= maxSide)
        {
            return (0, 0);
        }

        return pixelWidth >= pixelHeight ? (maxSide, 0) : (0, maxSide);
    }

    /// <summary>
    /// How to turn a photo upright from its EXIF orientation tag: mirror it left to right first (or
    /// not), then rotate it clockwise by the angle. Cameras store pixels as the sensor saw them and
    /// leave the turning to the tag, so a portrait photo without this lies on its side.
    /// </summary>
    public static (bool Mirror, int Rotation) Orientation(int exifOrientation) => exifOrientation switch
    {
        2 => (true, 0),
        3 => (false, 180),
        4 => (true, 180),
        5 => (true, 270),
        6 => (false, 90),
        7 => (true, 90),
        8 => (false, 270),
        _ => (false, 0),
    };
}
