using System.ComponentModel;
using System.IO;
using TCV.Core.Domain;

namespace TCV.App.FileSystem;

public sealed class FileWatcher : IDisposable
{
    private readonly Func<string, bool> _directoryExists;
    private readonly List<Task> _leaving = [];
    private FileSystemWatcher? _watcher;
    private int _generation;
    private System.Timers.Timer? _debounceTimer;
    private VfsChangedEventArgs? _pendingEvent;
    private readonly object _lock = new();
    private const int DebounceMs = 300;

    public FileWatcher()
        : this(Directory.Exists)
    {
    }

    /// <param name="directoryExists">Whether a folder exists; the place a share that stopped answering blocks, for tests.</param>
    public FileWatcher(Func<string, bool> directoryExists)
    {
        ArgumentNullException.ThrowIfNull(directoryExists);
        _directoryExists = directoryExists;
    }

    public event EventHandler<VfsChangedEventArgs>? Changed;

    /// <summary>The last start under way, for tests to await.</summary>
    public Task Starting { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Completes when no handle is left on a folder this watcher was told to leave. Leaving does not wait
    /// for that (a share that stopped answering can hold the close up), and until then Windows counts a
    /// share as in use and refuses to close it (TRAP-96).
    /// </summary>
    public Task Released
    {
        get
        {
            lock (_lock)
            {
                return Task.WhenAll(_leaving);
            }
        }
    }

    /// <summary>
    /// Watches a folder from now on. It starts in the pool: asking whether the folder exists and opening
    /// its handle block for as long as a share that stopped answering takes to time out, and this is
    /// called on the UI thread for every folder opened. The window froze for 21 s, with the Cancel under
    /// "Loading..." out of reach (INV-06, TRAP-96). A start overtaken by a newer one, or by
    /// <see cref="Stop"/>, opens nothing more and closes what it opened.
    /// </summary>
    public void Watch(string directoryPath)
    {
        var generation = Leave();
        Starting = Task.Run(() => Start(directoryPath, generation));
    }

    private bool IsOvertaken(int generation)
    {
        lock (_lock)
        {
            return generation != _generation;
        }
    }

    private void Start(string directoryPath, int generation)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            // Asked again after the answer: it can take as long as a dead share does, and a folder left
            // meanwhile is not worth blocking on once more to open a handle that is closed right away.
            if (IsOvertaken(generation) || !_directoryExists(directoryPath) || IsOvertaken(generation))
            {
                return;
            }

            watcher = new FileSystemWatcher(directoryPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
            };

            watcher.Created += OnFsChanged;
            watcher.Deleted += OnFsChanged;
            watcher.Changed += OnFsChanged;
            watcher.Renamed += OnFsRenamed;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception)
        {
            // The folder went, or the server refused: it just does not refresh by itself.
            if (watcher is not null)
            {
                TakeOffEvents(watcher);
                watcher.Dispose();
            }

            return;
        }

        lock (_lock)
        {
            if (generation == _generation)
            {
                _watcher = watcher;
                return;
            }
        }

        TakeOffEvents(watcher);
        watcher.Dispose();
    }

    public void Stop() => Leave();

    /// <summary>
    /// Lets go of the folder: its watcher is off the events now, its handle closes in the pool, which a
    /// dead share can hold up as well, and a start under way is overtaken (<see cref="Released"/>).
    /// </summary>
    /// <returns>The number a start after this one runs under.</returns>
    private int Leave()
    {
        lock (_lock)
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingEvent = null;

            _leaving.RemoveAll(task => task.IsCompleted);
            _leaving.Add(Starting);
            if (_watcher is { } watcher)
            {
                _watcher = null;
                TakeOffEvents(watcher);
                _leaving.Add(Task.Run(watcher.Dispose));
            }

            return ++_generation;
        }
    }

    private void TakeOffEvents(FileSystemWatcher watcher)
    {
        watcher.Created -= OnFsChanged;
        watcher.Deleted -= OnFsChanged;
        watcher.Changed -= OnFsChanged;
        watcher.Renamed -= OnFsRenamed;
        watcher.Error -= OnError;
    }

    private void OnFsChanged(object sender, FileSystemEventArgs e) =>
        Debounce(new VfsChangedEventArgs(new VfsPath(e.FullPath), e.ChangeType));

    private void OnFsRenamed(object sender, RenamedEventArgs e) =>
        Debounce(new VfsChangedEventArgs(new VfsPath(e.FullPath), WatcherChangeTypes.Renamed));

    private void OnError(object sender, ErrorEventArgs e) { }

    private void Debounce(VfsChangedEventArgs args)
    {
        lock (_lock)
        {
            _pendingEvent = args;

            if (_debounceTimer is null)
            {
                _debounceTimer = new System.Timers.Timer(DebounceMs) { AutoReset = false };
                _debounceTimer.Elapsed += (_, _) =>
                {
                    VfsChangedEventArgs? ev;
                    lock (_lock)
                    {
                        ev = _pendingEvent;
                        _pendingEvent = null;
                    }
                    if (ev is not null) Changed?.Invoke(this, ev);
                };
            }

            _debounceTimer.Stop();
            _debounceTimer.Start();
        }
    }

    public void Dispose() => Stop();
}
