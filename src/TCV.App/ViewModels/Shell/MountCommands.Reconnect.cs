using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TCV.App.Commands;
using TCV.App.Localization;
using TCV.App.Services;
using TCV.Core.Interfaces;
using TCV.Network;

namespace TCV.App.ViewModels;

/// <summary>
/// Connections that look after themselves (6.82): a dropped one is opened again with growing pauses
/// instead of waiting for the user to press Reconnect (<see cref="PanelReconnector"/>); the ones
/// background tabs keep are pinged so the server does not close them; and the connections opened
/// last are offered in the palette.
/// </summary>
public sealed partial class MountCommands
{
    /// <summary>One automatic reconnect per panel, for the tab on screen.</summary>
    private readonly Dictionary<PanelViewModel, PanelReconnector> _reconnectors = [];

    /// <summary>Ticks once a second while a reconnect waits, so "next try in N s" counts down.</summary>
    private DispatcherTimer? _reconnectClock;

    private DispatcherTimer? _keepAliveTimer;
    private readonly CancellationTokenSource _keepAliveStop = new();
    private bool _keepAliveRunning;

    /// <summary>Gives a panel its automatic reconnect: a listing that fails on a dropped connection starts it.</summary>
    private void WatchConnection(PanelViewModel panel)
    {
        var reconnector = new PanelReconnector(panel.FileList, OpenForReconnectAsync);
        reconnector.ProgressChanged += progress =>
        {
            ShowReconnectProgress(panel, reconnector);
            UpdateReconnectClock();
        };
        reconnector.Finished += finished => OnReconnectFinished(panel, finished);
        _reconnectors[panel] = reconnector;
    }

    /// <summary>
    /// A fresh file system for a dropped connection, with what was open (the session's password
    /// included). Never opens a browser: a drive that needs a new sign-in is a refusal.
    /// </summary>
    private async Task<IVirtualFileSystem> OpenForReconnectAsync(ConnectionInfo info, CancellationToken ct)
    {
        info.SecretsChanged = PersistSecrets;

        // Off the UI thread: an SFTP key file is parsed and decrypted here (INV-06).
        var vfs = await Task.Run(() => NetworkPlugin.CreateFileSystem(info), ct);
        if (vfs is IInteractiveSignIn { NeedsSignIn: true })
        {
            vfs.Dispose();
            throw new SignInRequiredException(LocalizationManager.Get("Conn_Protocol_" + info.Protocol));
        }

        return info.ReadOnly ? new ReadOnlyFileSystem(vfs) : vfs;
    }

    private void ShowReconnectProgress(PanelViewModel panel, PanelReconnector reconnector)
    {
        if (reconnector.Progress is not { } progress || reconnector.Connection is not { } connection)
        {
            return;
        }

        var title = PanelViewModel.ConnectionTitle(connection);
        panel.FileList.ReconnectStatus = progress.IsWaiting
            ? LocalizationManager.Format("Conn_ReconnectWaiting", title,
                Math.Max(0, (int)Math.Ceiling((progress.NextAttemptAt - DateTime.UtcNow).TotalSeconds)),
                progress.Attempt, progress.MaxAttempts,
                progress.LastError is null ? string.Empty : NetworkErrorText.Describe(progress.LastError))
            : LocalizationManager.Format("Conn_ReconnectTrying", title, progress.Attempt, progress.MaxAttempts);
    }

    /// <summary>
    /// The reconnect ended: a notice when the connection is back; the reason in the list, with
    /// Reconnect and Disconnect under it, when it gave up or was refused — a changed host key is
    /// shown as it is (TRAP-43), never asked about.
    /// </summary>
    private void OnReconnectFinished(PanelViewModel panel, ReconnectFinished finished)
    {
        // The reason goes in before the status comes off: clearing the status re-reads the error.
        ReportReconnectOutcome(panel, finished);
        panel.FileList.ReconnectStatus = null;
        UpdateReconnectClock();
    }

    private void ReportReconnectOutcome(PanelViewModel panel, ReconnectFinished finished)
    {
        var connection = finished.Connection;
        var title = PanelViewModel.ConnectionTitle(connection);
        var reason = finished.LastError is null ? string.Empty : NetworkErrorText.Describe(finished.LastError);
        switch (finished.Outcome)
        {
            case ReconnectOutcome.Connected:
                _logger.LogInformation("Reconnected to {Host} after {Attempts} attempt(s)", connection.Host, finished.Attempts);
                _shell.ShowNotice(LocalizationManager.Format("Conn_Reconnected", title), TimeSpan.FromSeconds(5));
                break;

            case ReconnectOutcome.GaveUp when finished.StillOnConnection:
                _logger.LogWarning(finished.LastError, "Gave up reconnecting to {Host}", connection.Host);
                panel.FileList.LoadError = LocalizationManager.Format("Conn_ReconnectGaveUp", title, reason, finished.Attempts);
                break;

            case ReconnectOutcome.Refused when finished.StillOnConnection:
                _logger.LogWarning(finished.LastError, "Reconnecting to {Host} was refused", connection.Host);
                panel.FileList.LoadError = LocalizationManager.Format("Conn_ReconnectRefused", title, reason);
                break;

            default:
                _logger.LogInformation("Stopped reconnecting to {Host} ({Reason})", connection.Host,
                    finished.StoppedByUser ? "by the user" : "the panel left the connection");
                break;
        }
    }

