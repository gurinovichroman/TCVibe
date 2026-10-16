using System.Diagnostics;
using System.IO;

namespace TCV.App.Services;

/// <summary>
/// The other processes of this very program: the workspace windows (TRAP-22), which run the same exe.
/// Velopack's updater kills whatever runs from <c>current\</c> (TRAP-72), so before an update they
/// are asked to close the normal way and save what they hold.
/// </summary>
public static class SiblingWindows
{
    /// <summary>The ids among <paramref name="processes"/> that run <paramref name="exePath"/>, this process aside.</summary>
    public static IReadOnlyList<int> Pick(IEnumerable<(int Id, string? Path)> processes, int selfId, string exePath)
    {
        var exe = Path.GetFullPath(exePath);
        return processes
            .Where(p => p.Id != selfId && p.Path is not null
                        && string.Equals(Path.GetFullPath(p.Path), exe, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToList();
    }

    /// <summary>
    /// Asks every sibling window to close, as its close button would, and waits up to
    /// <paramref name="timeout"/>. Returns how many still run: one with work in progress asks its user
    /// first and may stay, and none is ever killed. Blocks — call it off the UI thread.
    /// </summary>
    public static int CloseAll(TimeSpan timeout)
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return 0;
        }

        var candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        try
        {
            var ids = Pick(candidates.Select(p => (p.Id, PathOf(p))), Environment.ProcessId, exe);
            var siblings = candidates.Where(p => ids.Contains(p.Id)).ToList();
            foreach (var sibling in siblings)
            {
                sibling.CloseMainWindow();
            }

            var deadline = DateTime.UtcNow + timeout;
            var stillRunning = 0;
            foreach (var sibling in siblings)
            {
                var left = deadline - DateTime.UtcNow;
                if (!sibling.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero))
                {
                    stillRunning++;
                }
            }

            return stillRunning;
        }
        finally
        {
            foreach (var process in candidates)
            {
                process.Dispose();
            }
        }
    }

    private static string? PathOf(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Gone meanwhile, or not ours to inspect: either way not a window of this copy.
            return null;
        }
    }
}
