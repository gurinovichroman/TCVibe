using System.IO;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.ViewModels;

/// <summary>How an automatic reconnect of a panel ended.</summary>
/// <param name="Outcome">Connected (a try's listing came back, or a refresh found the old connection alive), gave up, refused, or cancelled.</param>
/// <param name="Connection">The connection it was about.</param>
/// <param name="Attempts">How many tries were made.</param>
/// <param name="LastError">Why the last try failed; null after a success.</param>
/// <param name="StoppedByUser">The user stopped it (rather than leaving the connection).</param>
/// <param name="StillOnConnection">The panel still stands on that connection: an error may be shown in it.</param>
public sealed record ReconnectFinished(
    ReconnectOutcome Outcome,
    ConnectionInfo Connection,
    int Attempts,
    Exception? LastError,
    bool StoppedByUser,
    bool StillOnConnection);

/// <summary>
/// Opens a panel's dropped connection again by itself (6.82). It watches the panel's listings: one
/// that failed on the server the tab stands on, with a failure another connection may fix
/// (<see cref="ReconnectPolicy"/>), starts a <see cref="ReconnectLoop"/>. Each try opens a fresh
/// connection with what was open, checks the folder, puts it in place of the dead one under the tab
/// (<see cref="FileListViewModel.ReplaceRemoteFileSystem"/>) and is judged by the listing it reads.
/// It stops at the first listing that comes back, at a refusal — a changed host key stays a refusal
/// (TRAP-43) — when the tries are used up, when the user stops it, or when the panel leaves the
/// connection (another tab, Disconnect, a workspace switch).
/// </summary>
/// <remarks>
/// No WPF and no dialogs: what to show is the owner's business (<see cref="ProgressChanged"/>,
/// <see cref="Finished"/>). The connection is opened by the owner's delegate, which must keep network
/// work off the UI thread (INV-06) and never open a browser or ask anything.
/// </remarks>
public sealed class PanelReconnector
{
    private readonly FileListViewModel _fileList;
    private readonly Func<ConnectionInfo, CancellationToken, Task<IVirtualFileSystem>> _open;
    private readonly ReconnectPolicy _policy;
    private readonly Func<TimeSpan, CancellationToken, Task>? _delay;
    private Session? _session;

    /// <param name="fileList">The panel's list.</param>
    /// <param name="open">Opens a fresh file system for a connection (not yet checked or mounted).</param>
    /// <param name="policy">Pauses and tries; <see cref="ReconnectPolicy.Default"/> when null.</param>
    /// <param name="delay">The pause, for tests; <see cref="Task.Delay(TimeSpan, CancellationToken)"/> when null.</param>
    public PanelReconnector(
        FileListViewModel fileList,
        Func<ConnectionInfo, CancellationToken, Task<IVirtualFileSystem>> open,
        ReconnectPolicy? policy = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(fileList);
        ArgumentNullException.ThrowIfNull(open);
        _fileList = fileList;
        _open = open;
        _policy = policy ?? ReconnectPolicy.Default;
        _delay = delay;

        fileList.ListingCompleted += OnListingCompleted;
        fileList.MountChanged += OnMountChanged;
    }

    /// <summary>Raised before each pause and each try, on the thread the reconnect runs on.</summary>
    public event Action<ReconnectProgress>? ProgressChanged;

    /// <summary>Raised once a reconnect has ended, after <see cref="IsRunning"/> turned false.</summary>
    public event Action<ReconnectFinished>? Finished;

    /// <summary>A reconnect is running.</summary>
    public bool IsRunning => _session is not null;

    /// <summary>What the running reconnect is doing; null when none runs.</summary>
    public ReconnectProgress? Progress => _session?.Progress;

    /// <summary>The connection being reconnected; null when none.</summary>
    public ConnectionInfo? Connection => _session?.Connection;

    /// <summary>The running reconnect, for tests to await; null when none.</summary>
    public Task? Running => _session?.Task;

    /// <summary>Ends the current pause now. False when no reconnect runs.</summary>
    public bool RetryNow()
    {
        if (_session is not { } session)
        {
            return false;
        }

        session.Loop.RetryNow();
        return true;
    }

    /// <summary>Stops the reconnect; the error stays on screen. False when none runs.</summary>
    public bool Stop()
    {
        if (_session is not { } session)
        {
            return false;
        }

        session.StoppedByUser = true;
        session.Cts.Cancel();
        End(session, ReconnectOutcome.Cancelled);
        return true;
    }

    /// <summary>Stops quietly: the window is closing.</summary>
    public void Cancel() => _session?.Cts.Cancel();

    private sealed class Session(ConnectionInfo connection, ReconnectLoop loop)
    {
        public ConnectionInfo Connection { get; } = connection;
        public ReconnectLoop Loop { get; } = loop;
        public CancellationTokenSource Cts { get; } = new();
        public ReconnectProgress? Progress { get; set; }
        public Task? Task { get; set; }

        /// <summary>The fresh connection is being put in place: that mount change is ours, not the user leaving.</summary>
        public bool Swapping { get; set; }

