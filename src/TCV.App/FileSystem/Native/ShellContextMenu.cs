using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace TCV.App.FileSystem.Native;

/// <summary>What came of a shell menu: nothing picked, a command the shell ran, or a verb handed back.</summary>
/// <param name="Picked">A command was chosen (false: the menu was dismissed).</param>
/// <param name="OwnVerb">The verb the caller claimed; the shell ran nothing. Null when the shell ran it.</param>
public readonly record struct ShellMenuResult(bool Picked, string? OwnVerb)
{
    public static ShellMenuResult Dismissed => new(false, null);
}

/// <summary>
/// Shows the native Windows Shell context menu for files on the disk, or for a folder's empty space.
/// Uses IContextMenu COM interop. Verbs the caller claims (see <see cref="ShellMenuVerbs"/>) are
/// handed back instead of run.
/// </summary>
public static class ShellContextMenu
{
    // ── COM interfaces ────────────────────────────────────────────────────────

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    // IContextMenu2 and 3 fill submenus on demand (Send to, Open with, New) and draw their icons.
    // COM interop does not inherit: the base methods are declared again, in vtable order.
    [ComImport, Guid("000214F4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu2
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport, Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IContextMenu3
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);

        [PreserveSig]
        int InvokeCommand(ref CMINVOKECOMMANDINFOEX pici);

        [PreserveSig]
        int GetCommandString(UIntPtr idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);

        [PreserveSig]
        int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);

