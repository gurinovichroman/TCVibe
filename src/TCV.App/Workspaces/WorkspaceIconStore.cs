using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
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

    private const int NameHexLength = 20;

    private readonly string _folder;
    private readonly ConcurrentDictionary<string, ImageSource> _loaded = new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceIconStore(string folder) => _folder = folder;

    /// <summary>The store every window uses, in the data folder.</summary>
    public static WorkspaceIconStore Default { get; } =
        new(global::TCV.Core.Domain.AppPaths.Data("workspace-icons"));

    /// <summary>
    /// How the store names a picture: the start of the hash of its content. Nothing else is a stored
    /// picture, so a name read from a file from elsewhere (<c>..\..\x.png</c>, <c>a.png:stream</c>,
    /// <c>CON</c>) stops here and never reaches a path.
    /// </summary>
    public static bool IsStoredName([NotNullWhen(true)] string? fileName)
    {
        if (fileName is not { Length: NameHexLength + 4 } || !fileName.EndsWith(".png", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in fileName.AsSpan(0, NameHexLength))
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The stored picture, or null when there is none or it cannot be read (a workspace imported from
    /// another machine names a file this one does not have). Small and read once, then cached and shared
    /// by everything that shows it: a content-named file never changes, and it is decoded at the size the
    /// store keeps whatever lies in the folder. A missing one is not remembered: a guest window may write it.
    /// </summary>
    public ImageSource? Load(string? fileName)
    {
        if (!IsStoredName(fileName))
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
            bitmap.DecodePixelWidth = PixelSize;
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

    /// <summary>The picture if it is already in memory; never goes to the disk, so a menu being built can ask (INV-06).</summary>
    public ImageSource? Peek(string? fileName) =>
        fileName is not null && _loaded.TryGetValue(fileName, out var cached) ? cached : null;

    /// <summary>
    /// <see cref="Load"/> off the UI thread. A file that is not there costs no thread of its own: a
    /// workspace imported from another machine asks every time its menu is opened.
    /// </summary>
    public async Task<ImageSource?> LoadAsync(string? fileName)
    {
        if (!IsStoredName(fileName))
        {
            return null;
        }

        if (Peek(fileName) is { } cached)
        {
            return cached;
        }

        var path = Path.Combine(_folder, fileName);
        return await Task.Run(() => File.Exists(path)) ? await RunSta(() => Load(fileName)) : null;
    }

    /// <summary>Reads what is not in memory yet, one thread for all of them, so that lists opened later find it there.</summary>
    public async Task PreloadAsync(IEnumerable<string?> fileNames)
    {
        var wanted = fileNames
            .Where(n => IsStoredName(n) && Peek(n) is null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        var present = await Task.Run(() => wanted.Where(n => File.Exists(Path.Combine(_folder, n!))).ToList());
        if (present.Count > 0)
        {
            await RunSta(() =>
            {
                foreach (var name in present)
                {
                    Load(name);
                }

                return true;
            });
        }
    }

    /// <summary>
    /// A stored picture as text for a file that leaves this machine; null when there is none to give:
    /// no such name, no such file, or a file past what travels (<see cref="WorkspacePictureData"/>).
    /// </summary>
    public string? ReadForTransfer(string? fileName)
    {
        if (!IsStoredName(fileName))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(Path.Combine(_folder, fileName));
            return file is { Exists: true, Length: <= WorkspacePictureData.MaxBytes }
                ? WorkspacePictureData.ToText(File.ReadAllBytes(file.FullName))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The pictures of a settings bundle by the names the workspaces hold, each once, up to
    /// <see cref="WorkspacePictureData.MaxBundleChars"/> in the order given; how many stayed out.
    /// </summary>
    public ExportedPictures ReadAllForTransfer(IEnumerable<string?> fileNames)
    {
        var pictures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var total = 0;
        var leftOut = 0;
        foreach (var name in fileNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (ReadForTransfer(name) is { } text && total + text.Length <= WorkspacePictureData.MaxBundleChars)
            {
                pictures[name!] = text;
                total += text.Length;
            }
            else
            {
                leftOut++;
            }
        }

        return new ExportedPictures(pictures, leftOut);
    }

    /// <summary>
    /// Takes a picture a file carried into the store; its file name, or null when it was refused. The name
    /// comes from the content, never from the file. Written by whichever window opened the file: the store
    /// is shared and named by content, so a guest window (TRAP-22) overwrites nothing of anybody's.
    /// </summary>
    public Task<string?> ImportAsync(string? text) =>
        WorkspacePictureData.FromText(text) is { } png
            ? RunSta(() => Import(png))
            : Task.FromResult<string?>(null);

    /// <summary>
    /// Takes the pictures of a settings bundle and leaves each workspace naming what is really in the
    /// store: the picture the bundle carried for it, else a name of this store that is on this disk,
    /// else none, so that its project can be searched as for a workspace that never had one. How many
    /// pictures were refused. The names in the file are only keys here: none of them reaches a path.
    /// </summary>
    public Task<int> ImportAllAsync(IEnumerable<Workspace?> workspaces, IReadOnlyDictionary<string, string>? pictures) =>
        RunSta(() =>
        {
            var refused = 0;
            foreach (var workspace in workspaces)
            {
                if (workspace is null)
                {
                    continue;
                }

                var named = workspace.IconImage ?? string.Empty;
                if (pictures is not null && pictures.TryGetValue(named, out var text))
                {
                    var name = WorkspacePictureData.FromText(text) is { } png ? Import(png) : null;
                    refused += name is null ? 1 : 0;
                    workspace.IconImage = name ?? string.Empty;
                }
                else
                {
                    workspace.IconImage = IsStoredName(named) && File.Exists(Path.Combine(_folder, named))
                        ? named
                        : string.Empty;
                }
            }

            return refused;
        });

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

    private static byte[] Encode(BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var buffer = new MemoryStream();
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    private static string NameOf(byte[] png) =>
        Convert.ToHexString(SHA256.HashData(png))[..NameHexLength].ToLowerInvariant() + ".png";

    /// <summary>
    /// Decoded and written anew, so what lies in the store holds pixels and nothing else a file put after
    /// them. The pixel format stays as decoded: the same picture encodes to the same bytes and so to the
    /// same name, whichever machine it comes back from.
    /// </summary>
    private string? Import(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            var decoder = BitmapDecoder.Create(
                stream, BitmapCreateOptions.IgnoreColorProfile | BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            return decoder is PngBitmapDecoder && decoder.Frames.Count == 1
                   && decoder.Frames[0] is { PixelWidth: PixelSize, PixelHeight: PixelSize } frame
                ? Save(frame)
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // The header looked right and the rest does not decode: refused like any other.
            return null;
        }
    }

    private string? Save(BitmapSource picture)
    {
        var bytes = Encode(picture);
        var name = NameOf(bytes);
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
            return new DrawnPicture(
                bitmap, Math.Max(bounds.Width, bounds.Height) / Math.Min(bounds.Width, bounds.Height), NameOf(Encode(bitmap)));
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

/// <summary>What <see cref="WorkspaceIconStore.ReadAllForTransfer"/> gives a settings bundle, and how many stayed out.</summary>
public sealed record ExportedPictures(Dictionary<string, string> Pictures, int LeftOut);

/// <summary>
/// A picture drawn for the rail, with the proportion of what was drawn: a wide wordmark or banner shrinks
/// to a sliver on the rail's 22 px tile, so the search takes only a picture near square. <see cref="FileName"/>
/// is the name the store keeps it under: how the properties window knows which of the pictures found in the
/// project is the one the workspace wears.
/// </summary>
public sealed record DrawnPicture(BitmapSource Bitmap, double Aspect, string FileName)
{
    /// <summary>Longer side over shorter side, at most, for a picture the search takes by itself.</summary>
    public const double MaxSquarishAspect = 1.5;

    public bool IsSquarish => Aspect <= MaxSquarishAspect;
}
