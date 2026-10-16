using System.Runtime.InteropServices;

namespace TCV.App.FileSystem.Native;

internal static class ClipboardApi
{
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    /// <summary>
    /// A number the system bumps every time the clipboard changes, whoever changed it. Comparing it
    /// with the value seen when TCV filled its own file buffer tells whether the buffer still is what
    /// the user copied last.
    /// </summary>
    internal static long SequenceNumber() => GetClipboardSequenceNumber();
}