        [PreserveSig]
        int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);

        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);

        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);

        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);

        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);

        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);

        // apidl = pointer to an array of child PIDLs; allocated via Marshal.AllocHGlobal
        [PreserveSig]
        int GetUIObjectOf(IntPtr hwndOwner, uint cidl, IntPtr apidl,
            ref Guid riid, uint rgfReserved, out IntPtr ppv);

        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);

        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public uint cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        public IntPtr lpTitleW;
        public POINT ptInvoke;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint CMF_EXPLORE       = 0x00000004;
    private const uint CMF_CANRENAME     = 0x00000010;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;

    private const uint CMIC_MASK_UNICODE      = 0x00004000;
    private const uint CMIC_MASK_SHIFT_DOWN   = 0x10000000;
    private const uint CMIC_MASK_PTINVOKE     = 0x20000000;
    private const uint CMIC_MASK_CONTROL_DOWN = 0x40000000;
    private const uint SW_SHOWNORMAL    = 1;

    private const uint GCS_VERBW = 0x00000004;

    private const int WM_DRAWITEM      = 0x002B;
    private const int WM_MEASUREITEM   = 0x002C;
    private const int WM_INITMENUPOPUP = 0x0117;
    private const int WM_MENUCHAR      = 0x0120;

    // Command ids handed to QueryContextMenu; the shell numbers its items from the first one.
    private const uint FirstCommandId = 1;
    private const uint LastCommandId  = 0x7FFF;

    [DllImport("shell32.dll")]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    // Returns the full absolute PIDL for a path
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    // Binds to the parent folder of a PIDL and returns the parent IShellFolder +
    // a pointer to the child's single-level PIDL (valid as long as pidlFull is alive)
    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        out IntPtr ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hmenu, uint fuFlags,
        int x, int y, IntPtr hwnd, IntPtr lptpm);

    private const uint TPM_RETURNCMD  = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows the Windows Shell context menu for the given file paths at the screen position. All of
    /// them must be in one folder (TRAP-63). The menu has Rename, as Explorer's does: the shell leaves
    /// it out unless the host says it can rename (<c>CMF_CANRENAME</c>), and here the caller does.
    /// </summary>
    /// <param name="extended">Shift was held: the shell adds its extended verbs (Copy as path, Open PowerShell here).</param>
    /// <param name="ownsVerb">Claims a verb for the caller, who then runs it instead of the shell.</param>
    public static ShellMenuResult ShowForItems(
        IReadOnlyList<string> filePaths, Window owner, Point screenPoint, bool extended, Predicate<string> ownsVerb)
    {
        if (filePaths.Count == 0)
        {
            return ShellMenuResult.Dismissed;
        }

        var hwnd = new WindowInteropHelper(owner).Handle;

        // Full PIDLs for all items — kept alive until we free them
        var fullPidls = new List<IntPtr>();

        // Child (single-level) PIDLs — point inside fullPidls memory, do NOT free separately
        var childPidls = new List<IntPtr>();

        IShellFolder? parentFolder = null;
        var sfGuid = typeof(IShellFolder).GUID;
        IntPtr apidlBlock = IntPtr.Zero;

        try
        {
            foreach (var path in filePaths)
            {
                var full = ILCreateFromPathW(path);
                if (full == IntPtr.Zero)
                {
                    continue;
                }

                var hr = SHBindToParent(full, ref sfGuid, out var pParent, out var pChild);
                if (hr != 0)
                {
                    ILFree(full);
                    continue;
                }

                fullPidls.Add(full);
                childPidls.Add(pChild); // pChild points inside full — valid while full is alive

                // All items must share the same parent folder; use the first one. The wrapper holds
                // its own reference, so the one SHBindToParent returned is released either way.
                parentFolder ??= (IShellFolder)Marshal.GetObjectForIUnknown(pParent);
                Marshal.Release(pParent);
            }

            if (childPidls.Count == 0 || parentFolder is null)
            {
                return ShellMenuResult.Dismissed;
            }

            // Build a contiguous array of child PIDL pointers in unmanaged memory
            // so that GetUIObjectOf receives a proper LPCITEMIDLIST* array
            int ptrSize = IntPtr.Size;
            apidlBlock = Marshal.AllocHGlobal(ptrSize * childPidls.Count);
            for (int i = 0; i < childPidls.Count; i++)
            {
                Marshal.WriteIntPtr(apidlBlock, i * ptrSize, childPidls[i]);
            }

            // Get IContextMenu
            var ctxGuid = typeof(IContextMenu).GUID;
            var hr2 = parentFolder.GetUIObjectOf(hwnd, (uint)childPidls.Count, apidlBlock,
                ref ctxGuid, 0, out var pCtx);
            if (hr2 != 0 || pCtx == IntPtr.Zero)
            {
                return ShellMenuResult.Dismissed;
            }

            var ctxMenu = (IContextMenu)Marshal.GetObjectForIUnknown(pCtx);
            Marshal.Release(pCtx);

            return Track(ctxMenu, hwnd, screenPoint, CMF_CANRENAME | (extended ? CMF_EXTENDEDVERBS : 0), ownsVerb);
        }
        finally
        {
            if (apidlBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(apidlBlock);
            }

            foreach (var p in fullPidls)
            {
                ILFree(p);
            }

            if (parentFolder is not null)
            {
                Marshal.ReleaseComObject(parentFolder);
            }
        }
    }

    /// <summary>
    /// Shows the menu of a folder's empty space, as Explorer does on a right-click below the files:
    /// Paste, New ▸, Properties and what extensions add there (Open in Terminal). Not the folder's
    /// own menu, whose Delete and Rename would act on the folder the user is standing in.
    /// </summary>
    public static ShellMenuResult ShowForFolderBackground(
        string folder, Window owner, Point screenPoint, bool extended, Predicate<string> ownsVerb)
    {
        var hwnd = new WindowInteropHelper(owner).Handle;
        var pidl = ILCreateFromPathW(folder);
        if (pidl == IntPtr.Zero)
        {
            return ShellMenuResult.Dismissed;
        }

        IShellFolder? desktop = null;
        IShellFolder? shellFolder = null;
        try
        {
            if (SHGetDesktopFolder(out desktop) != 0)
            {
                return ShellMenuResult.Dismissed;
            }

            var sfGuid = typeof(IShellFolder).GUID;
            desktop.BindToObject(pidl, IntPtr.Zero, ref sfGuid, out var pFolder);
            shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
            Marshal.Release(pFolder);

            var ctxGuid = typeof(IContextMenu).GUID;
            shellFolder.CreateViewObject(hwnd, ref ctxGuid, out var pCtx);
            if (pCtx == IntPtr.Zero)
            {
                return ShellMenuResult.Dismissed;
            }

            var ctxMenu = (IContextMenu)Marshal.GetObjectForIUnknown(pCtx);
            Marshal.Release(pCtx);

            return Track(ctxMenu, hwnd, screenPoint, extended ? CMF_EXTENDEDVERBS : 0, ownsVerb);
        }
        catch (Exception ex) when (ex is COMException or System.IO.IOException or UnauthorizedAccessException
                                       or ArgumentException or NotImplementedException)
        {
            // A folder the shell cannot bind to (gone, denied, or a path it does not parse): no menu.
            // Interop throws the well-known HRESULTs as their own types (0x80070002 as
            // FileNotFoundException, E_ACCESSDENIED, E_INVALIDARG, E_NOTIMPL), not as COMException.
            return ShellMenuResult.Dismissed;
        }
        finally
        {
            ILFree(pidl);
            if (shellFolder is not null)
            {
                Marshal.ReleaseComObject(shellFolder);
            }

            if (desktop is not null)
            {
                Marshal.ReleaseComObject(desktop);
            }
        }
    }

    // ── Showing the menu ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds the menu, shows it and runs the picked command — unless the caller claims its verb.
    /// Releases <paramref name="ctxMenu"/>.
    /// </summary>
    private static ShellMenuResult Track(IContextMenu ctxMenu, IntPtr hwnd, Point screenPoint, uint flags, Predicate<string> ownsVerb)
    {
        var hMenu = CreatePopupMenu();

        // Send to, Open with and New are empty until their handler sees WM_INITMENUPOPUP, and their
        // icons are owner-drawn: the messages reach the window that owns the menu, which passes them on.
        var menu2 = ctxMenu as IContextMenu2;
        var menu3 = ctxMenu as IContextMenu3;
        var source = HwndSource.FromHwnd(hwnd);
        HwndSourceHook hook = (IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            ForwardMenuMessage(menu2, menu3, msg, wParam, lParam, ref handled);

        try
        {
            if (ctxMenu.QueryContextMenu(hMenu, 0, FirstCommandId, LastCommandId, CMF_EXPLORE | flags) < 0)
            {
                return ShellMenuResult.Dismissed;
            }

            uint cmd;
            source?.AddHook(hook);
            try
            {
                cmd = TrackPopupMenuEx(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    (int)screenPoint.X, (int)screenPoint.Y, hwnd, IntPtr.Zero);
            }
            finally
            {
                source?.RemoveHook(hook);
            }

            if (cmd < FirstCommandId)
            {
                return ShellMenuResult.Dismissed;
            }

            var offset = cmd - FirstCommandId;
            if (VerbOf(ctxMenu, offset) is { } verb && ownsVerb(verb))
            {
                return new ShellMenuResult(true, verb);
            }

            var modifiers = Keyboard.Modifiers;
            var invoke = new CMINVOKECOMMANDINFOEX
            {
                cbSize  = (uint)Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask   = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE
                          | (modifiers.HasFlag(ModifierKeys.Shift) ? CMIC_MASK_SHIFT_DOWN : 0)
                          | (modifiers.HasFlag(ModifierKeys.Control) ? CMIC_MASK_CONTROL_DOWN : 0),
                hwnd    = hwnd,
                lpVerb  = (IntPtr)offset,
                lpVerbW = (IntPtr)offset,
                nShow   = (int)SW_SHOWNORMAL,
                ptInvoke = new POINT { X = (int)screenPoint.X, Y = (int)screenPoint.Y },
            };
            ctxMenu.InvokeCommand(ref invoke);
            return new ShellMenuResult(true, null);
        }
        finally
        {
            DestroyMenu(hMenu);
            Marshal.ReleaseComObject(ctxMenu);   // menu2 and menu3 are the same object
        }
    }

    private static IntPtr ForwardMenuMessage(
        IContextMenu2? menu2, IContextMenu3? menu3, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        var forMenu = msg switch
        {
            WM_INITMENUPOPUP or WM_MENUCHAR => true,
            WM_DRAWITEM or WM_MEASUREITEM => wParam == IntPtr.Zero,   // zero: sent for a menu, not a control
            _ => false,
        };
        if (!forMenu)
        {
            return IntPtr.Zero;
        }

        if (menu3 is not null)
        {
            if (menu3.HandleMenuMsg2((uint)msg, wParam, lParam, out var result) == 0)
            {
                handled = true;
                return result;
            }
        }
        else if (menu2 is not null && msg != WM_MENUCHAR && menu2.HandleMenuMsg((uint)msg, wParam, lParam) == 0)
        {
            handled = true;
        }

        return IntPtr.Zero;
    }

    /// <summary>The canonical verb of a menu command ("rename", "delete"), or null when it has none.</summary>
    private static string? VerbOf(IContextMenu ctxMenu, uint offset)
    {
        const int capacity = 256;
        var buffer = Marshal.AllocCoTaskMem((capacity + 1) * sizeof(char));
        try
        {
            // Some handlers succeed without writing anything, and some fill the buffer to the brim.
            Marshal.WriteInt16(buffer, 0);
            Marshal.WriteInt16(buffer, capacity * sizeof(char), 0);
            if (ctxMenu.GetCommandString((UIntPtr)offset, GCS_VERBW, IntPtr.Zero, buffer, capacity) != 0)
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer) is { Length: > 0 } verb ? verb : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }
}
