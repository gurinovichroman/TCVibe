using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SharpVectors.Converters;
using SharpVectors.Renderers.Wpf;

namespace TCV.App.Workspaces;

/// <summary>
/// The pictures of workspaces: each one drawn once into a 64 px PNG and kept in the data folder
/// (INV-03), named by its content. A copy rather than a path into the project: the rail must not read
/// a repository on every start (a share may be asleep), and a moved or edited logo must not change it.
/// SVG is drawn with SharpVectors (ADR-0039); everything WPF decodes (PNG, ICO, JPEG, GIF, BMP) natively.
/// </summary>
public sealed class WorkspaceIconStore
{
    /// <summary>The side of a stored picture: sharp on the rail's 20 px tile up to 300 % scaling.</summary>
    public const int PixelSize = 64;

    private readonly string _folder;
    private readonly ConcurrentDictionary<string, ImageSource> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceIconStore(string folder) => _folder = folder;

    /// <summary>The store every window uses, in the data folder.</summary>
    public static WorkspaceIconStore Default { get; } =
        new(global::TCV.Core.Domain.AppPaths.Data("workspace-icons"));

    /// <summary>
    /// The stored picture, or null when there is none or it cannot be read (a workspace imported from
    /// another machine names a file this one does not have). Small and read once, then cached: a
    /// content-named file never changes. A missing one is not remembered: a guest window may write it.
    /// </summary>
    public ImageSource? Load(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        if (_loaded.TryGetValue(fileName, out var cached))
        {
            return cached;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(File.ReadAllBytes(Path.Combine(_folder, fileName)));
            bitmap.EndInit();
            bitmap.Freeze();
            return _loaded.GetOrAdd(fileName, bitmap);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Draws the picture at <paramref name="path"/> into a square of <see cref="PixelSize"/>, fitted and
    /// centred; null for a file that is not a picture or cannot be read. On a thread of its own: WPF draws
    /// only on STA threads, and a large SVG must not hold the window (INV-06).
    /// </summary>
    public static Task<DrawnPicture?> RenderAsync(string path) => RunSta(() => Render(path));

    /// <summary>Writes a picture drawn by <see cref="RenderAsync"/> into the store; its file name, or null.</summary>
    public Task<string?> SaveAsync(BitmapSource picture) => RunSta(() => Save(picture));

    /// <summary>Takes away pictures no workspace names, older than <paramref name="minAge"/>: a picture
    /// just chosen in a guest window's dialog is not in the file yet.</summary>
    public void Sweep(IEnumerable<string> used, TimeSpan minAge)
    {
        var keep = used.Where(n => !string.IsNullOrWhiteSpace(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!Directory.Exists(_folder))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_folder, "*.png"))
            {
                if (!keep.Contains(Path.GetFileName(file)) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > minAge)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left for the next start.
        }
    }

    private string? Save(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        var bytes = buffer.ToArray();

        var name = Convert.ToHexString(SHA256.HashData(bytes))[..20].ToLowerInvariant() + ".png";
        var target = Path.Combine(_folder, name);
        try
        {
            Directory.CreateDirectory(_folder);
            if (!File.Exists(target))
            {
                var temp = target + ".tmp";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, target, overwrite: true);
            }
            else
            {
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);   // chosen again: young for the sweep
            }

            return name;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DrawnPicture? Render(string path)
    {
        try
        {
            var drawing = Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase)
                ? SvgDrawing(path)
                : RasterDrawing(path);
            if (drawing is null || drawing.Bounds.IsEmpty || drawing.Bounds.Width <= 0 || drawing.Bounds.Height <= 0)
            {
                return null;
            }

            var bounds = drawing.Bounds;
            var scale = PixelSize / Math.Max(bounds.Width, bounds.Height);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
            using (var context = visual.RenderOpen())
            {
                context.PushTransform(new TranslateTransform(
                    (PixelSize - bounds.Width * scale) / 2, (PixelSize - bounds.Height * scale) / 2));
                context.PushTransform(new ScaleTransform(scale, scale));
                context.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
                context.DrawDrawing(drawing);
            }

            var bitmap = new RenderTargetBitmap(PixelSize, PixelSize, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return new DrawnPicture(bitmap, Math.Max(bounds.Width, bounds.Height) / Math.Min(bounds.Width, bounds.Height));
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // A broken picture, an SVG SharpVectors cannot read: not a candidate.
            return null;
        }
    }

    private static Drawing? SvgDrawing(string path)
    {
        var settings = new WpfDrawingSettings { IncludeRuntime = false, TextAsGeometry = true };
        using var reader = new FileSvgReader(settings);
        return reader.Read(path);
    }

    /// <summary>The largest frame: an .ico holds 16 px to 256 px, and the first is often the smallest.</summary>
    private static Drawing? RasterDrawing(string path)
    {
        using var stream = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(
            stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.MaxBy(f => f.PixelWidth * f.PixelHeight);
        return frame is null ? null : new ImageDrawing(frame, new Rect(0, 0, frame.PixelWidth, frame.PixelHeight));
    }

    private static Task<T> RunSta<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
            finally
            {
                // The visual and the bitmap gave this thread a dispatcher (and its hidden window): shut
                // it down, or every picture drawn leaves one behind.
                System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "Workspace picture",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}

/// <summary>
/// A picture drawn for the rail, with the proportion of what was drawn: a wide wordmark or banner shrinks
/// to a sliver on the rail's 22 px tile, so the search takes only a picture near square.
/// </summary>
public sealed record DrawnPicture(BitmapSource Bitmap, double Aspect)
{
    /// <summary>Longer side over shorter side, at most, for a picture the search takes by itself.</summary>
    public const double MaxSquarishAspect = 1.5;

    public bool IsSquarish => Aspect <= MaxSquarishAspect;
}
