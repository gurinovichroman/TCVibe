using System.ComponentModel;
using System.IO;
using TCV.App.FileSystem.Native;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>
/// "Test connection" on a Windows share: opens it, looks for the folder, and closes again what it opened
/// itself, so a test leaves nothing behind under the saved account. A share that was connected before the
/// test is left alone: Windows keeps one connection per share however often it is asked (a second connect
/// changes nothing, and one disconnect ends it), so closing "ours" would cut off whoever opened it.
/// </summary>
/// <remarks>
/// Both steps block for as long as the server takes to answer (INV-06), and Cancel does not wait for them
/// (TRAP-96). The connect given up on comes back later and closes what it opened then, unless the share is
/// wanted by that time: after a Cancel the user often connects at once (<paramref name="isWanted"/>) or tests
/// again. In that last case the share may stay connected, as a share opened by a panel does; never the
/// other way round.
/// </remarks>
/// <param name="isConnected">Whether Windows holds a connection to the share; true when that cannot be told.</param>
/// <param name="connect">Opens the share (root, user, password). Blocks.</param>
/// <param name="disconnect">Closes the share. Blocks; refuses with a <see cref="Win32Exception"/> while it is in use.</param>
/// <param name="folderExists">Whether the folder is there. Blocks.</param>
/// <param name="isWanted">Whether something else needs the share by now. Called on the caller's thread.</param>
public sealed class ShareConnectionTest(
    Func<string, bool> isConnected,
    Action<string, string?, string?> connect,
    Action<string> disconnect,
    Func<string, bool> folderExists,
    Func<string, bool>? isWanted = null)
{
    /// <summary>The shares of tests still being waited for.</summary>
    private readonly List<string> _running = [];

    /// <summary>The test against Windows itself.</summary>
    public static ShareConnectionTest OnThisComputer(Func<string, bool>? isWanted = null) =>
        new(Mpr.IsConnected, Mpr.Connect, Mpr.Disconnect, Directory.Exists, isWanted);

    /// <summary>Whether <paramref name="folder"/> can be reached through <paramref name="root"/> with this account.</summary>
    /// <exception cref="Win32Exception">Windows refused the connection.</exception>
    /// <exception cref="OperationCanceledException">Cancelled, whether or not the server has answered.</exception>
    public async Task<bool> FolderExistsAsync(string root, string folder, string? username, string? password, CancellationToken ct)
    {
        var caller = SynchronizationContext.Current;
        lock (_running)
        {
            _running.Add(root);
        }

        var opened = false;
        try
        {
            opened = await ConnectWait.UntilCancelledAsync(
                Task.Run(() =>
                {
                    // Asked first: connecting succeeds just the same on a share that is already connected.
                    var wasConnected = isConnected(root);
                    connect(root, username, password);
                    return !wasConnected;
                }, ct),
                ct,
                mine =>
                {
                    if (mine)
                    {
                        OnCaller(caller, () => _ = CloseAsync(root, CancellationToken.None));
                    }
                });

            return await ConnectWait.UntilCancelledAsync(Task.Run(() => folderExists(folder), ct), ct);
        }
        finally
        {
            lock (_running)
            {
                _running.Remove(root);
            }

            if (opened)
            {
                await CloseAsync(root, ct);
            }
        }
    }

    /// <summary>Closes off the caller's thread. The test ends with it, so a connect right after does not race it; a Cancel does not wait.</summary>
    private async Task CloseAsync(string root, CancellationToken ct)
    {
        bool stillTested;
        lock (_running)
        {
            stillTested = _running.Contains(root);
        }

        if (stillTested || isWanted?.Invoke(root) == true)
        {
            return;
        }

        var closing = Task.Run(() =>
        {
            try
            {
                disconnect(root);
            }
            catch (Win32Exception)
            {
                // In use by now, so it is someone's: it stays. The test's answer does not depend on it.
            }
        }, CancellationToken.None);

        if (!ct.IsCancellationRequested)
        {
            await closing.WaitAsync(ct);
        }
    }

    private static void OnCaller(SynchronizationContext? caller, Action action)
    {
        if (caller is null)
        {
            action();
        }
        else
        {
            caller.Post(_ => action(), null);
        }
    }
}