        /// <summary>A try waits for the listing on the fresh connection: its error, or null when it was read.</summary>
        public TaskCompletionSource<Exception?>? Listing { get; set; }

        /// <summary>A refresh by the user read the folder meanwhile: the old connection came back by itself.</summary>
        public bool CameBack { get; set; }

        public bool StoppedByUser { get; set; }

        /// <summary>Finished was raised: the panel is done with it, whatever its loop still unwinds.</summary>
        public bool Ended { get; set; }
    }

    private bool OnTheConnection(ConnectionInfo connection) =>
        _fileList.IsRemote && ReferenceEquals(_fileList.RemoteConnection, connection);

    private void OnListingCompleted(object? sender, ListingCompletedEventArgs e)
    {
        if (_session is { } running)
        {
            if (running.Listing is { } listing)
            {
                running.Listing = null;
                listing.TrySetResult(e.Error);
            }
            else if (e.Error is null && OnTheConnection(running.Connection))
            {
                // The user refreshed while the loop waited, and the old connection answered.
                running.CameBack = true;
                running.Cts.Cancel();
                End(running, ReconnectOutcome.Connected);
            }

            return;
        }

        // Only a server the tab stands on directly: an archive opened from it reads a local copy, and a
        // folder that is forbidden or gone is no dropped connection.
        if (e.Error is null || !_fileList.IsRemote || _fileList.RemoteConnection is not { } connection
            || !ReconnectPolicy.IsWorthRetrying(e.Error))
        {
            return;
        }

        Start(connection, e.Error);
    }

    private void OnMountChanged(object? sender, EventArgs e)
    {
        if (_session is { Swapping: false } running && !OnTheConnection(running.Connection))
        {
            running.Cts.Cancel();
            End(running, ReconnectOutcome.Cancelled);
        }
    }

    private void Start(ConnectionInfo connection, Exception cause)
    {
        Session? session = null;
        var loop = new ReconnectLoop(_policy, (_, ct) => TryOnceAsync(session!, ct), _delay);
        session = new Session(connection, loop);
        loop.Progress += progress =>
        {
            session.Progress = progress;
            if (!session.Ended)
            {
                ProgressChanged?.Invoke(progress);
            }
        };

        _session = session;
        session.Task = RunAsync(session, cause);
    }

    private async Task RunAsync(Session session, Exception cause)
    {
        ReconnectOutcome outcome;
        try
        {
            outcome = await session.Loop.RunAsync(cause, session.Cts.Token);
        }
        finally
        {
            if (ReferenceEquals(_session, session))
            {
                _session = null;
            }

            session.Listing?.TrySetCanceled();
        }

        End(session, session.CameBack ? ReconnectOutcome.Connected : outcome);
        session.Cts.Dispose();
    }

    /// <summary>
    /// Ends a session for the panel, once: it stops counting as running and <see cref="Finished"/> is
    /// raised. Stop, leaving the connection and a refresh that found it alive end it at once rather than
    /// when its loop unwinds — a try can sit in a blocking connect for its whole timeout, and until then
    /// its status stayed on screen, Reconnect only "skipped the pause" of a cancelled loop and a new drop
    /// was not noticed. The cancelled loop checks its token before touching the panel again.
    /// </summary>
    private void End(Session session, ReconnectOutcome outcome)
    {
        if (session.Ended)
        {
            return;
        }

        session.Ended = true;
        if (ReferenceEquals(_session, session))
        {
            _session = null;
        }

        Finished?.Invoke(new ReconnectFinished(
            outcome, session.Connection, session.Loop.Attempts, outcome == ReconnectOutcome.Connected ? null : session.Loop.LastError,
            session.StoppedByUser, OnTheConnection(session.Connection)));
    }

    /// <summary>
    /// One try: a fresh connection checked on the folder the tab stands in, put in place of the dead
    /// one, and judged by the listing it reads — a stat alone can succeed where the listing fails.
    /// </summary>
    private async Task TryOnceAsync(Session session, CancellationToken ct)
    {
        var path = _fileList.CurrentPath;
        IVirtualFileSystem? vfs = await _open(session.Connection, ct);
        try
        {
            if (await vfs.GetEntryAsync(new VfsPath(path), ct) is null)
            {
                throw new DirectoryNotFoundException(
                    string.Format(Localization.LocalizationManager.Get("Conn_DirNotFound"), path));
            }

            ct.ThrowIfCancellationRequested();
            var listing = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.Listing = listing;
            session.Swapping = true;
            bool replaced;
            try
            {
                replaced = OnTheConnection(session.Connection) && _fileList.ReplaceRemoteFileSystem(vfs, path);
            }
            finally
            {
                session.Swapping = false;
            }

            if (!replaced)
            {
                session.Listing = null;
                session.Cts.Cancel();   // the tab is no longer on the server: nothing to reconnect
                ct.ThrowIfCancellationRequested();
            }

            vfs = null;   // the panel owns it now
            using (ct.Register(() => listing.TrySetCanceled(ct)))
            {
                if (await listing.Task is { } failed)
                {
                    throw failed;
                }
            }
        }
        finally
        {
            vfs?.Dispose();
        }
    }
}
