using System.IO;
using TCV.Core.Domain;

namespace TCV.App.FileSystem;

public sealed class FileWatcher : IDisposable
{
    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounceTimer;
    private VfsChangedEventArgs? _pendingEvent;
    private readonly object _lock = new();
    private const int DebounceMs = 300;

    public event EventHandler<VfsChangedEventArgs>? Changed;

    public void Watch(string directoryPath)
    {
        Stop();
        if (!Directory.Exists(directoryPath)) return;

        _watcher = new FileSystemWatcher(directoryPath)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                           NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };

        _watcher.Created += OnFsChanged;
        _watcher.Deleted += OnFsChanged;
        _watcher.Changed += OnFsChanged;
        _watcher.Renamed += OnFsRenamed;
        _watcher.Error += OnError;
    }

    public void Stop()
    {
        lock (_lock)
        {
            _debounceTimer?.Stop();
            _debounceTimer?.Dispose();
            _debounceTimer = null;
            _pendingEvent = null;
        }

        _watcher?.Dispose();
        _watcher = null;
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
