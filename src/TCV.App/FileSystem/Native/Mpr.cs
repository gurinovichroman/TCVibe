using System.ComponentModel;
using System.Runtime.InteropServices;

namespace TCV.App.FileSystem.Native;

/// <summary>
/// Windows network connections (<c>mpr.dll</c>): a share opened with a saved user and password, so
/// the panel can then read <c>\\host\share</c> as an ordinary disk path.
/// </summary>
internal static class Mpr
{
    private const int ResourcetypeDisk = 0x00000001;
    private const int ConnectTemporary = 0x00000004;
    private const int ErrorNotConnected = 2250;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NETRESOURCE
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;
        public string? lpLocalName;
        public string? lpRemoteName;
        public string? lpComment;
        public string? lpProvider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NETRESOURCE netResource, string? password, string? username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    /// <summary>
    /// Opens <paramref name="uncRoot"/> (<c>\\host\share</c>) for this logon session with the given
    /// account; a null user means the current Windows account. Temporary: not restored at next logon.
    /// Blocks for as long as the server takes to answer — call it off the UI thread (INV-06).
    /// </summary>
    public static void Connect(string uncRoot, string? username, string? password)
    {
        var resource = new NETRESOURCE { dwType = ResourcetypeDisk, lpRemoteName = uncRoot };
        var error = WNetAddConnection2(ref resource,
            string.IsNullOrEmpty(password) ? null : password,
            string.IsNullOrEmpty(username) ? null : username,
            ConnectTemporary);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }
    }

    /// <summary>Closes the connection to <paramref name="uncRoot"/>; one that is already gone is no error.</summary>
    public static void Disconnect(string uncRoot)
    {
        var error = WNetCancelConnection2(uncRoot, 0, force: false);
        if (error != 0 && error != ErrorNotConnected)
        {
            throw new Win32Exception(error);
        }
    }
}
