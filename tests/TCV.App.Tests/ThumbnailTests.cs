using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FluentAssertions;
using TCV.App.Services;

namespace TCV.App.Tests;

/// <summary>Which files the Thumbnails view draws, and how: size, orientation, failures.</summary>
public class ThumbnailPolicyTests
{
    [Theory]
    [InlineData("jpg")]
    [InlineData("JPEG")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("heic")]
    public void CanPreview_AnImageOnTheDisk_IsTrue(string extension)
    {
        ThumbnailPolicy.CanPreview(extension, false, 1000, FileAttributes.Archive, onLocalDisk: true).Should().BeTrue();
    }

    [Fact]
    public void CanPreview_InsideAnArchiveOrOnAServer_IsFalse()
    {
        // A mounted panel has no Windows path to read (INV-07), and reading would download the file.
        ThumbnailPolicy.CanPreview("jpg", false, 1000, FileAttributes.Normal, onLocalDisk: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("svg")]
    [InlineData("")]
    public void CanPreview_NotAnImage_IsFalse(string extension)
    {
        ThumbnailPolicy.CanPreview(extension, false, 1000, FileAttributes.Normal, onLocalDisk: true).Should().BeFalse();
    }

    [Fact]
    public void CanPreview_AFolderNamedLikeAnImage_IsFalse()
    {
        ThumbnailPolicy.CanPreview("png", true, 0, FileAttributes.Directory, onLocalDisk: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(ThumbnailPolicy.MaxFileBytes + 1)]
    public void CanPreview_EmptyOrHugeFiles_KeepTheirIcon(long size)
    {
        ThumbnailPolicy.CanPreview("jpg", false, size, FileAttributes.Normal, onLocalDisk: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(FileAttributes.Offline)]
    [InlineData((FileAttributes)0x00040000)]   // FILE_ATTRIBUTE_RECALL_ON_OPEN
    [InlineData((FileAttributes)0x00400000)]   // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS (OneDrive "online-only")
    public void CanPreview_ACloudPlaceholder_IsFalse(FileAttributes attributes)
    {
        // Reading it would download the photo just to draw a tile.
        ThumbnailPolicy.CanPreview("jpg", false, 1000, attributes | FileAttributes.Archive, onLocalDisk: true).Should().BeFalse();
    }

    [Theory]
    [InlineData(4000, 3000, 80, 80, 0)]
    [InlineData(3000, 4000, 80, 0, 80)]
    [InlineData(500, 500, 80, 80, 0)]
    public void DecodeSize_ABigImage_BringsTheLongerSideDown(int width, int height, int max, int expectedWidth, int expectedHeight)
    {
        ThumbnailPolicy.DecodeSize(width, height, max).Should().Be((expectedWidth, expectedHeight));
    }

    [Fact]
    public void DecodeSize_ASmallImage_IsNeverEnlarged()
    {
        ThumbnailPolicy.DecodeSize(32, 16, 80).Should().Be((0, 0));
    }

    [Theory]
    [InlineData(1, 80)]
    [InlineData(1.5, 120)]
    [InlineData(1.25, 100)]
    [InlineData(0.5, 80)]
    public void TargetPixels_FollowsTheScreenScaling(double dpiScale, int expected)
    {
        ThumbnailPolicy.TargetPixels(80, dpiScale).Should().Be(expected);
    }

    [Theory]
    [InlineData(1, false, 0)]
    [InlineData(2, true, 0)]
    [InlineData(3, false, 180)]
    [InlineData(4, true, 180)]
    [InlineData(5, true, 270)]
    [InlineData(6, false, 90)]
    [InlineData(7, true, 90)]
    [InlineData(8, false, 270)]
    [InlineData(0, false, 0)]
    [InlineData(42, false, 0)]
    public void Orientation_MapsTheExifTag(int tag, bool mirror, int rotation)
    {
        ThumbnailPolicy.Orientation(tag).Should().Be((mirror, rotation));
    }
}

/// <summary>Decoding real files (Windows imaging, a temporary folder).</summary>
public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-thumbs-" + Guid.NewGuid().ToString("N"));

    public ThumbnailServiceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not a test failure.
        }
    }

    [Fact]
    public async Task GetAsync_AWidePng_IsDecodedToTheBoxAt96Dpi()
    {
        var path = Write("wide.png", new PngBitmapEncoder(), 400, 200, dpi: 300);

        var picture = await ThumbnailService.GetAsync(KeyFor(path, 80), CancellationToken.None);

        picture.Should().NotBeNull();
        picture!.PixelWidth.Should().Be(80);
        picture.PixelHeight.Should().Be(40);
        picture.DpiX.Should().Be(96, "a file's own DPI must not decide how big the tile draws it");
        picture.IsFrozen.Should().BeTrue("it is made on a pool thread and shown on the UI thread");
    }

    [Fact]
    public async Task GetAsync_APortraitJpegLyingOnItsSide_IsTurnedUpright()
    {
        // Stored 200×100 with orientation 6 (rotate 90° clockwise), as a phone saves a portrait photo.
        var path = Write("portrait.jpg", new JpegBitmapEncoder(), 200, 100, dpi: 72, exifOrientation: 6);

        var picture = await ThumbnailService.GetAsync(KeyFor(path, 80), CancellationToken.None);

        picture.Should().NotBeNull();
        picture!.PixelWidth.Should().Be(40);
        picture.PixelHeight.Should().Be(80);
    }

    [Fact]
    public async Task GetAsync_NotReallyAnImage_GivesNoPictureAndRemembersThat()
    {
        var path = Path.Combine(_dir, "fake.png");
        await File.WriteAllTextAsync(path, "this is not a PNG");
        var key = KeyFor(path, 80);

        var picture = await ThumbnailService.GetAsync(key, CancellationToken.None);

        picture.Should().BeNull();
        ThumbnailService.TryGetCached(key, out var cached).Should().BeTrue("a broken file is not read again");
        cached.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_Cancelled_DoesNotDecode()
    {
        var path = Write("cancelled.png", new PngBitmapEncoder(), 50, 50, dpi: 96);
        var key = KeyFor(path, 80);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => ThumbnailService.GetAsync(key, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        ThumbnailService.TryGetCached(key, out _).Should().BeFalse();
    }

    private static ThumbnailKey KeyFor(string path, int pixels)
    {
        var info = new FileInfo(path);
        return new ThumbnailKey(path, info.Length, info.LastWriteTime, pixels);
    }

    private string Write(string name, BitmapEncoder encoder, int width, int height, double dpi, int exifOrientation = 0)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 0x40;       // B
            pixels[i + 1] = 0x80;   // G
            pixels[i + 2] = 0xC0;   // R
            pixels[i + 3] = 0xFF;   // A
        }

        var source = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, stride);
        BitmapMetadata? metadata = null;
        if (exifOrientation != 0)
        {
            metadata = new BitmapMetadata("jpg");
            metadata.SetQuery("/app1/ifd/{ushort=274}", (ushort)exifOrientation);
        }

        encoder.Frames.Add(BitmapFrame.Create(source, null, metadata, null));
        var path = Path.Combine(_dir, name);
        using var stream = File.Create(path);
        encoder.Save(stream);
        return path;
    }
}
