using System.Diagnostics;
using System.IO;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Services;

/// <summary>What a fetch is for: the same file may be opened and edited at once, each with a copy of its own.</summary>
public enum RemoteFetchPurpose
{
    /// <summary>Enter: a scratch copy handed to the associated program.</summary>
    Open,
    /// <summary>F4: a watched copy that is uploaded on every save.</summary>
    Edit,
}

/// <summary>
/// Fetching one file of a server or an archive to the disk, as Enter and F4 do (ADR-0038): off the UI
/// thread, reporting progress, and leaving nothing behind when it is stopped or fails.
/// </summary>
public static class RemoteFetch
{
    private const int BufferSize = 128 * 1024;

    /// <summary>A progress window updates this often at most: the bar needs no more, the dispatcher does not want more.</summary>
    private static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Copies <paramref name="path"/> to <paramref name="target"/> on the thread pool (INV-06: a server's
    /// open is a connection, and some libraries block). The partial file goes when it is cancelled or fails.
    /// </summary>
    /// <param name="total">The size if known, else 0: the bar then only says how much has come.</param>
    /// <param name="name">What the progress names.</param>
    public static Task CopyToFileAsync(
        IVirtualFileSystem vfs, VfsPath path, string target, long total, string name,
        IProgress<FileProgress>? progress, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            progress?.Report(new FileProgress(0, total, name, TimeSpan.Zero));
            try
            {
                await using var source = await vfs.OpenReadAsync(path, ct).ConfigureAwait(false);
                await using var file = new FileStream(
                    target, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

                var buffer = new byte[BufferSize];
                var copied = 0L;
                var lastReport = TimeSpan.Zero;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    copied += read;
                    if (clock.Elapsed - lastReport >= ReportEvery)
                    {
                        lastReport = clock.Elapsed;
                        progress?.Report(new FileProgress(copied, Math.Max(total, copied), name, clock.Elapsed));
                    }
                }

                progress?.Report(new FileProgress(copied, Math.Max(total, copied), name, clock.Elapsed));
            }
            catch
            {
                TryDelete(target);
                throw;
            }
        }, ct);

    /// <summary>Removes a scratch folder and what is in it, as far as Windows lets go of it.</summary>
    public static void DeleteFolder(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A scanner still has the file: the temp folder is best effort by nature.
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept with its folder (TemporaryFiles, or the next start).
        }
    }
}

/// <summary>
/// The fetches under way, one per file and purpose: a second Enter on a file already on its way brings
/// its window back instead of starting another download. UI thread only.
/// </summary>
public sealed class RemoteFetchTracker
{
    private readonly Dictionary<(IVirtualFileSystem Fs, string Path, RemoteFetchPurpose Purpose), RemoteFetchTicket> _running = [];

    /// <summary>How many are under way.</summary>
    public int Count => _running.Count;

    /// <summary>A ticket for this fetch, or null when the same one is under way already (<see cref="Find"/> gives it).</summary>
    public RemoteFetchTicket? TryBegin(IVirtualFileSystem fs, VfsPath path, RemoteFetchPurpose purpose)
    {
        var key = (fs, path.Value, purpose);
        if (_running.ContainsKey(key))
        {
            return null;
        }

        var ticket = new RemoteFetchTicket(() => _running.Remove(key));
        _running[key] = ticket;
        return ticket;
    }

    /// <summary>The fetch under way for this file, or null.</summary>
    public RemoteFetchTicket? Find(IVirtualFileSystem fs, VfsPath path, RemoteFetchPurpose purpose) =>
        _running.GetValueOrDefault((fs, path.Value, purpose));

    /// <summary>Stops every fetch: the window is closing, and a scratch copy has no use after that.</summary>
    public void CancelAll()
    {
        foreach (var ticket in _running.Values.ToList())
        {
            ticket.Cancel();
        }
    }
}

/// <summary>
/// One fetch under way: its token, and its progress window once there is one. Taking it starts the
/// count; disposing it ends it. A window appears only for a fetch that takes a while, so progress is
/// kept here until then and handed over whole (<see cref="Attach"/>).
/// </summary>
public sealed class RemoteFetchTicket : IProgress<FileProgress>, IDisposable
{
    private readonly Action _end;
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _gate = new();
    private FileProgress? _last;
    private IProgress<FileProgress>? _sink;

    internal RemoteFetchTicket(Action end) => _end = end;

    /// <summary>Cancelled by the window's button, or by the exit.</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>The window showing it; null while it has not been up long enough to need one.</summary>
    public ProgressDialogViewModel? Window { get; private set; }

    /// <summary>Stops the fetch.</summary>
    public void Cancel() => _cts.Cancel();

    /// <summary>Called from whichever thread the copy is on: the window marshals, the rest is kept for it.</summary>
    public void Report(FileProgress value)
    {
        IProgress<FileProgress>? sink;
        lock (_gate)
        {
            _last = value;
            sink = _sink;
        }

        sink?.Report(value);
    }

    /// <summary>The window is up: it hears what happened so far and everything from now on, and its Cancel stops the fetch.</summary>
    public void Attach(ProgressDialogViewModel window)
    {
        Window = window;
        window.CanPause = false;
        window.Cts.Token.Register(_cts.Cancel);

        FileProgress? last;
        lock (_gate)
        {
            last = _last;
            _sink = window;
        }

        if (last is not null)
        {
            ((IProgress<FileProgress>)window).Report(last);
        }
    }

    /// <summary>
    /// The fetch is over: a next Enter on this file fetches it again. The token source is left to the
    /// collector: a window closing later still calls its Cancel, and it holds nothing to release.
    /// </summary>
    public void Dispose() => _end();
}