    /// <summary>Runs the countdown clock while any reconnect is waiting, and stops it when none is.</summary>
    private void UpdateReconnectClock()
    {
        var waiting = _reconnectors.Values.Any(r => r.Progress is { IsWaiting: true });
        if (waiting && _reconnectClock is null)
        {
            _reconnectClock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _reconnectClock.Tick += (_, _) =>
            {
                foreach (var (panel, reconnector) in _reconnectors)
                {
                    if (reconnector.Progress is { IsWaiting: true })
                    {
                        ShowReconnectProgress(panel, reconnector);
                    }
                }

                UpdateReconnectClock();
            };
            _reconnectClock.Start();
        }
        else if (!waiting && _reconnectClock is not null)
        {
            _reconnectClock.Stop();
            _reconnectClock = null;
        }
    }

    /// <summary>Skips the pause of the panel's automatic reconnect; false when none runs there.</summary>
    private bool RetryReconnectNow(PanelViewModel panel) =>
        _reconnectors.TryGetValue(panel, out var reconnector) && reconnector.RetryNow();

    /// <summary>
    /// Stops a panel's automatic reconnect (the chip's ×, the button under the error): the error stays,
    /// with Reconnect and Disconnect under it. The active panel's when none is given.
    /// </summary>
    [RelayCommand]
    private void StopReconnect(PanelViewModel? panel)
    {
        if (_reconnectors.TryGetValue(panel ?? ActivePanel, out var reconnector))
        {
            reconnector.Stop();
        }
    }

    private void StopAllReconnects()
    {
        foreach (var reconnector in _reconnectors.Values)
        {
            reconnector.Cancel();
        }
    }

    // ── Keep-alive of every tab's connection ──────────────────────────────────

    /// <summary>Starts pinging the open connections now and then, the parked ones of background tabs included.</summary>
    private void StartKeepAlive()
    {
        _keepAliveTimer = new DispatcherTimer { Interval = KeepAlivePolicy.CheckInterval };
        _keepAliveTimer.Tick += (_, _) => _ = KeepAliveRoundAsync();
        _keepAliveTimer.Start();
    }

    private async Task KeepAliveRoundAsync()
    {
        if (_keepAliveRunning)
        {
            return;   // the last round is still waiting for a slow server
        }

        _keepAliveRunning = true;
        try
        {
            var targets = new List<KeepAliveTarget>();
            foreach (var panel in new[] { _shell.LeftPanel, _shell.RightPanel })
            {
                foreach (var vfs in panel.OpenFileSystems())
                {
                    targets.Add(new KeepAliveTarget(vfs, () => panel.FileList.LeaseFileSystem(vfs)));
                }
            }

            await ConnectionKeepAlive.PingAsync(targets, _logger, _keepAliveStop.Token);
        }
        finally
        {
            _keepAliveRunning = false;
        }
    }

    private void StopKeepAlive()
    {
        _keepAliveTimer?.Stop();
        _keepAliveTimer = null;
        _keepAliveStop.Cancel();
    }

    // ── Recent connections in the palette ─────────────────────────────────────

    /// <summary>
    /// The connections opened last, as palette rows that open them in the active panel ("Connect:
    /// prod"), the most recent first; the address beside each. Empty when none was ever opened.
    /// </summary>
    public IReadOnlyList<CommandPaletteItem> RecentConnectionPaletteItems()
    {
        var recent = RecentConnections.Pick(_connectionBook.Connections);
        return recent
            .Select((info, i) => new CommandPaletteItem(
                RecentConnections.CommandId(info),
                LocalizationManager.Format("Conn_PaletteTitle", PanelViewModel.ConnectionTitle(info)),
                info.DescribeAddress(info.IsObjectStorage ? "/" : info.RemotePath),
                string.Empty,
                Rank: recent.Count - i))
            .ToList();
    }

    /// <summary>Opens a recent connection picked in the palette; false when the id is no connection's.</summary>
    public bool TryOpenRecentConnection(string commandId)
    {
        if (RecentConnections.ParseCommandId(commandId) is not { } id
            || _connectionBook.Connections.FirstOrDefault(c => c.Id == id) is not { } info)
        {
            return false;
        }

        _ = ConnectAsync(info, ActivePanel);
        return true;
    }

    /// <summary>Notes that a saved connection was just opened: the palette lists it first next time.</summary>
    private void RememberUse(ConnectionInfo info)
    {
        try
        {
            _connectionBook.MarkUsed(info.Id, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not note the use of {Name}", info.Name);
        }
    }
}
