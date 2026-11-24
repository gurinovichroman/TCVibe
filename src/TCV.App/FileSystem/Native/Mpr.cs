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
    private const int ErrorNoMoreItems = 259;
    private const int ResourceConnected = 0x00000001;
    private const int ResourcetypeAny = 0x00000000;

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

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetOpenEnum(int scope, int type, int usage, IntPtr netResource, out IntPtr handle);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetEnumResource(IntPtr handle, ref int count, IntPtr buffer, ref int bufferSize);

    [DllImport("mpr.dll")]
    private static extern int WNetCloseEnum(IntPtr handle);

    /// <summary>
    /// Whether this logon session holds a connection to <paramref name="uncRoot"/>, with or without a drive
    /// letter. Not being able to tell counts as yes: the callers use it to leave a connection alone.
    /// Touches no network, but call it off the UI thread with the connect it comes before (INV-06).
    /// </summary>
    public static bool IsConnected(string uncRoot)
    {
        if (WNetOpenEnum(ResourceConnected, ResourcetypeAny, 0, IntPtr.Zero, out var handle) != 0)
        {
            return true;
        }

        const int BufferSize = 16 * 1024;
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            while (true)
            {
                var count = -1;
                var size = BufferSize;
                var error = WNetEnumResource(handle, ref count, buffer, ref size);
                if (error == ErrorNoMoreItems)
                {
                    return false;
                }

                if (error != 0)
                {
                    return true;
                }

                for (var i = 0; i < count; i++)
                {
                    var resource = Marshal.PtrToStructure<NETRESOURCE>(buffer + (i * Marshal.SizeOf<NETRESOURCE>()));
                    if (string.Equals(resource.lpRemoteName, uncRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            _ = WNetCloseEnum(handle);
        }
    }

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
